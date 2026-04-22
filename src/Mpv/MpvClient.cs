using System;
using System.Runtime.InteropServices;

namespace Vomplayer.Mpv;

public sealed class MpvException : Exception
{
    public int Code { get; }

    public MpvException(int code, string? message)
        : base(message != null ? $"mpv error {code}: {message}" : $"mpv error {code}")
    {
        Code = code;
    }
}

public readonly record struct PropertyChange(string Name, MpvPropertyValue Value, ulong Id);

public readonly record struct MpvPropertyValue(object? Raw)
{
    public double? AsDouble
    {
        get
        {
            return Raw as double?;
        }
    }

    public long? AsInt64
    {
        get
        {
            return Raw as long?;
        }
    }

    public bool? AsFlag
    {
        get
        {
            if (Raw is int i)
            {
                return i != 0;
            }
            return null;
        }
    }

    public string? AsString
    {
        get
        {
            return Raw as string;
        }
    }
}

// Thread model: no method is thread-safe relative to itself — callers must serialize access to a single owner thread. In production that owner is the MpvDispatcher worker; tests (via InternalsVisibleTo) drive MpvClient directly on the test thread. The wakeup callback is the one exception: it fires on libmpv's event thread and only raises EventAvailable; no mpv_* calls happen inside it. Callers must subscribe to PropertyChanged before calling ObserveProperty, since libmpv synthesizes an initial change event as part of the observe call.
//
// Visibility: internal so production consumers are forced through MpvDispatcher + MpvHandle. The only other call path is MpvRenderContext's ctor, which runs on the GL-owning thread and reaches us via MpvDispatcher.CreateRenderContext; it reads the Handle property to hand the raw mpv ctx to mpv_render_context_create.
internal sealed class MpvClient : IDisposable
{
    private IntPtr ctx;
    private LibMpv.WakeupCallback? wakeup;
    private ulong nextObserveId;

    internal IntPtr Handle
    {
        get
        {
            return ctx;
        }
    }

    public event Action? EventAvailable;
    public event Action? FileLoaded;
    public event Action<int>? FileEnded;
    public event Action<PropertyChange>? PropertyChanged;
    public event Action? Shutdown;

    public MpvClient()
    {
        ctx = LibMpv.Create();
        if (ctx == IntPtr.Zero)
        {
            throw new InvalidOperationException("mpv_create failed (is libmpv installed?)");
        }

        wakeup = OnWakeup;
        LibMpv.SetWakeupCallback(ctx, wakeup, IntPtr.Zero);
    }

    public void SetOption(string name, string value)
    {
        Check(LibMpv.SetOptionString(ctx, name, value));
    }

    public void SetProperty(string name, string value)
    {
        Check(LibMpv.SetPropertyString(ctx, name, value));
    }

    public void Initialize()
    {
        Check(LibMpv.Initialize(ctx));
    }

    public void Command(params string[] args)
    {
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            }
            ptrs[args.Length] = IntPtr.Zero;
            Check(LibMpv.Command(ctx, ptrs));
        }
        finally
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (ptrs[i] != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(ptrs[i]);
                }
            }
        }
    }

    public string? GetPropertyString(string name)
    {
        var p = LibMpv.GetPropertyString(ctx, name);
        if (p == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            return Marshal.PtrToStringUTF8(p);
        }
        finally
        {
            LibMpv.Free(p);
        }
    }

    public double? GetPropertyDouble(string name)
    {
        var rc = LibMpv.GetPropertyDouble(ctx, name, MpvFormat.Double, out var value);
        if (rc == (int)MpvErrorCode.PropertyUnavailable)
        {
            return null;
        }
        Check(rc);
        return value;
    }

    public bool? GetPropertyFlag(string name)
    {
        var rc = LibMpv.GetPropertyFlag(ctx, name, MpvFormat.Flag, out var value);
        if (rc == (int)MpvErrorCode.PropertyUnavailable)
        {
            return null;
        }
        Check(rc);
        return value != 0;
    }

    public ulong ObserveProperty(string name, MpvFormat format)
    {
        var id = ++nextObserveId;
        Check(LibMpv.ObserveProperty(ctx, id, name, format));
        return id;
    }

    // Returns the number of observations removed — 0 means the id wasn't registered. Not throwing on 0 lets callers write idempotent cleanup paths.
    public int UnobserveProperty(ulong id)
    {
        var rc = LibMpv.UnobserveProperty(ctx, id);
        if (rc < 0)
        {
            throw new MpvException(rc, LibMpv.ErrorString(rc));
        }
        return rc;
    }

    public void DrainEvents()
    {
        if (ctx == IntPtr.Zero)
        {
            return;
        }
        while (true)
        {
            var ptr = LibMpv.WaitEvent(ctx, 0);
            if (ptr == IntPtr.Zero)
            {
                return;
            }
            var evt = Marshal.PtrToStructure<LibMpv.Event>(ptr);
            if (evt.EventId == MpvEventId.None)
            {
                return;
            }
            Dispatch(evt);
        }
    }

    private void Dispatch(LibMpv.Event evt)
    {
        switch (evt.EventId)
        {
            case MpvEventId.FileLoaded:
                FileLoaded?.Invoke();
                break;
            case MpvEventId.EndFile:
                FileEnded?.Invoke(evt.Error);
                break;
            case MpvEventId.Shutdown:
                Shutdown?.Invoke();
                break;
            case MpvEventId.PropertyChange when evt.Data != IntPtr.Zero:
                var prop = Marshal.PtrToStructure<LibMpv.EventProperty>(evt.Data);
                var name = Marshal.PtrToStringUTF8(prop.Name) ?? "";
                var value = ReadPropertyValue(prop);
                PropertyChanged?.Invoke(new PropertyChange(name, value, evt.ReplyUserData));
                break;
        }
    }

    private static MpvPropertyValue ReadPropertyValue(LibMpv.EventProperty prop)
    {
        if (prop.Data == IntPtr.Zero)
        {
            return new MpvPropertyValue(null);
        }
        switch (prop.Format)
        {
            case MpvFormat.Double:
                return new MpvPropertyValue(Marshal.PtrToStructure<double>(prop.Data));
            case MpvFormat.Int64:
                return new MpvPropertyValue(Marshal.PtrToStructure<long>(prop.Data));
            case MpvFormat.Flag:
                return new MpvPropertyValue(Marshal.PtrToStructure<int>(prop.Data));
            case MpvFormat.String:
            case MpvFormat.OsdString:
                return new MpvPropertyValue(Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(prop.Data)));
            default:
                throw new InvalidOperationException($"Unhandled mpv property format: {prop.Format}");
        }
    }

    private void OnWakeup(IntPtr data)
    {
        EventAvailable?.Invoke();
    }

    private static void Check(int rc)
    {
        if (rc < 0)
        {
            throw new MpvException(rc, LibMpv.ErrorString(rc));
        }
    }

    public void Dispose()
    {
        if (ctx == IntPtr.Zero)
        {
            return;
        }
        // Clear the native wakeup callback first so libmpv can't fire into a disposed object during teardown. Then drop managed event subscribers before destroying ctx so any late dispatch this thread is still servicing becomes a no-op.
        LibMpv.ClearWakeupCallback(ctx, IntPtr.Zero, IntPtr.Zero);
        EventAvailable = null;
        FileLoaded = null;
        FileEnded = null;
        PropertyChanged = null;
        Shutdown = null;
        LibMpv.TerminateDestroy(ctx);
        ctx = IntPtr.Zero;
        wakeup = null;
    }
}
