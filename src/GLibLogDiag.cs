using System;
using System.Runtime.InteropServices;

namespace Vomplayer;

// Diagnostic hooks that attach a managed stack trace to GLib's fatal paths, because GLib aborts give no managed frame — without them, "GLib-GObject:ERROR ... assertion failed" is a dead end. Two hooks, because GLib has two disjoint fatal output paths:
//  - The structured-log writer (g_log_set_writer_func) sees g_error(), CRITICALs, and levels promoted via g_log_set_fatal_mask. We print message + managed stack, then chain to g_log_writer_default, which prints to stderr as usual; g_log_structured_array runs the fatal abort after the writer returns, so behavior is unchanged except for the extra context lines.
//  - The g_assert* family does NOT go through the writer: g_assertion_message emits "**\n%s\n" via g_printerr and then aborts (verified against libglib 2.88.1). So we also install a g_set_printerr_handler hook. Installing a printerr handler makes GLib stop writing to stderr itself, so that hook is responsible for the passthrough — done with raw write(2) so the original message survives even if the managed side is wedged mid-abort.
// All output from these hooks goes through raw write(2) (LibC.WriteToStderr), never Console.Error: both hooks can run mid-abort, where Console's lock and allocations are liabilities, and mixing two stderr channels risks interleaving anyway. One deliberate difference from GLib's default printerr path: GLib converts to the console charset before fputs; we write the UTF-8 bytes as-is (a non-UTF-8 console would mojibake — acceptable for a Linux-only diagnostics path).
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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GPrintFunc(IntPtr message);

    [LibraryImport(GLibLib, EntryPoint = "g_set_printerr_handler")]
    private static partial IntPtr GSetPrinterrHandler(IntPtr func);

    // Held statically so the marshalled function pointers stay valid for the process lifetime — GLib invokes us asynchronously from any thread, including the abort path.
    private static GLogWriterFunc? writerFunc;
    private static GPrintFunc? printerrFunc;

    // Whatever printerr handler was installed before ours. Given Install()'s before-any-GLib-call contract this is always null in practice (GLib's default is a NULL handler slot); chained anyway because the API hands it to us and silently discarding a handler someone did install would be worse.
    private static GPrintFunc? previousPrinterrHandler;

    // Pre-encoded (u8 literal, no runtime encoding step) so the last-resort failure paths below cannot themselves fail; LibC.WriteToStderr(byte[]) performs no allocation and cannot throw.
    private static readonly byte[] hookFailureMarker = "[vomplayer] GLibLogDiag hook failed while reporting a failure\n"u8.ToArray();

    // Install at process startup, before any GTK call. GLib forbids calling g_log_set_writer_func more than once (it g_error's on the second call), so we no-op if already installed.
    public static void Install()
    {
        if (writerFunc != null)
        {
            return;
        }
        writerFunc = OnGLibLog;
        GLogSetWriterFunc(Marshal.GetFunctionPointerForDelegate(writerFunc), IntPtr.Zero, IntPtr.Zero);

        printerrFunc = OnGPrinterr;
        IntPtr previous = GSetPrinterrHandler(Marshal.GetFunctionPointerForDelegate(printerrFunc));
        previousPrinterrHandler = previous != IntPtr.Zero ? Marshal.GetDelegateForFunctionPointer<GPrintFunc>(previous) : null;
    }

    private static void OnGPrinterr(IntPtr message)
    {
        // Passthrough first, straight from the native buffer: with a handler installed GLib writes nothing to stderr itself, and the original message must get out even if the managed formatting below fails. No reentry guard needed — nothing in this handler calls g_printerr or GLib logging (raw write(2) and pure managed string work only), so it cannot recurse into itself.
        if (previousPrinterrHandler != null)
        {
            previousPrinterrHandler(message);
        }
        else
        {
            LibC.WriteCStringToStderr(message);
        }

        try
        {
            string? msg = Marshal.PtrToStringUTF8(message);
            if (msg == null)
            {
                return;
            }
            string augmentation = BuildPrinterrAugmentation(msg, Environment.CurrentManagedThreadId, Environment.StackTrace);
            if (augmentation.Length != 0)
            {
                LibC.WriteToStderr(augmentation);
            }
        }
        catch (Exception ex)
        {
            // Never throw out of a native callback — would unwind through native frames. Report the failure; the final fallback is a pre-encoded write that cannot throw.
            try
            {
                LibC.WriteToStderr($"[vomplayer] GLibLogDiag printerr hook threw: {ex}\n");
            }
            catch
            {
                LibC.WriteToStderr(hookFailureMarker);
            }
        }
    }

    // Pure classification + formatting for the printerr hook, split out for unit testing. g_assertion_message's output is the only printerr traffic that warrants a stack trace, and its "**\n" prefix is unique to it.
    internal static string BuildPrinterrAugmentation(string message, int threadId, string stackTrace)
    {
        if (!message.StartsWith("**\n", StringComparison.Ordinal))
        {
            return string.Empty;
        }
        return $"[vomplayer] managed stack at GLib assertion (tid={threadId}):\n{stackTrace}\n";
    }

    private static int OnGLibLog(int logLevel, IntPtr fields, UIntPtr nFields, IntPtr userData)
    {
        // No reentry guard needed — same reasoning as OnGPrinterr: raw write(2) plus pure managed string work cannot trip another GLib log. (The tail call to g_log_writer_default runs after our block regardless and does not re-invoke the writer func.)
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
                string site = codeFile != null || codeLine != null || codeFunc != null
                    ? $"[vomplayer] GLib {severity} site: {codeFile ?? "?"}:{codeLine ?? "?"} in {codeFunc ?? "?"}\n"
                    : "";
                LibC.WriteToStderr($"[vomplayer] GLib {severity} (tid={tid}, domain={domain ?? "?"}): {msg ?? "<no MESSAGE>"}\n{site}[vomplayer] managed stack at GLib {severity}:\n{Environment.StackTrace}\n");
            }
        }
        catch (Exception ex)
        {
            // Never throw out of a writer func — would unwind through native frames. Report the failure; the final fallback is a pre-encoded write that cannot throw.
            try
            {
                LibC.WriteToStderr($"[vomplayer] GLibLogDiag writer threw: {ex}\n");
            }
            catch
            {
                LibC.WriteToStderr(hookFailureMarker);
            }
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
