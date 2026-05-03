using System;
using System.Runtime.InteropServices;
using System.Threading;
using Vomplayer.Controls;
using Vomplayer.Mpv;

namespace Vomplayer.Wayland;

// Orchestrates the Wayland subsurface lifecycle against a Gtk.Window + VideoArea pair.
// Constructor is trivial — stores references and hooks window realize/unrealize + area geometry changes. EGL/subsurface work runs in OnRealize, by which time MainWindow has connected RenderContextReady / RenderFailed. Render loop: mpv update callback (mpv thread) → coalesced IdleAdd → main-thread Render = MakeCurrent + mpv_render_context_render + Swap + ReportSwap.
public sealed partial class VideoSurface : IDisposable, IHdrSink
{
    private const string GtkLib = "libgtk-4.so.1";

    [LibraryImport(GtkLib, EntryPoint = "gdk_wayland_display_get_wl_display")]
    private static partial IntPtr GdkWaylandDisplayGetWlDisplay(IntPtr display);

    [LibraryImport(GtkLib, EntryPoint = "gdk_wayland_surface_get_wl_surface")]
    private static partial IntPtr GdkWaylandSurfaceGetWlSurface(IntPtr surface);

    private readonly Gtk.Window window;
    private readonly VideoArea area;
    private VomplVideoSurface? surface;
    private MpvRenderContext? renderContext;
    private MpvDispatcher? dispatcher;
    // Cached for TryCreateRenderContext, which is also reachable via SetMpvDispatcher (post-realize). The wl_display is GDK-app-scope and remains valid across the render-context lifetime.
    private IntPtr wlDisplay;
    private int renderQueued;
    // Latched (via Interlocked) the first time mpv's update callback fires. Gates DoRender: before mpv has signaled any content, a render+swap commits the EGL back buffer's undefined contents to the subsurface — under KWin + SDR the compositor honors the back buffer's alpha and the (transparent) parent region shows the desktop through the video area. With this latch, the subsurface stays unmapped (no buffer ever attached) until mpv has real content, and the GTK placeholder overlay on the parent keeps the region opaque-black in the meantime. HDR masked this historically: the PQ image description on the subsurface changes the compositor's alpha handling so an undefined swap didn't punch through.
    private int mpvUpdateSignaled;
    private bool firstFrameRendered;
    private (int x, int y, int w, int h, int scale)? pendingGeometry;

    public event Action? RenderContextReady;
    public event Action<int>? RenderFailed;
    // Fires exactly once, on the main thread, after mpv has signaled content AND the first render+swap of that content completes. Deliberately NOT fired on pre-content renders (initial kickoff, geometry-change renders before any file is loaded) — those are gated out of DoRender so the subsurface stays unmapped. Consumers can use this to remove any "placeholder" they drew on the GTK side while the subsurface was empty.
    public event Action? FirstFrameRendered;
    // Fires when CurrentOutputIsHdr's observable value changes (null ↔ true, null ↔ false, true ↔ false). Deduped internally against a cached last-published value so a same-value re-notify (e.g. registry event for an output that's already known-SDR) does not fire. Sources: wl_surface.enter/leave mutating the active-output set, and wp_color_management_output_v1.image_description_changed propagating through WaylandOutputRegistry.
    public event Action? CurrentOutputHdrChanged;

    private bool? lastPublishedOutputIsHdr;

    // Stages an explicit image description on the subsurface: PQ/BT.2020 for enable=true, GAMMA22/BT.709 SDR for enable=false. The shim does NOT commit — the next mpv-driven Swap flushes it alongside the first new-content buffer, so tag-change and frame-change land atomically on the compositor. Returns 0 on success; -1 if the compositor does not advertise wp_color_manager_v1 or the subsurface is not yet realized. Caller must only enable mpv PQ targeting when this returns 0 with enable=true, else PQ-encoded output would hit an SDR-tagged surface. For enable=false, an SDR tag is preferable to untagged because per wp_color_management_v1 spec untagged surface handling is compositor-defined; on KWin with an HDR output present that compositor-defined handling blows out gamma22-encoded SDR output catastrophically (see hdr_helper.c). On compositors without wp_color_manager_v1 the surface stays untagged and -1 is returned — most compositors handle untagged-as-sRGB sensibly, so this is logged but tolerated.
    public int SetHdr(bool enable)
    {
        if (surface == null)
        {
            return -1;
        }
        return surface.SetHdr(enable);
    }

    // Read the current VRR classification from the shim's refresh-sample ring. Returns Unknown if the subsurface isn't ready yet.
    public VrrClassification GetVrrClassification()
    {
        if (surface == null)
        {
            return VrrClassification.Unknown;
        }
        return surface.GetVrrClassification();
    }

    // Measured mean presentation rate over the classifier's ring, expressed as centi-Hz (5000 = 50.00 Hz). 0 when the ring is empty / pre-warmup. Distinct from the panel's nominal mode rate; see FrameTimingBridge.MeasuredHzCenti for the rationale. Writer and reader are both on the GTK main thread (Wayland events pump through GTK's main loop), so no memory barrier is needed.
    public int VrrMeasuredHzCenti
    {
        get
        {
            if (surface == null)
            {
                return 0;
            }
            return surface.VrrMeasuredHzCenti;
        }
    }

    // Whether the output the subsurface is currently on advertises an HDR (PQ/HLG) preferred image description. null means unknown — either the surface has no active wl_output yet (pre-first-enter), the shim's HDR probe hasn't completed, or the compositor doesn't advertise wp_color_manager_v1. Callers should treat null as SDR for conservative defaults.
    //
    // Reuses FrameTimingBridge.ActiveOutput's first-wins convention: on a subsurface spanning multiple outputs, this reflects whichever output the compositor reported first in wl_surface.enter. TODO: on a span that covers an HDR panel + an SDR panel simultaneously, first-wins can report HDR while pixels on the SDR side receive a PQ-tagged surface (imperfect compositor tonemap-down). Safer future policy: return false if ANY entered output is SDR, true only when all entered outputs are HDR.
    public bool? CurrentOutputIsHdr
    {
        get
        {
            if (surface == null)
            {
                return null;
            }
            uint? active = surface.Bridge.ActiveOutput;
            if (!active.HasValue)
            {
                return null;
            }
            if (WaylandOutputRegistry.TryGetIsHdr(active.Value, out var isHdr))
            {
                return isHdr;
            }
            return null;
        }
    }

    public VideoSurface(Gtk.Window window, VideoArea area)
    {
        if (window == null)
        {
            throw new ArgumentNullException(nameof(window));
        }
        if (area == null)
        {
            throw new ArgumentNullException(nameof(area));
        }
        this.window = window;
        this.area = area;

        window.OnRealize += OnWindowRealize;
        window.OnUnrealize += OnWindowUnrealize;
        area.GeometryChanged += OnAreaGeometryChanged;

        // Static-event subscription — must be unhooked in Dispose or we'd leak this VideoSurface for the process lifetime. The Bridge-side subscription is hooked/unhooked with the VomplVideoSurface lifetime in TryCreateRenderContext / TearDown.
        WaylandOutputRegistry.IsHdrChanged += OnRegistryIsHdrChanged;

        // If the GTK window is already realized at construction time — the case for the lazily-created PiP secondary VideoSurface in MainWindow.EnablePip() — OnRealize will never fire, leaving `surface` null forever. Run the realize handler synchronously so the subsurface is created on this thread, before any caller can call into us. Primary VideoSurface (constructed pre-realize from MainWindow's ctor) takes the normal event-driven path.
        if (window.GetRealized())
        {
            OnWindowRealize(window, EventArgs.Empty);
        }
    }

    // Stack this subsurface above `other` in the parent's z-order. Both must be subsurfaces of the same parent (enforced by the underlying protocol; this wrapper just forwards). Used by MainWindow.EnablePip after constructing the secondary so the PiP composites above the primary's video buffer.
    public void PlaceAbove(VideoSurface other)
    {
        if (other == null)
        {
            throw new ArgumentNullException(nameof(other));
        }
        if (surface == null || other.surface == null)
        {
            return;
        }
        surface.PlaceAbove(other.surface);
    }

    // Stack this subsurface above the GTK parent main wl_surface (above any opaque GTK content on it). Used by MainWindow during the PiP-secondary-rendered-but-primary-not transient, so the secondary is visible despite the still-shown opaque noVideoBg on the parent. Restore via PlaceAbove(primary) when the primary first frame fires.
    public void PlaceAboveParent()
    {
        if (surface == null)
        {
            return;
        }
        surface.PlaceAbove(null);
    }

    // Called by MainWindow via playback.AttachRenderSurface(dispatcher => videoSurface.SetMpvDispatcher(dispatcher)). If the window is already realized, the render context is built now; otherwise the dispatcher is stashed and the render context is built on OnRealize. Internal because MpvDispatcher is internal.
    internal void SetMpvDispatcher(MpvDispatcher d)
    {
        if (d == null)
        {
            throw new ArgumentNullException(nameof(d));
        }
        dispatcher = d;
        if (surface != null && renderContext == null)
        {
            TryCreateRenderContext();
        }
    }

    private void OnWindowRealize(object? sender, EventArgs e)
    {
        var gdkSurface = window.GetSurface();
        if (gdkSurface == null)
        {
            Console.Error.WriteLine("[vomplayer] window has no GdkSurface at realize");
            RenderFailed?.Invoke(-1);
            return;
        }
        var gdkDisplay = gdkSurface.GetDisplay();
        if (gdkDisplay == null)
        {
            RenderFailed?.Invoke(-1);
            return;
        }
        wlDisplay = GdkWaylandDisplayGetWlDisplay(gdkDisplay.Handle.DangerousGetHandle());
        IntPtr wlSurface = GdkWaylandSurfaceGetWlSurface(gdkSurface.Handle.DangerousGetHandle());
        if (wlDisplay == IntPtr.Zero || wlSurface == IntPtr.Zero)
        {
            Console.Error.WriteLine("[vomplayer] Wayland display/surface unavailable — not on Wayland backend?");
            RenderFailed?.Invoke(-1);
            return;
        }

        int initialW = 1;
        int initialH = 1;
        int initialScale = 1;
        if (pendingGeometry.HasValue)
        {
            var g = pendingGeometry.Value;
            initialW = g.w > 0 ? g.w : 1;
            initialH = g.h > 0 ? g.h : 1;
            initialScale = g.scale > 0 ? g.scale : 1;
        }

        try
        {
            // Must register output callbacks before the shim's first ensure_globals call (triggered by Create below). The initial wl_output + mode events arrive during its two roundtrips; without the callbacks wired, WaylandOutputRegistry would miss them and the VRR classifier would stay Unknown.
            VomplOutputCallbacks.EnsureRegistered();
            surface = new VomplVideoSurface(wlDisplay, wlSurface, initialW, initialH, initialScale);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vomplayer] subsurface create failed: {ex.Message}");
            RenderFailed?.Invoke(-1);
            return;
        }
        // Note the ordering: ensure_globals (inside the VomplVideoSurface constructor above) synchronously pumps roundtrips. If the compositor fires wl_surface.enter during those roundtrips, it lands on the bridge BEFORE this subscription, so the first ActiveOutputsChanged that fires through us is for a later mutation. That's acceptable because CurrentOutputIsHdr.get reads bridge+registry state directly — ApplyHdrPolicy will see the correct combined value whenever it next runs (from SourceHdrChanged or a real output change).
        surface.Bridge.ActiveOutputsChanged += OnBridgeActiveOutputsChanged;

        if (pendingGeometry.HasValue)
        {
            var g = pendingGeometry.Value;
            surface.SetGeometry(g.x, g.y, g.w > 0 ? g.w : 1, g.h > 0 ? g.h : 1, g.scale > 0 ? g.scale : 1);
            pendingGeometry = null;
        }

        TryCreateRenderContext();

        // Area may already have computed its geometry before we got here.
        area.RefreshGeometry();
    }

    private void TryCreateRenderContext()
    {
        if (dispatcher == null || surface == null || renderContext != null)
        {
            return;
        }
        if (surface.MakeCurrent() < 0)
        {
            RenderFailed?.Invoke(-1);
            return;
        }
        try
        {
            // Pass the wl_display so mpv's vaapi hwdec driver can open a VADisplay via vaGetDisplayWl. Without it the driver's native-display probe fails across all fallbacks (x11 → wayland → drm) and mpv drops to vulkan-copy, costing a full GPU→RAM→GPU round-trip per frame.
            renderContext = dispatcher.CreateRenderContext(name => Epoxy.GetProcAddress(name), wlDisplay, IntPtr.Zero);
            renderContext.UpdateRequested += OnMpvUpdateRequested;
            renderContext.RenderFailed += OnMpvRenderFailed;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vomplayer] mpv render context creation failed: {ex.Message}");
            RenderFailed?.Invoke(-1);
            return;
        }
        RenderContextReady?.Invoke();
        // No kickoff QueueRender here — before mpv has signaled any content, DoRender early-returns (see mpvUpdateSignaled gate) and the native shim has already committed the subsurface bufferless. The first real render will be driven by OnMpvUpdateRequested once a file is loaded.
    }

    private void OnAreaGeometryChanged(int x, int y, int w, int h, int scale)
    {
        if (surface == null)
        {
            pendingGeometry = (x, y, w, h, scale);
            return;
        }
        pendingGeometry = null;
        surface.SetGeometry(x, y, w, h, scale);
        QueueRender();
    }

    // Runs on mpv's internal render thread. Coalesce + hop to main.
    private void OnMpvUpdateRequested()
    {
        Interlocked.Exchange(ref mpvUpdateSignaled, 1);
        QueueRender();
    }

    private void OnMpvRenderFailed(int code)
    {
        GLib.Functions.IdleAdd(
            (int)GLib.Constants.PRIORITY_DEFAULT_IDLE,
            () =>
            {
                RenderFailed?.Invoke(code);
                return false;
            });
    }

    // Thread-safe via Interlocked: callable from both mpv's render thread (OnMpvUpdateRequested) and the main thread (OnAreaGeometryChanged, TryCreateRenderContext). Idempotent — at most one IdleAdd in flight.
    private void QueueRender()
    {
        if (Interlocked.Exchange(ref renderQueued, 1) == 1)
        {
            return;
        }
        GLib.Functions.IdleAdd(
            (int)GLib.Constants.PRIORITY_DEFAULT_IDLE,
            () =>
            {
                Interlocked.Exchange(ref renderQueued, 0);
                DoRender();
                return false;
            });
    }

    private void DoRender()
    {
        // Guard the whole render against shim/mpv exceptions. Without this, an uncaught throw here unwinds into GLib's main loop and typically aborts the process; a one-shot failure should surface via RenderFailed, not kill the app.
        try
        {
            if (renderContext == null || surface == null)
            {
                return;
            }
            // Skip until mpv has real content. Other QueueRender callers (TryCreateRenderContext kickoff, OnAreaGeometryChanged) can fire arbitrarily early, and an eglSwapBuffers before mpv has rendered anything commits undefined alpha to the subsurface — the SDR regression behind this gate. surface.SetGeometry already ran synchronously in OnAreaGeometryChanged, so skipping the render here doesn't lose the resize.
            if (Volatile.Read(ref mpvUpdateSignaled) == 0)
            {
                return;
            }
            if (surface.MakeCurrent() < 0)
            {
                return;
            }
            var (bw, bh) = surface.GetBufferSize();
            renderContext.Render(0, bw, bh);
            surface.Swap();
            renderContext.ReportSwap();
            if (!firstFrameRendered)
            {
                firstFrameRendered = true;
                FirstFrameRendered?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vomplayer] DoRender threw: {ex}");
            RenderFailed?.Invoke(-1);
        }
    }

    private void OnWindowUnrealize(object? sender, EventArgs e)
    {
        TearDown();
    }

    private void TearDown()
    {
        if (surface != null)
        {
            surface.Bridge.ActiveOutputsChanged -= OnBridgeActiveOutputsChanged;
            // mpv_render_context_free needs the GL context current.
            surface.MakeCurrent();
        }
        if (renderContext != null)
        {
            renderContext.UpdateRequested -= OnMpvUpdateRequested;
            renderContext.RenderFailed -= OnMpvRenderFailed;
            renderContext.Dispose();
            renderContext = null;
        }
        surface?.Dispose();
        surface = null;
    }

    public void Dispose()
    {
        window.OnRealize -= OnWindowRealize;
        window.OnUnrealize -= OnWindowUnrealize;
        area.GeometryChanged -= OnAreaGeometryChanged;
        WaylandOutputRegistry.IsHdrChanged -= OnRegistryIsHdrChanged;
        TearDown();
    }

    private void OnRegistryIsHdrChanged(uint registryName)
    {
        // The registry fires for every output. Filter to the current active output before publishing, else a distant monitor's probe update would spuriously re-fire our event.
        uint? active = surface?.Bridge.ActiveOutput;
        if (!active.HasValue || active.Value != registryName)
        {
            return;
        }
        PublishOutputHdrIfChanged();
    }

    private void OnBridgeActiveOutputsChanged()
    {
        PublishOutputHdrIfChanged();
    }

    private void PublishOutputHdrIfChanged()
    {
        bool? current = CurrentOutputIsHdr;
        if (current == lastPublishedOutputIsHdr)
        {
            return;
        }
        lastPublishedOutputIsHdr = current;
        // Defer the invoke: this fires from Wayland dispatch on the main thread, and consumers (MainWindow.ApplyHdrPolicy) call mpv.SetProperty which synchronously waits on mpv's core thread. The core then waits on the render thread — which is *this* main thread. Synchronous invocation would deadlock the main loop. Punting to a fresh GMainContext iteration breaks the cycle, matching Playback.UpdateSourceHdr's postToMainThread pattern for SourceHdrChanged.
        GLib.Functions.IdleAdd(
            (int)GLib.Constants.PRIORITY_DEFAULT_IDLE,
            () =>
            {
                CurrentOutputHdrChanged?.Invoke();
                return false;
            });
    }
}
