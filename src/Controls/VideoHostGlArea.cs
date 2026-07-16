using System;
using Vomplayer.Wayland;

namespace Vomplayer.Controls;

// GLArea-fallback implementation of IVideoHost (X11 / non-Wayland backends): mpv renders into the VideoView's GTK-owned FBO. No Wayland surface, so WaylandSurface is null and HDR stays SDR-tonemapped per ARCHITECTURE.md.
public sealed class VideoHostGlArea : IVideoHost
{
    private readonly VideoView view;

    public event Action? RenderContextReady;
    public event Action<int>? RenderFailed;
    public event Action? GeometryChanged;

    // Never fires on this path — there is no post-swap hook on Gtk.GLArea, and nothing downstream needs one (no transparent-parent gap to unmask). Accessor-only so the compiler doesn't flag a never-raised event.
    public event Action? FirstFrameRendered
    {
        add
        {
        }
        remove
        {
        }
    }

    public VideoHostGlArea()
    {
        view = new VideoView();
        view.RenderContextReady += OnViewRenderContextReady;
        view.RenderFailed += OnViewRenderFailed;
        view.OnResize += OnViewResize;
    }

    public Gtk.Widget Widget
    {
        get
        {
            return view;
        }
    }

    public VideoSurface? WaylandSurface
    {
        get
        {
            return null;
        }
    }

    public void AttachPlayback(Playback.Playback playback)
    {
        playback.AttachRenderSurface(d => view.AttachDispatcher(d));
    }

    public void RefreshGeometry()
    {
        // No-op: GTK draws the FBO at the widget's actual screen position, so there's no separate surface position to keep in sync.
    }

    public void TeardownRenderSurface()
    {
        view.TeardownRenderContext();
    }

    private void OnViewRenderContextReady()
    {
        RenderContextReady?.Invoke();
    }

    private void OnViewRenderFailed(int code)
    {
        RenderFailed?.Invoke(code);
    }

    private void OnViewResize(Gtk.GLArea sender, Gtk.GLArea.ResizeSignalArgs args)
    {
        GeometryChanged?.Invoke();
    }
}
