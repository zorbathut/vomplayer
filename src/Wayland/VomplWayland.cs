using System;
using System.Runtime.InteropServices;

namespace Vomplayer.Wayland;

// P/Invoke bindings to the native/hdr_helper.c subsurface API. Opaque IntPtr handle. Disposing the wrapper calls vompl_video_surface_destroy.
//
// Delegate lifetime: per-surface trampoline delegates are held on instance fields so their thunks remain rooted until Dispose. A GCHandle routes callbacks back to the C# FrameTimingBridge via the native `data` parameter. Matches the Mpv/MpvRenderContext.cs:15 pattern.
// ABI mirror of hdr_helper.c's vompl_image_description_params. All fields are wp_color_management_v1 wire units: PrimariesNamed / TfNamed / RenderIntent are the protocol enum values, mastering primaries are protocol-unit CIE xy. Sequential layout with only 4-byte fields — no padding on either side.
[StructLayout(LayoutKind.Sequential)]
internal struct ImageDescriptionParams
{
    public uint PrimariesNamed;
    public uint TfNamed;
    public uint RenderIntent;
    public int WithMasteringPrimaries;
    public int MRx, MRy, MGx, MGy, MBx, MBy, MWx, MWy;
}

internal sealed partial class VomplVideoSurface : IDisposable
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

    public VomplVideoSurface(IntPtr wlDisplay, IntPtr wlParentSurface, int initialW, int initialH, int initialBufferScale)
    {
        Bridge = new FrameTimingBridge();
        bridgeHandle = GCHandle.Alloc(Bridge);

        int clampedW = initialW < 1 ? 1 : initialW;
        int clampedH = initialH < 1 ? 1 : initialH;
        int clampedScale = initialBufferScale < 1 ? 1 : initialBufferScale;

        handle = Create(wlDisplay, wlParentSurface, clampedW, clampedH, clampedScale);
        if (handle == IntPtr.Zero)
        {
            bridgeHandle.Free();
            throw new InvalidOperationException("vompl_video_surface_create returned NULL — see stderr for details.");
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

    // Place this subsurface directly above `sibling` (which must be a subsurface of the same parent — invariant enforced by the protocol), or directly above the parent wl_surface when `sibling` is null. The above-parent form is used during the PiP-only-secondary-loaded transient to make the PiP composite over the still-visible noVideoBg placeholder; restore by passing the primary sibling. The native call commits the parent + flushes synchronously so stacking lands atomically rather than waiting for GTK's next redraw.
    public void PlaceAbove(VomplVideoSurface? sibling)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }
        IntPtr siblingHandle = (sibling != null) ? sibling.handle : IntPtr.Zero;
        if (sibling != null && siblingHandle == IntPtr.Zero)
        {
            return;
        }
        PlaceAboveNative(handle, siblingHandle);
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

    // Stages an explicit image description on the subsurface's wp_color_management_v1 surface, built from caller-chosen protocol parameters (the HDR-vs-SDR selection and all values are policy and live in VideoSurface). Does NOT issue a wl_surface_commit — the next Swap flushes it atomically with the first new-content buffer, avoiding a one-frame flash of mis-tagged content. Returns 0 on success, -1 if the compositor does not advertise wp_color_manager_v1 or the description build failed; caller must only advance mpv to PQ targets when this returns 0 for an HDR description.
    public int SetImageDescription(in ImageDescriptionParams p)
    {
        if (handle == IntPtr.Zero)
        {
            return -1;
        }
        return SetImageDescriptionNative(handle, in p);
    }

    // Classifies the compositor's recent refresh-period stream. Unknown means the sample window hasn't filled yet; wait a second and re-poll.
    public VrrClassification GetVrrClassification()
    {
        return Bridge.GetClassification(out _);
    }

    // Measured mean presentation rate from the ring deltas, expressed as centi-Hz. See FrameTimingBridge.MeasuredHzCenti for the rationale (vs. nominal panel-mode Hz).
    public int VrrMeasuredHzCenti
    {
        get
        {
            return Bridge.MeasuredHzCenti;
        }
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

    [LibraryImport(Lib, EntryPoint = "vompl_video_surface_create")]
    private static partial IntPtr Create(IntPtr wlDisplay, IntPtr wlParentSurface, int initialW, int initialH, int initialBufferScale);

    [LibraryImport(Lib, EntryPoint = "vompl_video_surface_set_callbacks")]
    private static partial void SetCallbacks(IntPtr vs, IntPtr data,
        SurfaceEnterCallback enter, SurfaceLeaveCallback leave,
        FeedbackPresentedCallback presented, FeedbackDiscardedCallback discarded);

    [LibraryImport(Lib, EntryPoint = "vompl_video_surface_set_geometry")]
    private static partial void SetGeometryNative(IntPtr vs, int x, int y, int w, int h, int bufferScale);

    // sibling=IntPtr.Zero means "place above parent" (see vompl_video_surface_place_above's docstring).
    [LibraryImport(Lib, EntryPoint = "vompl_video_surface_place_above")]
    private static partial void PlaceAboveNative(IntPtr vs, IntPtr sibling);

    [LibraryImport(Lib, EntryPoint = "vompl_video_surface_make_current")]
    private static partial int MakeCurrentNative(IntPtr vs);

    [LibraryImport(Lib, EntryPoint = "vompl_video_surface_swap")]
    private static partial void SwapNative(IntPtr vs);

    [LibraryImport(Lib, EntryPoint = "vompl_video_surface_get_buffer_size")]
    private static partial void GetBufferSizeNative(IntPtr vs, out int w, out int h);

    [LibraryImport(Lib, EntryPoint = "vompl_video_surface_set_image_description")]
    private static partial int SetImageDescriptionNative(IntPtr vs, in ImageDescriptionParams p);

    [LibraryImport(Lib, EntryPoint = "vompl_video_surface_destroy")]
    private static partial void DestroyNative(IntPtr vs);
}

// Process-global output-event trampolines. Forwards straight into WaylandOutputRegistry. Delegates are static-rooted so they're pinned for the process lifetime. The native shim buffers cached mode + HDR bits and replays them when callbacks register, so ordering vs. ensure_globals is not load-bearing.
internal static partial class VomplOutputCallbacks
{
    private const string Lib = "hdr_helper";

    private static readonly OutputModeCallback modeDelegate = OnMode;
    private static readonly OutputRemovedCallback removedDelegate = OnRemoved;
    private static readonly OutputImageInfoCallback imageInfoDelegate = OnImageInfo;
    private static readonly OutputNameCallback nameDelegate = OnName;
    private static readonly bool logHdr = Environment.GetEnvironmentVariable("VOMPL_LOG_HDR") == "1";
    private static bool registered;

    public static void EnsureRegistered()
    {
        if (registered)
        {
            return;
        }
        SetOutputCallbacks(modeDelegate, removedDelegate, imageInfoDelegate, nameDelegate);
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

    private static void OnImageInfo(uint registryName, int hasTfNamed, uint tfNamed)
    {
        try
        {
            bool isHdr = HdrClassifier.IsHdr(hasTfNamed != 0, tfNamed);
            WaylandOutputRegistry.OnOutputHdr(registryName, isHdr);
            if (logHdr)
            {
                string tfStr = hasTfNamed != 0 ? tfNamed.ToString() : "absent";
                Console.Error.WriteLine($"[vompl] output {registryName} tf_named={tfStr} isHdr={isHdr}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vomplayer] WaylandOutputRegistry.OnOutputHdr threw: {ex}");
        }
    }

    // wl_output v4 .name forwards a UTF-8 connector string (e.g. "HDMI-A-1"). Marshalled in as IntPtr to keep Mono/CoreCLR's marshaler from copying eagerly when the value is empty/null on pre-v4 compositors; PtrToStringUTF8 handles both null and the empty case.
    private static void OnName(uint registryName, IntPtr namePtr)
    {
        try
        {
            string name = namePtr == IntPtr.Zero ? string.Empty : (Marshal.PtrToStringUTF8(namePtr) ?? string.Empty);
            WaylandOutputRegistry.OnOutputName(registryName, name);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vomplayer] WaylandOutputRegistry.OnOutputName threw: {ex}");
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void OutputModeCallback(uint registryName, int refreshMhz);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void OutputRemovedCallback(uint registryName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void OutputImageInfoCallback(uint registryName, int hasTfNamed, uint tfNamed);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void OutputNameCallback(uint registryName, IntPtr namePtr);

    [LibraryImport(Lib, EntryPoint = "vompl_set_output_callbacks")]
    private static partial void SetOutputCallbacks(OutputModeCallback mode, OutputRemovedCallback removed, OutputImageInfoCallback imageInfo, OutputNameCallback name);
}
