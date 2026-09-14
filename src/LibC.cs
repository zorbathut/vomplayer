using System;
using System.Runtime.InteropServices;

namespace Vomplayer;

internal static partial class LibC
{
    private const int LC_NUMERIC = 1;

    [LibraryImport("libc.so.6", EntryPoint = "setlocale", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr Setlocale(int category, string locale);

    // mpv parses numeric option values via strtod etc., which respects LC_NUMERIC. mpv refuses to initialize on non-C LC_NUMERIC. GTK's init calls setlocale(LC_ALL, "") which switches to the user locale; we must re-force LC_NUMERIC=C after gtk_init has run and before mpv_create, not before Main.
    public static void ForceCNumericLocale()
    {
        Setlocale(LC_NUMERIC, "C");
    }

    private const int STDERR_FILENO = 2;
    private const int EINTR = 4;
    private const int EAGAIN = 11; // == EWOULDBLOCK on Linux

    [LibraryImport("libc.so.6", EntryPoint = "write", SetLastError = true)]
    private static partial nint Write(int fd, IntPtr buf, nuint count);

    // Raw fd-2 writes for crash-path diagnostics (GLibLogDiag's hooks). Console.Error is unsuitable there: it takes a lock and may allocate or throw while the process is mid-abort, and losing the message defeats the whole point. write(2) is async-signal-safe, and the byte[] path performs no allocation and cannot throw.
    public static unsafe void WriteCStringToStderr(IntPtr nulTerminated)
    {
        // Length computed in managed code (empty span for a null pointer), so a NULL from the native side cannot segfault the diagnostics path.
        var span = MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)nulTerminated);
        fixed (byte* p = span)
        {
            WriteAllTo(STDERR_FILENO, (IntPtr)p, (nuint)span.Length);
        }
    }

    public static void WriteToStderr(string text)
    {
        WriteToStderr(System.Text.Encoding.UTF8.GetBytes(text));
    }

    public static unsafe void WriteToStderr(byte[] bytes)
    {
        fixed (byte* p = bytes)
        {
            WriteAllTo(STDERR_FILENO, (IntPtr)p, (nuint)bytes.Length);
        }
    }

    // Testable core (see LibCTests): fd is a parameter so tests can point it at a pipe.
    internal static unsafe void WriteAllTo(int fd, byte[] bytes)
    {
        fixed (byte* p = bytes)
        {
            WriteAllTo(fd, (IntPtr)p, (nuint)bytes.Length);
        }
    }

    private static void WriteAllTo(int fd, IntPtr buf, nuint count)
    {
        nuint written = 0;
        while (written < count)
        {
            nint n = Write(fd, buf + (nint)written, count - written);
            if (n < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                if (errno == EINTR)
                {
                    continue;
                }
                if (errno == EAGAIN)
                {
                    // The fd is O_NONBLOCK and momentarily full (e.g. a sibling process set O_NONBLOCK on a shared tty). The data is a diagnostic we refuse to truncate; brief sleep-retry mirrors what a blocking fd would do without spinning a core.
                    System.Threading.Thread.Sleep(1);
                    continue;
                }
                // The fd is genuinely unwritable (closed, broken pipe). This IS the reporting channel — there is no further place to surface the failure.
                return;
            }
            if (n == 0)
            {
                return;
            }
            written += (nuint)n;
        }
    }

    private const int ENODATA = 61;
    private const int ENOTSUP = 95;

    // `value` is an out-buffer the kernel writes into; the generated marshalling pins the span for the call, and a size of 0 turns the call into a length query.
    [LibraryImport("libc.so.6", EntryPoint = "getxattr", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint GetXattrRaw(string path, string name, Span<byte> value, nuint size);

    // Reads one extended attribute as UTF-8 text. Null with the errno when it can't be read: ENODATA (not set) and ENOTSUP (filesystem has no xattrs) are ordinary answers; anything else is a real failure and is also reported to stderr, so the caller can carry on without the value but the failure isn't invisible.
    public static string? GetXattr(string path, string name, out int errno)
    {
        nint size = GetXattrRaw(path, name, Span<byte>.Empty, 0);
        if (size < 0)
        {
            errno = ReportXattrFailure(path, name);
            return null;
        }
        var buffer = new byte[size];
        nint got = GetXattrRaw(path, name, buffer, (nuint)buffer.Length);
        if (got < 0)
        {
            errno = ReportXattrFailure(path, name);
            return null;
        }
        errno = 0;
        return System.Text.Encoding.UTF8.GetString(buffer, 0, (int)got);
    }

    private static int ReportXattrFailure(string path, string name)
    {
        int errno = Marshal.GetLastPInvokeError();
        if (errno != ENODATA && errno != ENOTSUP)
        {
            Console.Error.WriteLine($"[vompl] getxattr({path}, {name}) failed: errno {errno}");
        }
        return errno;
    }

    [LibraryImport("libc.so.6", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial IntPtr RealPathRaw(string path, IntPtr resolved);

    [LibraryImport("libc.so.6", EntryPoint = "free")]
    private static partial void Free(IntPtr ptr);

    // Canonical absolute path with every symlink resolved (realpath(3)), or null with the errno when any component can't be resolved. Path.GetFullPath only normalizes syntax; this is the one that answers "which directory is this really".
    public static string? RealPath(string path, out int errno)
    {
        IntPtr resolved = RealPathRaw(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero)
        {
            errno = Marshal.GetLastPInvokeError();
            return null;
        }
        try
        {
            errno = 0;
            return Marshal.PtrToStringUTF8(resolved);
        }
        finally
        {
            Free(resolved);
        }
    }

    [LibraryImport("libc.so.6", EntryPoint = "getuid")]
    public static partial uint GetUid();
}
