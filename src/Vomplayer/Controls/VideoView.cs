using System;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering;
using Avalonia.Threading;
using Vomplayer.Mpv;

// OpenGL-backed video surface. Avalonia owns the framebuffer; mpv renders into it each frame via mpv_render_context. Attach(MpvClient) is one-shot and must be called before the control is first drawn — OnOpenGlInit throws if no client has been attached.
namespace Vomplayer.Controls;

public class VideoView : OpenGlControlBase
{
    private MpvClient? client;
    private MpvRenderContext? renderContext;
    private Action? initializedHandlers;
    private bool isRenderContextLive;

    // Latch-and-replay: handlers subscribed after OnOpenGlInit has run still get invoked once. Raised on the UI thread. Fires once per OnOpenGlInit cycle — across detach/reattach (fullscreen, reparent) it will fire again for each new GL context.
    public event Action? RenderContextReady
    {
        add
        {
            initializedHandlers += value;
            if (isRenderContextLive && value != null)
            {
                Dispatcher.UIThread.Post(value);
            }
        }
        remove
        {
            initializedHandlers -= value;
        }
    }

    public event Action<int>? RenderFailed;

    internal void AttachClient(MpvClient client)
    {
        if (this.client != null)
        {
            throw new InvalidOperationException("VideoView already has a client attached.");
        }
        this.client = client ?? throw new ArgumentNullException(nameof(client));
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        if (client == null)
        {
            throw new InvalidOperationException("VideoView.Attach(MpvClient) must be called before the control is drawn.");
        }
        try
        {
            renderContext = new MpvRenderContext(client, gl.GetProcAddress);
            renderContext.UpdateRequested += OnMpvUpdateRequested;
            renderContext.RenderFailed += OnMpvRenderFailed;
            isRenderContextLive = true;

            var handler = initializedHandlers;
            if (handler != null)
            {
                Dispatcher.UIThread.Post(handler.Invoke);
            }
        }
        catch (Exception ex)
        {
            // Surface on the UI thread rather than letting it tear down Avalonia's render thread silently.
            Console.Error.WriteLine($"[vomplayer] mpv render context creation failed: {ex}");
            Dispatcher.UIThread.Post(() => RenderFailed?.Invoke(-1));
            throw;
        }
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (renderContext == null)
        {
            return;
        }
        // Avalonia 12 doesn't expose the FBO's pixel size publicly, so we derive it from Bounds * RenderScaling. Rounded to int — risks a one-pixel edge of undefined pixels at fractional scales. Revisit if a public PixelSize accessor lands.
        var bounds = Bounds;
        var scale = (VisualRoot as IPresentationSource)?.RenderScaling ?? 1.0;
        int width = Math.Max(1, (int)Math.Round(bounds.Width * scale));
        int height = Math.Max(1, (int)Math.Round(bounds.Height * scale));
        renderContext.Render(fb, width, height);
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        DisposeRenderContext();
    }

    protected override void OnOpenGlLost()
    {
        // GL context is gone; drop managed state but don't call the native free.
        if (renderContext != null)
        {
            renderContext.UpdateRequested -= OnMpvUpdateRequested;
            renderContext.RenderFailed -= OnMpvRenderFailed;
            renderContext.Abandon();
            renderContext = null;
        }
        isRenderContextLive = false;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Belt-and-braces teardown in case Avalonia raises window Closed before OnOpenGlDeinit.
        DisposeRenderContext();
        base.OnDetachedFromVisualTree(e);
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
        isRenderContextLive = false;
    }

    private void OnMpvUpdateRequested()
    {
        Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Background);
    }

    private void OnMpvRenderFailed(int code)
    {
        Dispatcher.UIThread.Post(() => RenderFailed?.Invoke(code));
    }
}
