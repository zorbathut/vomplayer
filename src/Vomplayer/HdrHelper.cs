using System;
using System.Runtime.InteropServices;

namespace Vomplayer;

// Attaches a PQ/BT.2020 image description to the given GTK window's underlying wl_surface via our native shim (native/hdr_helper.c + wayland-scanner-generated protocol glue). Bypasses GTK's own GDK color management because as of GTK 4.20, GDK's sanity check requires `wp_color_manager_v1` to advertise TRANSFER_FUNCTION_SRGB, which KWin does not advertise — GDK then opts out entirely. Our shim binds the protocol itself and doesn't fight GDK (which has already bailed).
//
// Linux-only. On Windows / macOS / non-Wayland the method returns early with a sentinel code and no P/Invoke happens.
internal static partial class HdrHelper
{
    private const string NativeLib = "hdr_helper";
    private const string GtkLib = "gtk-4";

    [LibraryImport(GtkLib, EntryPoint = "gdk_wayland_display_get_wl_display")]
    private static partial IntPtr GdkWaylandDisplayGetWlDisplay(IntPtr display);

    [LibraryImport(GtkLib, EntryPoint = "gdk_wayland_surface_get_wl_surface")]
    private static partial IntPtr GdkWaylandSurfaceGetWlSurface(IntPtr surface);

    [LibraryImport(NativeLib, EntryPoint = "hdr_helper_apply_pq")]
    private static partial int ApplyPq(IntPtr wlDisplay, IntPtr wlSurface);

    public static int ApplyPqToGtkWindow(Gtk.Window window)
    {
        if (!OperatingSystem.IsLinux())
        {
            return -200;
        }

        var gdkSurface = window.GetSurface();
        if (gdkSurface == null)
        {
            Console.Error.WriteLine("[hdr_helper] window has no GdkSurface yet (not realized?)");
            return -100;
        }
        var gdkDisplay = gdkSurface.GetDisplay();
        if (gdkDisplay == null)
        {
            Console.Error.WriteLine("[hdr_helper] surface has no display");
            return -101;
        }

        IntPtr surfaceHandle = gdkSurface.Handle.DangerousGetHandle();
        IntPtr displayHandle = gdkDisplay.Handle.DangerousGetHandle();

        IntPtr wlDisplay = GdkWaylandDisplayGetWlDisplay(displayHandle);
        IntPtr wlSurface = GdkWaylandSurfaceGetWlSurface(surfaceHandle);

        if (wlDisplay == IntPtr.Zero || wlSurface == IntPtr.Zero)
        {
            Console.Error.WriteLine($"[hdr_helper] wl_display={wlDisplay}, wl_surface={wlSurface} — not on Wayland GDK backend?");
            return -102;
        }

        return ApplyPq(wlDisplay, wlSurface);
    }
}
