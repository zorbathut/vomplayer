using System;
using System.Runtime.InteropServices;

namespace Vomplayer;

// Diagnostic hook on GLib's structured-log writer. When GLib emits an ERROR or CRITICAL — including the abort path used by g_assert / g_assertion_message — we print the message plus a managed stack trace before chaining through to g_log_writer_default. The default writer formats and prints to stderr as usual, and g_log_structured_array runs the fatal abort after the writer returns, so behavior is unchanged except for the extra context line(s) immediately before the abort. This exists because GLib aborts give no managed frame; without this hook, "GLib-GObject:ERROR ... assertion failed" is a dead end.
internal static partial class GLibLogDiag
{
    private const string GLibLib = "libglib-2.0.so.0";

    // GLogLevelFlags bits. We only react to fatal/loud levels; warnings are too noisy for stack traces. G_LOG_FLAG_FATAL is the bit set on a per-message basis when the message is being treated as fatal — covers user code that promoted a level via g_log_set_fatal_mask, so a warning that's about to abort still gets a trace.
    private const int GLogFlagFatal = 1 << 1;
    private const int GLogLevelError = 1 << 2;
    private const int GLogLevelCritical = 1 << 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct GLogField
    {
        public IntPtr Name;
        public IntPtr Value;
        public IntPtr Length; // gssize; -1 means NUL-terminated string
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GLogWriterFunc(int logLevel, IntPtr fields, UIntPtr nFields, IntPtr userData);

    [LibraryImport(GLibLib, EntryPoint = "g_log_set_writer_func")]
    private static partial void GLogSetWriterFunc(IntPtr writerFunc, IntPtr userData, IntPtr destroyNotify);

    [LibraryImport(GLibLib, EntryPoint = "g_log_writer_default")]
    private static partial int GLogWriterDefault(int logLevel, IntPtr fields, UIntPtr nFields, IntPtr userData);

    // Held statically so the marshalled function pointer stays valid for the process lifetime — GLib invokes us asynchronously from any thread, including the abort path.
    private static GLogWriterFunc? writerFunc;

    // Re-entry can happen if Console.Error.WriteLine itself trips a GLib log on a misconfigured stderr (rare, but cheap to guard). Without this, a log inside the writer would recurse and stack-overflow before reaching abort.
    [ThreadStatic] private static bool reentryGuard;

    // Install at process startup, before any GTK call. GLib forbids calling g_log_set_writer_func more than once (it g_error's on the second call), so we no-op if already installed.
    public static void Install()
    {
        if (writerFunc != null)
        {
            return;
        }
        writerFunc = OnGLibLog;
        GLogSetWriterFunc(Marshal.GetFunctionPointerForDelegate(writerFunc), IntPtr.Zero, IntPtr.Zero);
    }

    private static int OnGLibLog(int logLevel, IntPtr fields, UIntPtr nFields, IntPtr userData)
    {
        if (reentryGuard)
        {
            return GLogWriterDefault(logLevel, fields, nFields, userData);
        }
        reentryGuard = true;
        try
        {
            int interesting = logLevel & (GLogLevelError | GLogLevelCritical | GLogFlagFatal);
            if (interesting != 0)
            {
                string severity = (logLevel & GLogLevelError) != 0 ? "ERROR"
                    : (logLevel & GLogLevelCritical) != 0 ? "CRITICAL"
                    : "FATAL";
                string? msg = ExtractField(fields, nFields, "MESSAGE");
                string? domain = ExtractField(fields, nFields, "GLIB_DOMAIN");
                string? codeFile = ExtractField(fields, nFields, "CODE_FILE");
                string? codeLine = ExtractField(fields, nFields, "CODE_LINE");
                string? codeFunc = ExtractField(fields, nFields, "CODE_FUNC");
                int tid = Environment.CurrentManagedThreadId;
                Console.Error.WriteLine($"[vomplayer] GLib {severity} (tid={tid}, domain={domain ?? "?"}): {msg ?? "<no MESSAGE>"}");
                if (codeFile != null || codeLine != null || codeFunc != null)
                {
                    Console.Error.WriteLine($"[vomplayer] GLib {severity} site: {codeFile ?? "?"}:{codeLine ?? "?"} in {codeFunc ?? "?"}");
                }
                Console.Error.WriteLine($"[vomplayer] managed stack at GLib {severity}:");
                Console.Error.WriteLine(Environment.StackTrace);
            }
        }
        catch (Exception ex)
        {
            // Never throw out of a writer func — would unwind through native frames. Surface the diagnostic failure but don't let it propagate.
            try
            {
                Console.Error.WriteLine($"[vomplayer] GLibLogDiag writer threw: {ex}");
            }
            catch
            {
                // Stderr itself is gone; nothing more we can do.
            }
        }
        finally
        {
            reentryGuard = false;
        }
        return GLogWriterDefault(logLevel, fields, nFields, userData);
    }

    private static unsafe string? ExtractField(IntPtr fields, UIntPtr nFields, string targetName)
    {
        var ptr = (GLogField*)fields;
        ulong count = (ulong)nFields;
        for (ulong i = 0; i < count; i++)
        {
            var f = ptr[i];
            string? name = Marshal.PtrToStringUTF8(f.Name);
            if (name != targetName)
            {
                continue;
            }
            long length = f.Length.ToInt64();
            if (length < 0)
            {
                return Marshal.PtrToStringUTF8(f.Value);
            }
            if (length == 0)
            {
                return string.Empty;
            }
            return Marshal.PtrToStringUTF8(f.Value, (int)length);
        }
        return null;
    }
}
