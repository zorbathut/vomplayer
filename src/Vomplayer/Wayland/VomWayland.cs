using System;
using System.Runtime.InteropServices;

namespace Vomplayer.Wayland;

public enum VrrClassification
{
    Unknown = 0,
    Vrr = 1,
    Fixed = 2,
    // Frames on the nominal T grid but the stream is too stable (or metrics in a middle band) to tell fixed-refresh from VRR-locked-to-content-rate. 60 fps content on a 60 Hz panel is the canonical example: behaviorally indistinguishable.
    CantTell = 3,
}

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

    // Classifies the compositor's recent refresh-period stream. When Fixed, hzCenti is the detected rate × 100 (e.g. 6000 = 60.00Hz). Unknown means the sample window hasn't filled yet; wait a second and re-poll.
    public VrrClassification GetVrrClassification(out int hzCenti)
    {
        hzCenti = 0;
        if (handle == IntPtr.Zero)
        {
            return VrrClassification.Unknown;
        }
        int result = GetVrrClassificationNative(handle, out hzCenti);
        return (VrrClassification)result;
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

    [LibraryImport(Lib, EntryPoint = "vom_video_surface_get_vrr_classification")]
    private static partial int GetVrrClassificationNative(IntPtr vs, out int hzCenti);

    [LibraryImport(Lib, EntryPoint = "vom_video_surface_destroy")]
    private static partial void DestroyNative(IntPtr vs);
}
