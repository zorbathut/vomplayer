using System;
using Vomplayer.Wayland;

namespace Vomplayer.Controls;

// Wayland-subsurface implementation of IVideoHost: a VideoArea placeholder widget reserving layout space plus a VideoSurface managing the wl_subsurface mpv renders into. Construction order vs parenting doesn't matter: GTK allocation is asynchronous either way, and VideoSurface tolerates zero initial geometry (it tracks the area's GeometryChanged).
public sealed class VideoHostWayland : IVideoHost
{
    private readonly VideoArea area;
    private VideoSurface? surface;

    public event Action? RenderContextReady;
    public event Action<int>? RenderFailed;
    public event Action? FirstFrameRendered;
    public event Action? GeometryChanged;

    public VideoHostWayland(Gtk.Window window)
    {
        area = new VideoArea();
        surface = new VideoSurface(window, area);
        surface.RenderContextReady += OnSurfaceRenderContextReady;
        surface.RenderFailed += OnSurfaceRenderFailed;
        surface.FirstFrameRendered += OnSurfaceFirstFrameRendered;
        area.GeometryChanged += OnAreaGeometryChanged;
    }

    public Gtk.Widget Widget
    {
        get
        {
            return area;
        }
    }

    public VideoSurface? WaylandSurface
    {
        get
        {
            return surface;
        }
    }

    public void AttachPlayback(Playback.Playback playback)
    {
        if (surface == null)
        {
            throw new InvalidOperationException("AttachPlayback called after TeardownRenderSurface.");
        }
        var s = surface;
        playback.AttachRenderSurface(d => s.SetMpvDispatcher(d));
    }

    public void RefreshGeometry()
    {
        area.RefreshGeometry();
    }

    public void TeardownRenderSurface()
    {
        if (surface == null)
        {
            return;
        }
        surface.RenderContextReady -= OnSurfaceRenderContextReady;
        surface.RenderFailed -= OnSurfaceRenderFailed;
        surface.FirstFrameRendered -= OnSurfaceFirstFrameRendered;
        surface.Dispose();
        surface = null;
    }

    private void OnSurfaceRenderContextReady()
    {
        RenderContextReady?.Invoke();
    }

    private void OnSurfaceRenderFailed(int code)
    {
        RenderFailed?.Invoke(code);
    }

    private void OnSurfaceFirstFrameRendered()
    {
        FirstFrameRendered?.Invoke();
    }

    private void OnAreaGeometryChanged(int x, int y, int w, int h, int scale)
    {
        GeometryChanged?.Invoke();
    }
}
