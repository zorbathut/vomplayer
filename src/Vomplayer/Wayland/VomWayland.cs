using System;
using System.Runtime.InteropServices;

namespace Vomplayer.Wayland;

// P/Invoke bindings to the native/hdr_helper.c subsurface API. Opaque IntPtr handle. Disposing the wrapper calls vom_video_surface_destroy.
internal sealed partial class VomVideoSurface : IDisposable
{
    private const string Lib = "hdr_helper";

    private IntPtr handle;

    public VomVideoSurface(IntPtr wlDisplay, IntPtr wlParentSurface, int initialW, int initialH, int initialBufferScale, bool hdr)
    {
        handle = Create(wlDisplay, wlParentSurface, initialW, initialH, initialBufferScale, hdr ? 1 : 0);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("vom_video_surface_create returned NULL — see stderr for details.");
        }
    }

    public void SetGeometry(int x, int y, int w, int h, int bufferScale)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }
        SetGeometryNative(handle, x, y, w, h, bufferScale);
    }

    public int MakeCurrent()
    {
        if (handle == IntPtr.Zero)
        {
            return -1;
        }
        return MakeCurrentNative(handle);
    }

    public void Swap()
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }
        SwapNative(handle);
    }

    public (int width, int height) GetBufferSize()
    {
        if (handle == IntPtr.Zero)
        {
            return (0, 0);
        }
        GetBufferSizeNative(handle, out var w, out var h);
        return (w, h);
    }

    // True iff the shim successfully attached a PQ/BT.2020 image description. False when hdr was requested but the compositor didn't honor it (no wp_color_manager_v1, no PQ transfer, etc).
    public bool HdrActive
    {
        get
        {
            if (handle == IntPtr.Zero)
            {
                return false;
            }
            return HdrActiveNative(handle) != 0;
        }
    }

    public void Dispose()
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }
        DestroyNative(handle);
        handle = IntPtr.Zero;
    }

    [LibraryImport(Lib, EntryPoint = "vom_video_surface_create")]
    private static partial IntPtr Create(IntPtr wlDisplay, IntPtr wlParentSurface, int initialW, int initialH, int initialBufferScale, int hdr);

    [LibraryImport(Lib, EntryPoint = "vom_video_surface_set_geometry")]
    private static partial void SetGeometryNative(IntPtr vs, int x, int y, int w, int h, int bufferScale);

    [LibraryImport(Lib, EntryPoint = "vom_video_surface_make_current")]
    private static partial int MakeCurrentNative(IntPtr vs);

    [LibraryImport(Lib, EntryPoint = "vom_video_surface_swap")]
    private static partial void SwapNative(IntPtr vs);

    [LibraryImport(Lib, EntryPoint = "vom_video_surface_get_buffer_size")]
    private static partial void GetBufferSizeNative(IntPtr vs, out int w, out int h);

    [LibraryImport(Lib, EntryPoint = "vom_video_surface_hdr_active")]
    private static partial int HdrActiveNative(IntPtr vs);

    [LibraryImport(Lib, EntryPoint = "vom_video_surface_destroy")]
    private static partial void DestroyNative(IntPtr vs);
}
