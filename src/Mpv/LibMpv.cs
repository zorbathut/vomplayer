using System;
using System.Runtime.InteropServices;

namespace Vomplayer.Mpv;

internal static partial class LibMpv
{
    private const string Lib = "mpv";

    [LibraryImport(Lib, EntryPoint = "mpv_create")]
    public static partial IntPtr Create();

    [LibraryImport(Lib, EntryPoint = "mpv_initialize")]
    public static partial int Initialize(IntPtr ctx);

    [LibraryImport(Lib, EntryPoint = "mpv_terminate_destroy")]
    public static partial void TerminateDestroy(IntPtr ctx);

    [LibraryImport(Lib, EntryPoint = "mpv_set_option_string", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SetOptionString(IntPtr ctx, string name, string data);

    [LibraryImport(Lib, EntryPoint = "mpv_set_property_string", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SetPropertyString(IntPtr ctx, string name, string data);

    [LibraryImport(Lib, EntryPoint = "mpv_get_property_string", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr GetPropertyString(IntPtr ctx, string name);

    [LibraryImport(Lib, EntryPoint = "mpv_get_property", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int GetPropertyDouble(IntPtr ctx, string name, MpvFormat format, out double data);

    [LibraryImport(Lib, EntryPoint = "mpv_get_property", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int GetPropertyFlag(IntPtr ctx, string name, MpvFormat format, out int data);

    [LibraryImport(Lib, EntryPoint = "mpv_observe_property", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int ObserveProperty(IntPtr ctx, ulong replyUserData, string name, MpvFormat format);

    [LibraryImport(Lib, EntryPoint = "mpv_unobserve_property")]
    public static partial int UnobserveProperty(IntPtr ctx, ulong registeredReplyUserData);

    [LibraryImport(Lib, EntryPoint = "mpv_free")]
    public static partial void Free(IntPtr data);

    [LibraryImport(Lib, EntryPoint = "mpv_command")]
    public static partial int Command(IntPtr ctx, IntPtr[] args);

    [LibraryImport(Lib, EntryPoint = "mpv_command_string", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int CommandString(IntPtr ctx, string args);

    [LibraryImport(Lib, EntryPoint = "mpv_wait_event")]
    public static partial IntPtr WaitEvent(IntPtr ctx, double timeout);

    [LibraryImport(Lib, EntryPoint = "mpv_set_wakeup_callback")]
    public static partial void SetWakeupCallback(IntPtr ctx, WakeupCallback callback, IntPtr data);

    [LibraryImport(Lib, EntryPoint = "mpv_set_wakeup_callback")]
    public static partial void ClearWakeupCallback(IntPtr ctx, IntPtr callback, IntPtr data);

    [LibraryImport(Lib, EntryPoint = "mpv_error_string", StringMarshalling = StringMarshalling.Utf8)]
    public static partial string? ErrorString(int error);

    [LibraryImport(Lib, EntryPoint = "mpv_render_context_create")]
    public static partial int RenderContextCreate(out IntPtr res, IntPtr mpv, ref MpvRenderParam parameters);

    [LibraryImport(Lib, EntryPoint = "mpv_render_context_render")]
    public static partial int RenderContextRender(IntPtr ctx, ref MpvRenderParam parameters);

    [LibraryImport(Lib, EntryPoint = "mpv_render_context_update")]
    public static partial ulong RenderContextUpdate(IntPtr ctx);

    [LibraryImport(Lib, EntryPoint = "mpv_render_context_set_update_callback")]
    public static partial void RenderContextSetUpdateCallback(IntPtr ctx, RenderUpdateCallback callback, IntPtr cbCtx);

    [LibraryImport(Lib, EntryPoint = "mpv_render_context_set_update_callback")]
    public static partial void RenderContextClearUpdateCallback(IntPtr ctx, IntPtr callback, IntPtr cbCtx);

    [LibraryImport(Lib, EntryPoint = "mpv_render_context_report_swap")]
    public static partial void RenderContextReportSwap(IntPtr ctx);

    [LibraryImport(Lib, EntryPoint = "mpv_render_context_free")]
    public static partial void RenderContextFree(IntPtr ctx);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void WakeupCallback(IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void RenderUpdateCallback(IntPtr cbCtx);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr MpvOpenGlGetProcAddress(IntPtr ctx, IntPtr name);

    [StructLayout(LayoutKind.Sequential)]
    public struct Event
    {
        public MpvEventId EventId;
        public int Error;
        public ulong ReplyUserData;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventProperty
    {
        public IntPtr Name;
        public MpvFormat Format;
        public IntPtr Data;
    }
}

public enum MpvEventId
{
    None = 0,
    Shutdown = 1,
    LogMessage = 2,
    GetPropertyReply = 3,
    SetPropertyReply = 4,
    CommandReply = 5,
    StartFile = 6,
    EndFile = 7,
    FileLoaded = 8,
    ClientMessage = 16,
    VideoReconfig = 17,
    AudioReconfig = 18,
    Seek = 20,
    PlaybackRestart = 21,
    PropertyChange = 22,
    QueueOverflow = 24,
    Hook = 25,
}

public enum MpvErrorCode
{
    Success = 0,
    EventQueueFull = -1,
    NoMem = -2,
    Uninitialized = -3,
    InvalidParameter = -4,
    OptionNotFound = -5,
    OptionFormat = -6,
    OptionError = -7,
    PropertyNotFound = -8,
    PropertyFormat = -9,
    PropertyUnavailable = -10,
    PropertyError = -11,
    Command = -12,
    LoadingFailed = -13,
    AoInitFailed = -14,
    VoInitFailed = -15,
    NothingToPlay = -16,
    UnknownFormat = -17,
    Unsupported = -18,
    NotImplemented = -19,
    Generic = -20,
}

public enum MpvFormat
{
    None = 0,
    String = 1,
    OsdString = 2,
    Flag = 3,
    Int64 = 4,
    Double = 5,
    Node = 6,
    NodeArray = 7,
    NodeMap = 8,
    ByteArray = 9,
}

[Flags]
public enum MpvRenderUpdateFlag : ulong
{
    None = 0,
    Frame = 1UL << 0,
}

public enum MpvRenderParamType
{
    Invalid = 0,
    ApiType = 1,
    OpenglInitParams = 2,
    OpenglFbo = 3,
    FlipY = 4,
    Depth = 5,
    IccProfile = 6,
    AmbientLight = 7,
    X11Display = 8,
    WlDisplay = 9,
    AdvancedControl = 10,
    NextFrameInfo = 11,
    BlockForTargetTime = 12,
    SkipRendering = 13,
    Drm = 14,
    SwSize = 15,
    SwFormat = 16,
    SwStride = 17,
    SwPointer = 18,
}

[StructLayout(LayoutKind.Sequential)]
public struct MpvRenderParam
{
    public MpvRenderParamType Type;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
public struct MpvOpenGlInitParams
{
    public IntPtr GetProcAddress;
    public IntPtr GetProcAddressCtx;
}

[StructLayout(LayoutKind.Sequential)]
public struct MpvOpenGlFbo
{
    public int Fbo;
    public int Width;
    public int Height;
    public int InternalFormat;
}
