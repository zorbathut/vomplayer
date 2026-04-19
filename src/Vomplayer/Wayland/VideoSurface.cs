using System;
using System.Runtime.InteropServices;
using System.Threading;
using Vomplayer.Controls;
using Vomplayer.Mpv;

namespace Vomplayer.Wayland;

// Orchestrates the Wayland subsurface lifecycle against a Gtk.Window + VideoArea pair.
// Constructor is trivial — stores references and hooks window realize/unrealize + area geometry changes. EGL/subsurface work runs in OnRealize, by which time MainWindow has connected RenderContextReady / RenderFailed. Render loop: mpv update callback (mpv thread) → coalesced IdleAdd → main-thread Render = MakeCurrent + mpv_render_context_render + Swap + ReportSwap.
public sealed partial class VideoSurface : IDisposable
{
    private const string GtkLib = "gtk-4";

    [LibraryImport(GtkLib, EntryPoint = "gdk_wayland_display_get_wl_display")]
    private static partial IntPtr GdkWaylandDisplayGetWlDisplay(IntPtr display);

    [LibraryImport(GtkLib, EntryPoint = "gdk_wayland_surface_get_wl_surface")]
    private static partial IntPtr GdkWaylandSurfaceGetWlSurface(IntPtr surface);

    private readonly Gtk.Window window;
    private readonly VideoArea area;
    private readonly bool hdrRequested;
    private VomVideoSurface? surface;
    private MpvRenderContext? renderContext;
    private MpvClient? client;
    private int renderQueued;
    private (int x, int y, int w, int h, int scale)? pendingGeometry;

    public event Action? RenderContextReady;
    public event Action<int>? RenderFailed;

    // True iff the subsurface has a PQ/BT.2020 image description successfully attached. Valid only after RenderContextReady fires.
    public bool HdrActive
    {
        get
        {
            return surface != null && surface.HdrActive;
        }
    }

    public VideoSurface(Gtk.Window window, VideoArea area, bool hdrRequested)
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
        this.hdrRequested = hdrRequested;

        window.OnRealize += OnWindowRealize;
        window.OnUnrealize += OnWindowUnrealize;
        area.GeometryChanged += OnAreaGeometryChanged;
    }

    // Called by MainWindow via playback.AttachRenderSurface(client => videoSurface.SetMpvClient(client)). If the window is already realized, the render context is built now; otherwise the client is stashed and the render context is built on OnRealize.
    public void SetMpvClient(MpvClient c)
    {
        if (c == null)
        {
            throw new ArgumentNullException(nameof(c));
        }
        client = c;
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
        IntPtr wlDisplay = GdkWaylandDisplayGetWlDisplay(gdkDisplay.Handle.DangerousGetHandle());
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
            surface = new VomVideoSurface(wlDisplay, wlSurface, initialW, initialH, initialScale, hdrRequested);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vomplayer] subsurface create failed: {ex.Message}");
            RenderFailed?.Invoke(-1);
            return;
        }

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
        if (client == null || surface == null || renderContext != null)
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
            renderContext = new MpvRenderContext(client, name => Epoxy.GetProcAddress(name));
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
        QueueRender();
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
            if (surface.MakeCurrent() < 0)
            {
                return;
            }
            var (bw, bh) = surface.GetBufferSize();
            renderContext.Render(0, bw, bh);
            surface.Swap();
            renderContext.ReportSwap();
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
        TearDown();
    }
}
