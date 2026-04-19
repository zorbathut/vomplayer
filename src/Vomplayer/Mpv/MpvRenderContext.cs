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

    public MpvRenderContext(MpvClient client, Func<string, IntPtr> getProcAddress)
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
            Span<MpvRenderParam> parameters = stackalloc MpvRenderParam[4];
            parameters[0] = new MpvRenderParam { Type = MpvRenderParamType.ApiType, Data = openGlApiTypePtr };
            parameters[1] = new MpvRenderParam { Type = MpvRenderParamType.OpenglInitParams, Data = (IntPtr)(&initParams) };
            parameters[2] = new MpvRenderParam { Type = MpvRenderParamType.AdvancedControl, Data = (IntPtr)(&advancedControl) };
            parameters[3] = new MpvRenderParam { Type = MpvRenderParamType.Invalid, Data = IntPtr.Zero };

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
