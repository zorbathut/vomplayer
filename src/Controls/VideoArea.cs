using System;

namespace Vomplayer.Controls;

// DrawingArea subclass that paints nothing. Emits GeometryChanged whenever its size or position (relative to the root window) changes. Used as the placeholder widget in the Wayland subsurface path — it reserves layout space, and the subsurface sits over its bounds at the compositor level.
public sealed class VideoArea : Gtk.DrawingArea
{
    private int lastX = -1;
    private int lastY = -1;
    private int lastWidth = -1;
    private int lastHeight = -1;
    private int lastScale = -1;

    public event Action<int, int, int, int, int>? GeometryChanged;

    public VideoArea()
    {
        SetHexpand(true);
        SetVexpand(true);
        // Paint nothing. Explicitly set an empty draw func so the default CSS background doesn't render.
        SetDrawFunc((_, _, _, _) => { });
        OnResize += OnAreaResize;
    }

    private void OnAreaResize(Gtk.DrawingArea sender, Gtk.DrawingArea.ResizeSignalArgs args)
    {
        EmitGeometryIfChanged();
    }

    // Called after the widget is mapped / after layout settles. Safe to invoke at any time; emits only if the geometry actually changed.
    public void RefreshGeometry()
    {
        EmitGeometryIfChanged();
    }

    private void EmitGeometryIfChanged()
    {
        // GetRoot returns Gtk.Root (interface); ComputeBounds wants Gtk.Widget. The Gtk.Window that implements Root is-a Widget, so cast.
        var root = GetRoot() as Gtk.Widget;
        if (root == null)
        {
            return;
        }
        // ComputeBounds gives our bounds expressed in the ancestor's coordinate space. For the root window, that's (x, y) relative to the window's origin — which is what we want for wl_subsurface.set_position against the main wl_surface.
        if (!ComputeBounds(root, out var bounds))
        {
            return;
        }
        int x = (int)MathF.Round(bounds.GetX());
        int y = (int)MathF.Round(bounds.GetY());
        int w = (int)MathF.Round(bounds.GetWidth());
        int h = (int)MathF.Round(bounds.GetHeight());
        int scale = GetScaleFactor();
        if (scale < 1)
        {
            scale = 1;
        }
        if (x == lastX && y == lastY && w == lastWidth && h == lastHeight && scale == lastScale)
        {
            return;
        }
        lastX = x;
        lastY = y;
        lastWidth = w;
        lastHeight = h;
        lastScale = scale;
        GeometryChanged?.Invoke(x, y, w, h, scale);
    }
}
