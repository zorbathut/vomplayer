using System;
using System.Runtime.InteropServices;

namespace Vomplayer.Wayland;

// P/Invoke bindings to the native/hdr_helper.c subsurface API. Opaque IntPtr handle. Disposing the wrapper calls vom_video_surface_destroy.
//
// Delegate lifetime: per-surface trampoline delegates are held on instance fields so their thunks remain rooted until Dispose. A GCHandle routes callbacks back to the C# FrameTimingBridge via the native `data` parameter. Matches the Mpv/MpvRenderContext.cs:15 pattern.
internal sealed partial class VomVideoSurface : IDisposable
{
    private const string Lib = "hdr_helper";

    private IntPtr handle;
    private GCHandle bridgeHandle;

    // Held as instance fields so the thunks outlive the native struct. Cleared in Dispose after native destroy returns.
    private SurfaceEnterCallback? enterDelegate;
    private SurfaceLeaveCallback? leaveDelegate;
    private FeedbackPresentedCallback? presentedDelegate;
    private FeedbackDiscardedCallback? discardedDelegate;

    internal FrameTimingBridge Bridge { get; }

    public VomVideoSurface(IntPtr wlDisplay, IntPtr wlParentSurface, int initialW, int initialH, int initialBufferScale, bool hdr)
    {
        Bridge = new FrameTimingBridge();
        bridgeHandle = GCHandle.Alloc(Bridge);

        int clampedW = initialW < 1 ? 1 : initialW;
        int clampedH = initialH < 1 ? 1 : initialH;
        int clampedScale = initialBufferScale < 1 ? 1 : initialBufferScale;

        handle = Create(wlDisplay, wlParentSurface, clampedW, clampedH, clampedScale, hdr ? 1 : 0);
        if (handle == IntPtr.Zero)
        {
            bridgeHandle.Free();
            throw new InvalidOperationException("vom_video_surface_create returned NULL — see stderr for details.");
        }

        enterDelegate = OnEnterTrampoline;
        leaveDelegate = OnLeaveTrampoline;
        presentedDelegate = OnPresentedTrampoline;
        discardedDelegate = OnDiscardedTrampoline;
        SetCallbacks(handle, GCHandle.ToIntPtr(bridgeHandle),
            enterDelegate, leaveDelegate, presentedDelegate, discardedDelegate);
    }

    public void SetGeometry(int x, int y, int w, int h, int bufferScale)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }
        int clampedW = w < 1 ? 1 : w;
        int clampedH = h < 1 ? 1 : h;
        int clampedScale = bufferScale < 1 ? 1 : bufferScale;
        SetGeometryNative(handle, x, y, clampedW, clampedH, clampedScale);
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
        return Bridge.GetClassification(out hzCenti);
    }

    public void Dispose()
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }
        var h = handle;
        handle = IntPtr.Zero;
        // Destroy first so no in-flight callback can trampoline to a freed GCHandle. After destroy returns, the native struct is gone and no new callbacks can fire.
        DestroyNative(h);
        if (bridgeHandle.IsAllocated)
        {
            bridgeHandle.Free();
        }
        enterDelegate = null;
        leaveDelegate = null;
        presentedDelegate = null;
        discardedDelegate = null;
    }

    // Trampolines must not throw across the ABI — that's undefined behavior in native code. Log and swallow here; upstream `Bridge.On*` methods are simple accumulators that should not throw under normal operation, but a future change might introduce something that does.
    private static void OnEnterTrampoline(IntPtr data, uint registryName)
    {
        try
        {
            GetBridge(data)?.OnEnter(registryName);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vomplayer] FrameTimingBridge.OnEnter threw: {ex}");
        }
    }

    private static void OnLeaveTrampoline(IntPtr data, uint registryName)
    {
        try
        {
            GetBridge(data)?.OnLeave(registryName);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vomplayer] FrameTimingBridge.OnLeave threw: {ex}");
        }
    }

    private static void OnPresentedTrampoline(IntPtr data, ulong tvNs, uint refreshNs)
    {
        try
        {
            GetBridge(data)?.OnPresented(tvNs, refreshNs);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vomplayer] FrameTimingBridge.OnPresented threw: {ex}");
        }
    }

    private static void OnDiscardedTrampoline(IntPtr data)
    {
        try
        {
            GetBridge(data)?.OnDiscarded();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vomplayer] FrameTimingBridge.OnDiscarded threw: {ex}");
        }
    }

    private static FrameTimingBridge? GetBridge(IntPtr data)
    {
        if (data == IntPtr.Zero)
        {
            return null;
        }
        var h = GCHandle.FromIntPtr(data);
        return h.IsAllocated ? h.Target as FrameTimingBridge : null;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SurfaceEnterCallback(IntPtr data, uint registryName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SurfaceLeaveCallback(IntPtr data, uint registryName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FeedbackPresentedCallback(IntPtr data, ulong tvNs, uint refreshNs);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FeedbackDiscardedCallback(IntPtr data);

    [LibraryImport(Lib, EntryPoint = "vom_video_surface_create")]
    private static partial IntPtr Create(IntPtr wlDisplay, IntPtr wlParentSurface, int initialW, int initialH, int initialBufferScale, int hdr);

    [LibraryImport(Lib, EntryPoint = "vom_video_surface_set_callbacks")]
    private static partial void SetCallbacks(IntPtr vs, IntPtr data,
        SurfaceEnterCallback enter, SurfaceLeaveCallback leave,
        FeedbackPresentedCallback presented, FeedbackDiscardedCallback discarded);

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

// Process-global output-event trampolines. Forwards straight into WaylandOutputRegistry. Delegates are static-rooted so they're pinned for the process lifetime. The native shim buffers cached modes and replays them when callbacks register, so ordering vs. ensure_globals is not load-bearing.
internal static partial class VomOutputCallbacks
{
    private const string Lib = "hdr_helper";

    private static readonly OutputModeCallback modeDelegate = OnMode;
    private static readonly OutputRemovedCallback removedDelegate = OnRemoved;
    private static bool registered;

    public static void EnsureRegistered()
    {
        if (registered)
        {
            return;
        }
        SetOutputCallbacks(modeDelegate, removedDelegate);
        registered = true;
    }

    private static void OnMode(uint registryName, int refreshMhz)
    {
        try
        {
            WaylandOutputRegistry.OnOutputMode(registryName, refreshMhz);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vomplayer] WaylandOutputRegistry.OnOutputMode threw: {ex}");
        }
    }

    private static void OnRemoved(uint registryName)
    {
        try
        {
            WaylandOutputRegistry.OnOutputRemoved(registryName);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vomplayer] WaylandOutputRegistry.OnOutputRemoved threw: {ex}");
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void OutputModeCallback(uint registryName, int refreshMhz);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void OutputRemovedCallback(uint registryName);

    [LibraryImport(Lib, EntryPoint = "vom_set_output_callbacks")]
    private static partial void SetOutputCallbacks(OutputModeCallback mode, OutputRemovedCallback removed);
}
