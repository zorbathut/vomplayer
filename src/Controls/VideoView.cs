using System;
using Vomplayer.Mpv;

namespace Vomplayer.Controls;

// GTK4 GLArea that owns the mpv render context. AttachDispatcher is one-shot and must be called before the control is first realized. RenderContextReady fires on every successful OnRealize (not just the first) — GLArea cycles unrealize/realize on reparent, and the downstream VM's `OnRenderContextReady` has the one-shot latch for initial-file loading.
public class VideoView : Gtk.GLArea
{
    private MpvDispatcher? dispatcher;
    private MpvRenderContext? renderContext;

    public event Action? RenderContextReady;
    public event Action<int>? RenderFailed;

    public VideoView()
    {
        SetHexpand(true);
        SetVexpand(true);

        OnRealize += OnGlRealize;
        OnUnrealize += OnGlUnrealize;
        OnRender += OnGlRender;
    }

    internal void AttachDispatcher(MpvDispatcher dispatcher)
    {
        if (this.dispatcher != null)
        {
            throw new InvalidOperationException("VideoView already has a dispatcher attached.");
        }
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    private void OnGlRealize(object? sender, EventArgs e)
    {
        if (dispatcher == null)
        {
            throw new InvalidOperationException("VideoView.AttachDispatcher must be called before the control is realized.");
        }
        MakeCurrent();
        var err = GetError();
        if (err != null)
        {
            // Letting an exception escape OnRealize aborts GSignal dispatch and usually the process. Report via RenderFailed and leave renderContext null so OnRender short-circuits.
            Console.Error.WriteLine($"[vomplayer] GLArea realize error: {err.Message}");
            RenderFailed?.Invoke(-1);
            return;
        }

        try
        {
            // No native display handles plumbed through on the GLArea path — we don't reach into GDK for the underlying wl_display / X11 Display here. Consequence: hwdec backends that need native-display interop (e.g. vaapi's native-display probe) fall back or fail, and mpv may pick a copy-back backend. Acceptable for the fallback path; the Wayland subsurface path is where we care about zero-copy.
            renderContext = dispatcher.CreateRenderContext(name => Epoxy.GetProcAddress(name), IntPtr.Zero, IntPtr.Zero);
            renderContext.UpdateRequested += OnMpvUpdateRequested;
            renderContext.RenderFailed += OnMpvRenderFailed;
            RenderContextReady?.Invoke();
        }
        catch (Exception ex)
        {
            // Same contract as the GLArea error branch: report and return, don't rethrow.
            Console.Error.WriteLine($"[vomplayer] mpv render context creation failed: {ex}");
            RenderFailed?.Invoke(-1);
        }
    }

    private bool OnGlRender(Gtk.GLArea area, Gtk.GLArea.RenderSignalArgs args)
    {
        if (renderContext == null)
        {
            return false;
        }
        int fbo = Epoxy.GetCurrentDrawFbo();
        int width = Math.Max(1, GetAllocatedWidth() * GetScaleFactor());
        int height = Math.Max(1, GetAllocatedHeight() * GetScaleFactor());
        renderContext.Render(fbo, width, height);
        return true;
    }

    private void OnGlUnrealize(object? sender, EventArgs e)
    {
        DisposeRenderContext();
    }

    private void DisposeRenderContext()
    {
        if (renderContext == null)
        {
            return;
        }
        renderContext.UpdateRequested -= OnMpvUpdateRequested;
        renderContext.RenderFailed -= OnMpvRenderFailed;
        renderContext.Dispose();
        renderContext = null;
    }

    private void OnMpvUpdateRequested()
    {
        // mpv update callback fires on its internal render thread. Hop to the main thread before touching the widget.
        GLib.Functions.IdleAdd(
            (int)GLib.Constants.PRIORITY_DEFAULT,
            () =>
            {
                QueueRender();
                return false;
            });
    }

    private void OnMpvRenderFailed(int code)
    {
        GLib.Functions.IdleAdd(
            (int)GLib.Constants.PRIORITY_DEFAULT,
            () =>
            {
                RenderFailed?.Invoke(code);
                return false;
            });
    }
}
