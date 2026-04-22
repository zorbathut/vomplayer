using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Vomplayer.Wayland;

// Per-surface accumulator for wp_presentation_feedback events. Owns:
//   - The refresh-delta ring the VRR classifier consumes.
//   - The rolling presentation-stats window the diagnostic log emits every StatsWindow frames.
//   - The active-output tracking that gates classification (without knowing which panel we're on, classifier returns Unknown rather than guess — on a multi-monitor host, silently picking the wrong nominal rate would silently mis-classify).
//
// All entry points are called on whichever thread drives the Wayland event queue. In this app that's always the GTK main thread — either the GMainContext dispatcher or the main thread running wl_display_roundtrip synchronously during init. No locking.
public sealed class FrameTimingBridge
{
    internal const int RingSize = 60;
    // The log line calls the classifier (which requires a full ring). StatsWindow must be >= RingSize so every log emission has something meaningful to classify. Verified by a compile-time-ish assert in FrameTimingBridgeTests.StatsWindowCoversRing.
    internal const int StatsWindow = 120;

    private readonly uint[] ringDeltaUs = new uint[RingSize];
    private int ringCount;
    private int ringHead;
    private bool hasPrev;
    private ulong prevNs;

    private int statsFrames;
    private ulong statsLastNs;
    private double statsDeltaSumMs;
    private double statsDeltaMinMs;
    private double statsDeltaMaxMs;
    private uint statsRefreshMinNs;
    private uint statsRefreshMaxNs;
    private int statsDiscarded;

    // Ordered list of currently-entered wl_outputs (earliest first). wl_surface.enter/leave are symmetric per output, so the list is the set of outputs the subsurface is on *right now*. ActiveOutput returns the earliest-still-entered so behavior matches the prior single-monitor first-wins semantics, but a cross-monitor drag (enter B → leave A) now correctly reports B rather than latching null forever.
    private readonly List<uint> enteredOutputs = new();
    private readonly bool logEnabled;
    private readonly TextWriter logSink;

    // Fires after a real mutation to the entered-outputs list (a new enter, or a leave that actually removed something). A duplicate enter or a leave-of-absent is a no-op and does not fire. Consumers that care which output is currently "the" output should re-read ActiveOutput on each fire.
    public event Action? ActiveOutputsChanged;

    public FrameTimingBridge()
        : this(
            logEnabled: Environment.GetEnvironmentVariable("VOMPL_LOG_PRESENTATION") == "1",
            logSink: Console.Error)
    {
    }

    // Internal-only ctor so tests can inject a StringWriter and flip log mode without mutating process env.
    internal FrameTimingBridge(bool logEnabled, TextWriter logSink)
    {
        this.logEnabled = logEnabled;
        this.logSink = logSink;
    }

    // Set by the wl_surface.enter trampoline. Idempotent: the compositor may repeat an enter for the same output; we dedupe so leave/enter counts don't drift.
    public void OnEnter(uint registryName)
    {
        if (!enteredOutputs.Contains(registryName))
        {
            enteredOutputs.Add(registryName);
            ActiveOutputsChanged?.Invoke();
        }
    }

    public void OnLeave(uint registryName)
    {
        if (enteredOutputs.Remove(registryName))
        {
            ActiveOutputsChanged?.Invoke();
        }
    }

    // wp_presentation_feedback.presented trampoline. tvNs is the composite (tv_sec_hi,tv_sec_lo,tv_nsec) timestamp; refreshNs is the compositor's expected next-frame interval (0 or varying on VRR-engaged outputs — kept only as diagnostic).
    public void OnPresented(ulong tvNs, uint refreshNs)
    {
        PushRing(tvNs);
        AccumulateStats(tvNs, refreshNs);
    }

    public void OnDiscarded()
    {
        statsDiscarded++;
        // Don't let the next presented event compute its delta against the pre-discard timestamp — that would produce a spuriously-large delta. Clearing hasPrev causes the next PushRing to skip the sample; we pick up clean deltas from the one after.
        hasPrev = false;
    }

    public VrrClassification GetClassification(out int hzCenti)
    {
        hzCenti = 0;
        if (enteredOutputs.Count == 0)
        {
            return VrrClassification.Unknown;
        }
        if (!WaylandOutputRegistry.TryGetMode(enteredOutputs[0], out var mhz))
        {
            return VrrClassification.Unknown;
        }
        var (cls, hz) = VrrClassifier.Classify(ringDeltaUs.AsSpan(0, ringCount), mhz);
        hzCenti = hz;
        return cls;
    }

    // Mean of the ring's inter-frame deltas, expressed as centi-Hz (e.g. 5000 = 50.00 Hz). Distinct from GetClassification's hzCenti, which is the panel's nominal mode rate from wl_output.mode — useful for the log line's "nominal=" field, but misleading in the user-facing overlay when VRR is engaged at a non-mode rate (e.g. 50 fps content scanned out at ~50Hz on a 60Hz panel). Returns 0 when the ring is empty or sums to zero.
    public int MeasuredHzCenti
    {
        get
        {
            if (ringCount == 0)
            {
                return 0;
            }
            ulong sum = 0;
            for (int i = 0; i < ringCount; i++)
            {
                sum += ringDeltaUs[i];
            }
            if (sum == 0)
            {
                return 0;
            }
            return (int)Math.Round(100_000_000.0 * ringCount / sum);
        }
    }

    private void PushRing(ulong nowNs)
    {
        uint deltaUs = 0;
        if (hasPrev && nowNs > prevNs)
        {
            ulong d = (nowNs - prevNs) / 1000;
            deltaUs = d > uint.MaxValue ? uint.MaxValue : (uint)d;
        }
        prevNs = nowNs;
        hasPrev = true;
        // Skip when no valid delta (first sample, zero-delta, or out-of-order timestamp against prev). prevNs still advances so the next delta computes against the newest timestamp rather than staying anchored to a stale one.
        if (deltaUs == 0)
        {
            return;
        }
        ringDeltaUs[ringHead] = deltaUs;
        ringHead = (ringHead + 1) % RingSize;
        if (ringCount < RingSize)
        {
            ringCount++;
        }
    }

    private void AccumulateStats(ulong nowNs, uint refreshNs)
    {
        if (statsLastNs != 0)
        {
            double deltaMs = (nowNs - statsLastNs) / 1e6;
            statsDeltaSumMs += deltaMs;
            if (statsFrames == 0 || deltaMs < statsDeltaMinMs)
            {
                statsDeltaMinMs = deltaMs;
            }
            if (statsFrames == 0 || deltaMs > statsDeltaMaxMs)
            {
                statsDeltaMaxMs = deltaMs;
            }
            statsFrames++;
        }
        statsLastNs = nowNs;
        if (refreshNs != 0)
        {
            if (statsRefreshMinNs == 0 || refreshNs < statsRefreshMinNs)
            {
                statsRefreshMinNs = refreshNs;
            }
            if (refreshNs > statsRefreshMaxNs)
            {
                statsRefreshMaxNs = refreshNs;
            }
        }
        if (statsFrames < StatsWindow)
        {
            return;
        }
        if (logEnabled)
        {
            EmitLogLine();
        }
        ResetStats();
        statsLastNs = nowNs;
    }

    private void EmitLogLine()
    {
        double avgMs = statsDeltaSumMs / statsFrames;
        double avgHz = avgMs > 0 ? 1000.0 / avgMs : 0;
        double minHz = statsDeltaMaxMs > 0 ? 1000.0 / statsDeltaMaxMs : 0;
        double maxHz = statsDeltaMinMs > 0 ? 1000.0 / statsDeltaMinMs : 0;
        double refreshMinHz = statsRefreshMaxNs > 0 ? 1e9 / statsRefreshMaxNs : 0;
        double refreshMaxHz = statsRefreshMinNs > 0 ? 1e9 / statsRefreshMinNs : 0;
        // Route through GetClassification so the output-membership validation (TryGetMode) governs the reported nominal rate too.
        var cls = GetClassification(out var hzCenti);
        string clsName = cls switch
        {
            VrrClassification.Vrr => "VRR",
            VrrClassification.Fixed => "FIXED",
            VrrClassification.CantTell => "CANT-TELL",
            _ => "UNKNOWN",
        };
        double nominalHz = hzCenti > 0 ? hzCenti / 100.0 : 0;
        logSink.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "[vompl] presentation {0} frames: delta avg={1:F2}ms ({2:F1}Hz) range=[{3:F2}..{4:F2}]ms ([{5:F1}..{6:F1}]Hz) refresh=[{7:F1}..{8:F1}]Hz nominal={9:F2}Hz classification={10} discarded={11}",
            statsFrames, avgMs, avgHz,
            statsDeltaMinMs, statsDeltaMaxMs, minHz, maxHz,
            refreshMinHz, refreshMaxHz, nominalHz, clsName, statsDiscarded));
    }

    private void ResetStats()
    {
        statsFrames = 0;
        statsDeltaSumMs = 0;
        statsDeltaMinMs = 0;
        statsDeltaMaxMs = 0;
        statsRefreshMinNs = 0;
        statsRefreshMaxNs = 0;
        statsDiscarded = 0;
    }

    // Test introspection.
    internal int RingCount
    {
        get
        {
            return ringCount;
        }
    }

    internal ReadOnlySpan<uint> Ring
    {
        get
        {
            return ringDeltaUs.AsSpan(0, ringCount);
        }
    }

    internal int StatsDiscarded
    {
        get
        {
            return statsDiscarded;
        }
    }

    internal uint? ActiveOutput
    {
        get
        {
            if (enteredOutputs.Count == 0)
            {
                return null;
            }
            return enteredOutputs[0];
        }
    }
}
