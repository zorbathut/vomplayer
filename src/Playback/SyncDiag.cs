using System;
using System.Diagnostics;
using System.Globalization;

namespace Vomplayer.Playback;

// Diagnostic helper for the PiP sync-mode seek/step coordinator. Gated on the VOMPL_LOG_SYNC=1 environment variable so production runs pay nothing. When enabled, every coordinator entry, per-context dispatch, mpv-bound command, and time-pos update is printed to stderr with a relative-millisecond timestamp and a per-Playback tag ("primary" / "secondary"). The tag is set by the wiring code that knows which slot a Playback is in (ViewModelMain ctor for primary, EnablePip for secondary) — Playback itself doesn't know.
internal static class SyncDiag
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("VOMPL_LOG_SYNC") == "1";
    private static readonly long startTicks = Stopwatch.GetTimestamp();

    public static void Log(string message)
    {
        if (!Enabled)
        {
            return;
        }
        long elapsedMs = (Stopwatch.GetTimestamp() - startTicks) * 1000 / Stopwatch.Frequency;
        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[vompl sync +{elapsedMs,7}ms] {message}"));
    }
}
