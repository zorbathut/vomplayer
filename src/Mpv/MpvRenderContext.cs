using System;
using System.Runtime.InteropServices;

namespace Vomplayer.Mpv;

// Wraps mpv_render_context_*. Owns the native context, the managed delegates mpv will call, and per-frame parameter arrays. Constructor and Dispose must run on a thread with the GL context current. GTK4's GLArea pairs realize/unrealize on the main thread with the GL context current, so Dispose can always call mpv_render_context_free safely — no Abandon variant is needed. The get_proc_address and update-callback delegates are held as instance fields so their thunks stay alive as long as the native context; Dispose clears them only after the native free returns.
public sealed class MpvRenderContext : IDisposable
{
    // Static pinned UTF-8 bytes for the "opengl" API type string; lives for process lifetime.
    private static readonly byte[] openGlApiTypeBytes = "opengl\0"u8.ToArray();
    private static readonly GCHandle openGlApiTypePin = GCHandle.Alloc(openGlApiTypeBytes, GCHandleType.Pinned);
    private static readonly IntPtr openGlApiTypePtr = openGlApiTypePin.AddrOfPinnedObject();

    private IntPtr ctx;
    private LibMpv.RenderUpdateCallback? updateCallback;
    private LibMpv.MpvOpenGlGetProcAddress? getProcAddressDelegate;
    private Func<string, IntPtr>? getProcAddress;
    private bool renderFailed;

    public event Action? UpdateRequested;
    public event Action<int>? RenderFailed;

    // Constructed exclusively via MpvDispatcher.CreateRenderContext (or from tests with InternalsVisibleTo). Callers get access to MpvClient only via MpvDispatcher's internal wiring; the ctor is internal because MpvClient is internal.
    //
    // wlDisplay / x11Display: raw native display handles for hwdec interop. mpv's vaapi hwdec driver (and similar) calls vaGetDisplayWl / vaGetDisplayXlib with these to open a VADisplay. Without them the driver iterates its fallbacks (x11 → wayland → drm) and fails — the eglGetCurrentDisplay trick applies to the EGL-interop path, NOT to VA-API native display creation, which needs the actual wl_display / X Display pointer. Pass IntPtr.Zero when unavailable (e.g. non-Wayland surface has no wl_display). Handles must stay valid for the lifetime of this render context; on our path the wl_display is GDK-owned and outlives us.
    internal MpvRenderContext(MpvClient client, Func<string, IntPtr> getProcAddress, IntPtr wlDisplay, IntPtr x11Display)
    {
        if (client == null)
        {
            throw new ArgumentNullException(nameof(client));
        }
        if (getProcAddress == null)
        {
            throw new ArgumentNullException(nameof(getProcAddress));
        }

        this.getProcAddress = getProcAddress;
        getProcAddressDelegate = OnGetProcAddress;
        updateCallback = OnMpvUpdate;

        var initParams = new MpvOpenGlInitParams
        {
            GetProcAddress = Marshal.GetFunctionPointerForDelegate(getProcAddressDelegate),
            GetProcAddressCtx = IntPtr.Zero,
        };

        // ADVANCED_CONTROL=1 enables direct rendering (decoder writes straight to GL textures), GPU screenshots, hwdec interop, and frame-timing alignment with display refresh. Contract: we must have a wakeup callback registered (we do, below) and we must call mpv_render_context_update before mpv_render_context_render so mpv knows the user is keeping up. Failure to follow the contract risks deadlock of the mpv core thread.
        int advancedControl = 1;

        unsafe
        {
            // Max 6 slots: ApiType, OpenglInitParams, AdvancedControl, optional WlDisplay, optional X11Display, Invalid terminator.
            Span<MpvRenderParam> parameters = stackalloc MpvRenderParam[6];
            int i = 0;
            parameters[i++] = new MpvRenderParam { Type = MpvRenderParamType.ApiType, Data = openGlApiTypePtr };
            parameters[i++] = new MpvRenderParam { Type = MpvRenderParamType.OpenglInitParams, Data = (IntPtr)(&initParams) };
            parameters[i++] = new MpvRenderParam { Type = MpvRenderParamType.AdvancedControl, Data = (IntPtr)(&advancedControl) };
            if (wlDisplay != IntPtr.Zero)
            {
                parameters[i++] = new MpvRenderParam { Type = MpvRenderParamType.WlDisplay, Data = wlDisplay };
            }
            if (x11Display != IntPtr.Zero)
            {
                parameters[i++] = new MpvRenderParam { Type = MpvRenderParamType.X11Display, Data = x11Display };
            }
            parameters[i] = new MpvRenderParam { Type = MpvRenderParamType.Invalid, Data = IntPtr.Zero };

            var rc = LibMpv.RenderContextCreate(out ctx, client.Handle, ref parameters[0]);
            if (rc < 0)
            {
                throw new MpvException(rc, LibMpv.ErrorString(rc));
            }
        }

        LibMpv.RenderContextSetUpdateCallback(ctx, updateCallback, IntPtr.Zero);
    }

    public void Render(int fbo, int width, int height)
    {
        if (ctx == IntPtr.Zero)
        {
            return;
        }
        if (renderFailed)
        {
            return;
        }

        // Required by the ADVANCED_CONTROL contract: poll mpv before each render so it knows we're keeping up. We render unconditionally — GTK may have asked for a redraw for non-frame reasons (window damage, resize) and we want to repaint to the new FBO either way; mpv re-renders the current frame if no new one is available.
        LibMpv.RenderContextUpdate(ctx);

        unsafe
        {
            var fboStruct = new MpvOpenGlFbo
            {
                Fbo = fbo,
                Width = width,
                Height = height,
                InternalFormat = 0,
            };
            int flip = 1;

            Span<MpvRenderParam> parameters = stackalloc MpvRenderParam[3];
            parameters[0] = new MpvRenderParam { Type = MpvRenderParamType.OpenglFbo, Data = (IntPtr)(&fboStruct) };
            parameters[1] = new MpvRenderParam { Type = MpvRenderParamType.FlipY, Data = (IntPtr)(&flip) };
            parameters[2] = new MpvRenderParam { Type = MpvRenderParamType.Invalid, Data = IntPtr.Zero };

            var rc = LibMpv.RenderContextRender(ctx, ref parameters[0]);
            if (rc < 0)
            {
                renderFailed = true;
                RenderFailed?.Invoke(rc);
            }
        }
    }

    // Tell mpv the last rendered frame was presented. Part of the ADVANCED_CONTROL contract: improves mpv's internal frame-timing estimate and enables display-sync. Safe no-op on a torn-down context.
    public void ReportSwap()
    {
        if (ctx == IntPtr.Zero)
        {
            return;
        }
        LibMpv.RenderContextReportSwap(ctx);
    }

    public void Dispose()
    {
        if (ctx == IntPtr.Zero)
        {
            return;
        }
        LibMpv.RenderContextClearUpdateCallback(ctx, IntPtr.Zero, IntPtr.Zero);
        LibMpv.RenderContextFree(ctx);
        ctx = IntPtr.Zero;
        updateCallback = null;
        getProcAddressDelegate = null;
        getProcAddress = null;
    }

    private IntPtr OnGetProcAddress(IntPtr cbCtx, IntPtr namePtr)
    {
        var loader = getProcAddress;
        if (loader == null)
        {
            return IntPtr.Zero;
        }
        var name = Marshal.PtrToStringUTF8(namePtr) ?? "";
        return loader(name);
    }

    private void OnMpvUpdate(IntPtr cbCtx)
    {
        UpdateRequested?.Invoke();
    }
}
