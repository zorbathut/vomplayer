using System;
using System.Runtime.InteropServices;

namespace Vomplayer.Wayland;

internal static partial class WaylandDetect
{
    private const string GtkLib = "libgtk-4.so.1";

    [LibraryImport(GtkLib, EntryPoint = "gdk_wayland_display_get_wl_display")]
    private static partial IntPtr GdkWaylandDisplayGetWlDisplay(IntPtr display);

    // Returns true if the Gdk.Display is backed by Wayland. Implementation: call gdk_wayland_display_get_wl_display; on non-Wayland backends this returns NULL (and GDK logs a warning on stderr, but the call itself is safe). We swallow that stderr noise — it's the price of having no cleaner backend-detection API in GirCore 0.7.0.
    public static bool IsWaylandBackend(Gdk.Display display)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }
        if (display == null)
        {
            return false;
        }
        IntPtr wlDisplay = GdkWaylandDisplayGetWlDisplay(display.Handle.DangerousGetHandle());
        return wlDisplay != IntPtr.Zero;
    }
}
