using System.Globalization;
using System.IO;
using System.Threading;
using Vomplayer.Wayland;

namespace Vomplayer.Tests;

[TestFixture]
public class FrameTimingBridgeTests
{
    // 60 Hz → 16666667 ns per frame.
    private const ulong NsPerFrame60Hz = 16_666_667;

    [SetUp]
    public void Reset()
    {
        WaylandOutputRegistry.Reset();
    }

    [Test]
    public void StatsWindowCoversRing()
    {
        // EmitLogLine calls the classifier, which requires a full ring. This invariant replaces a runtime static-ctor throw — cheaper and louder.
        Assert.That(FrameTimingBridge.StatsWindow, Is.GreaterThanOrEqualTo(FrameTimingBridge.RingSize));
    }

    [Test]
    public void FirstPresentIsSkippedNoDelta()
    {
        var bridge = new FrameTimingBridge(logEnabled: false, logDeltas: false, logSink: TextWriter.Null);
        bridge.OnPresented(1_000_000_000, 0);
        Assert.That(bridge.RingCount, Is.EqualTo(0));
    }

    [Test]
    public void SubsequentPresentsProduceDeltas()
    {
        var bridge = new FrameTimingBridge(logEnabled: false, logDeltas: false, logSink: TextWriter.Null);
        ulong t = 1_000_000_000;
        bridge.OnPresented(t, 0);
        bridge.OnPresented(t + NsPerFrame60Hz, 0);
        bridge.OnPresented(t + 2 * NsPerFrame60Hz, 0);
        Assert.That(bridge.RingCount, Is.EqualTo(2));
        Assert.That(bridge.Ring[0], Is.EqualTo(16666u));
        Assert.That(bridge.Ring[1], Is.EqualTo(16666u));
    }

    [Test]
    public void ZeroDeltaIsSkippedButPrevAdvances()
    {
        var bridge = new FrameTimingBridge(logEnabled: false, logDeltas: false, logSink: TextWriter.Null);
        bridge.OnPresented(1_000_000_000, 0);
        // Same timestamp — no delta, don't push. But prevNs should still advance (or rather, remain valid) so the next real present gets a correct delta.
        bridge.OnPresented(1_000_000_000, 0);
        Assert.That(bridge.RingCount, Is.EqualTo(0));
        bridge.OnPresented(1_000_000_000 + NsPerFrame60Hz, 0);
        Assert.That(bridge.RingCount, Is.EqualTo(1));
        Assert.That(bridge.Ring[0], Is.EqualTo(16666u));
    }

    [Test]
    public void DiscardClearsHasPrev()
    {
        var bridge = new FrameTimingBridge(logEnabled: false, logDeltas: false, logSink: TextWriter.Null);
        bridge.OnPresented(1_000_000_000, 0);
        bridge.OnPresented(1_000_000_000 + NsPerFrame60Hz, 0);
        Assert.That(bridge.RingCount, Is.EqualTo(1));
        bridge.OnDiscarded();
        // Next present after a discard must not produce a delta computed against the pre-discard timestamp.
        bridge.OnPresented(1_000_000_000 + 5 * NsPerFrame60Hz, 0);
        Assert.That(bridge.RingCount, Is.EqualTo(1));
    }

    [Test]
    public void DiscardIncrementsDiscardCounter()
    {
        var bridge = new FrameTimingBridge(logEnabled: false, logDeltas: false, logSink: TextWriter.Null);
        bridge.OnDiscarded();
        bridge.OnDiscarded();
        Assert.That(bridge.StatsDiscarded, Is.EqualTo(2));
    }

    [Test]
    public void RingWrapsAtCapacity()
    {
        var bridge = new FrameTimingBridge(logEnabled: false, logDeltas: false, logSink: TextWriter.Null);
        ulong t = 1_000_000_000;
        bridge.OnPresented(t, 0);
        // Push 80 samples; ring holds 60. Count caps at 60.
        for (int i = 1; i <= 80; i++)
        {
            bridge.OnPresented(t + (ulong)i * NsPerFrame60Hz, 0);
        }
        Assert.That(bridge.RingCount, Is.EqualTo(60));
    }

    [Test]
    public void ClassificationUnknownWithoutActiveOutput()
    {
        var bridge = new FrameTimingBridge(logEnabled: false, logDeltas: false, logSink: TextWriter.Null);
        ulong t = 1_000_000_000;
        bridge.OnPresented(t, 0);
        for (int i = 1; i <= 60; i++)
        {
            bridge.OnPresented(t + (ulong)i * NsPerFrame60Hz, 0);
        }
        var cls = bridge.GetClassification(out var hz);
        Assert.That(cls, Is.EqualTo(VrrClassification.Unknown));
        Assert.That(hz, Is.EqualTo(0));
    }

    [Test]
    public void ClassificationUsesActiveOutputMode()
    {
        WaylandOutputRegistry.OnOutputMode(5, 60000);
        var bridge = new FrameTimingBridge(logEnabled: false, logDeltas: false, logSink: TextWriter.Null);
        bridge.OnEnter(5);
        ulong t = 1_000_000_000;
        bridge.OnPresented(t, 0);
        for (int i = 1; i <= 60; i++)
        {
            bridge.OnPresented(t + (ulong)i * NsPerFrame60Hz, 0);
        }
        var cls = bridge.GetClassification(out var hz);
        Assert.That(hz, Is.EqualTo(6000));
        // Perfectly steady grid stream → CantTell per the classifier's intent. Whatever classification, hzCenti must reflect the mode.
        Assert.That(cls, Is.Not.EqualTo(VrrClassification.Unknown));
    }

    [Test]
    public void MeasuredHzCentiZeroOnEmptyRing()
    {
        var bridge = new FrameTimingBridge(logEnabled: false, logDeltas: false, logSink: TextWriter.Null);
        Assert.That(bridge.MeasuredHzCenti, Is.EqualTo(0));
    }

    [Test]
    public void MeasuredHzCentiTracksDeltaMean()
    {
        // 50 fps stream → 20000 µs deltas → 5000 centi-Hz. This is the case that exposes the "panel mode != actual rate" overlay bug — the diagnostic overlay reads MeasuredHzCenti so it reports 50.00 Hz instead of the panel's 60.00 Hz nominal mode.
        var bridge = new FrameTimingBridge(logEnabled: false, logDeltas: false, logSink: TextWriter.Null);
        const ulong NsPer50Hz = 20_000_000;
        ulong t = 1_000_000_000;
        bridge.OnPresented(t, 0);
        for (int i = 1; i <= 60; i++)
        {
            bridge.OnPresented(t + (ulong)i * NsPer50Hz, 0);
        }
        Assert.That(bridge.MeasuredHzCenti, Is.EqualTo(5000));
    }

    [Test]
    public void MeasuredHzCentiAvailableOnPartialRing()
    {
        // The classifier requires a full ring before it'll return non-Unknown, but MeasuredHzCenti has no such gate — it's a pure mean over whatever's in the ring. The diagnostic overlay short-circuits via classification; this pins the bridge contract independently so a future overlay change doesn't get blindsided.
        var bridge = new FrameTimingBridge(logEnabled: false, logDeltas: false, logSink: TextWriter.Null);
        const ulong NsPer50Hz = 20_000_000;
        ulong t = 1_000_000_000;
        bridge.OnPresented(t, 0);
        for (int i = 1; i <= 10; i++)
        {
            bridge.OnPresented(t + (ulong)i * NsPer50Hz, 0);
        }
        Assert.That(bridge.MeasuredHzCenti, Is.EqualTo(5000));
    }

    [Test]
    public void MeasuredHzCentiRoundsCorrectlyForSixtyHz()
    {
        // 60 Hz frames are 16667 µs (the rounded representation of 16666.67). 60 of those sum to 1,000,020 µs; mean 16667; 1e6/16667 = 59.998800... Hz → rounds to 6000 centi-Hz, not 5999. Pins the rounding policy at the boundary case that would otherwise off-by-one to "59.99 Hz" in the overlay.
        var bridge = new FrameTimingBridge(logEnabled: false, logDeltas: false, logSink: TextWriter.Null);
        ulong t = 1_000_000_000;
        bridge.OnPresented(t, 0);
        for (int i = 1; i <= 60; i++)
        {
            bridge.OnPresented(t + (ulong)i * NsPerFrame60Hz, 0);
        }
        Assert.That(bridge.MeasuredHzCenti, Is.EqualTo(6000));
    }

    [Test]
    public void LeaveClearsActiveOutputOnlyIfMatching()
    {
        var bridge = new FrameTimingBridge(logEnabled: false, logDeltas: false, logSink: TextWriter.Null);
        bridge.OnEnter(5);
        bridge.OnLeave(7);
        Assert.That(bridge.ActiveOutput, Is.EqualTo((uint)5));
        bridge.OnLeave(5);
        Assert.That(bridge.ActiveOutput, Is.Null);
    }

    [Test]
    public void EnterIsFirstWinsOnMultiOutput()
    {
        // Multi-monitor subsurface straddling two panels: compositor fires enter per-output. We keep the earliest-still-entered so the classifier has a stable "home" reference until the subsurface actually leaves the primary.
        var bridge = new FrameTimingBridge(logEnabled: false, logDeltas: false, logSink: TextWriter.Null);
        bridge.OnEnter(5);
        bridge.OnEnter(7);
        Assert.That(bridge.ActiveOutput, Is.EqualTo((uint)5));
    }

    [Test]
    public void CrossMonitorDragReassignsActiveOutput()
    {
        // Drag scenario: window is on A, spans A+B during the drag, then settles on B. wl_surface.enter(B) fires during the span, then wl_surface.leave(A) once the drag completes. ActiveOutput must become B — the old first-wins+clear-on-match policy would have latched null permanently here.
        var bridge = new FrameTimingBridge(logEnabled: false, logDeltas: false, logSink: TextWriter.Null);
        bridge.OnEnter(1);
        bridge.OnEnter(2);
        bridge.OnLeave(1);
        Assert.That(bridge.ActiveOutput, Is.EqualTo((uint)2));
    }

    [Test]
    public void DuplicateEnterIsIdempotent()
    {
        var bridge = new FrameTimingBridge(logEnabled: false, logDeltas: false, logSink: TextWriter.Null);
        bridge.OnEnter(3);
        bridge.OnEnter(3);
        bridge.OnLeave(3);
        Assert.That(bridge.ActiveOutput, Is.Null);
    }

    [Test]
    public void ActiveOutputsChangedFiresOnRealMutationsOnly()
    {
        var bridge = new FrameTimingBridge(logEnabled: false, logDeltas: false, logSink: TextWriter.Null);
        int fires = 0;
        bridge.ActiveOutputsChanged += () => fires++;
        bridge.OnEnter(1);     // fires
        bridge.OnEnter(1);     // no-op duplicate
        bridge.OnEnter(2);     // fires
        bridge.OnLeave(99);    // no-op (not present)
        bridge.OnLeave(1);     // fires
        bridge.OnLeave(2);     // fires
        Assert.That(fires, Is.EqualTo(4));
    }

    [Test]
    public void LogLineEmittedAtWindowBoundary()
    {
        WaylandOutputRegistry.OnOutputMode(1, 60000);
        var sink = new StringWriter();
        var bridge = new FrameTimingBridge(logEnabled: true, logDeltas: false, logSink: sink);
        bridge.OnEnter(1);

        ulong t = 1_000_000_000;
        bridge.OnPresented(t, 0);
        for (int i = 1; i <= FrameTimingBridge.StatsWindow; i++)
        {
            bridge.OnPresented(t + (ulong)i * NsPerFrame60Hz, 0);
        }

        var output = sink.ToString();
        Assert.That(output, Does.Contain($"[vompl] presentation {FrameTimingBridge.StatsWindow} frames"));
        // Invariant-culture decimals: the log line must use '.' not ',' regardless of host locale.
        Assert.That(output, Does.Contain("nominal=60.00Hz"));
    }

    [Test]
    public void LogLineSuppressedWhenDisabled()
    {
        WaylandOutputRegistry.OnOutputMode(1, 60000);
        var sink = new StringWriter();
        var bridge = new FrameTimingBridge(logEnabled: false, logDeltas: false, logSink: sink);
        bridge.OnEnter(1);
        ulong t = 1_000_000_000;
        for (int i = 0; i <= FrameTimingBridge.StatsWindow + 10; i++)
        {
            bridge.OnPresented(t + (ulong)i * NsPerFrame60Hz, 0);
        }
        Assert.That(sink.ToString(), Is.Empty);
    }

    [Test]
    public void DeltasLineEmittedWhenLogDeltasEnabled()
    {
        WaylandOutputRegistry.OnOutputMode(1, 60000);
        var sink = new StringWriter();
        var bridge = new FrameTimingBridge(logEnabled: true, logDeltas: true, logSink: sink);
        bridge.OnEnter(1);

        ulong t = 1_000_000_000;
        bridge.OnPresented(t, 0);
        for (int i = 1; i <= FrameTimingBridge.StatsWindow; i++)
        {
            bridge.OnPresented(t + (ulong)i * NsPerFrame60Hz, 0);
        }

        var lines = sink.ToString().Split('\n');
        string? deltasLine = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("[vompl] deltas (ms):"))
            {
                deltasLine = line;
                break;
            }
        }
        Assert.That(deltasLine, Is.Not.Null);
        // 60 Hz frames → every delta is 16.67 ms; StatsWindow of them on the deltas line.
        var values = deltasLine!.Substring("[vompl] deltas (ms):".Length).Trim().Split(' ');
        Assert.That(values.Length, Is.EqualTo(FrameTimingBridge.StatsWindow));
        foreach (var v in values)
        {
            Assert.That(v, Is.EqualTo("16.67"));
        }
    }

    [Test]
    public void DeltasLineResetsBetweenWindows()
    {
        // Second window's deltas must reflect second-window samples, not a stale prefix from the first window. Catches a regression where ResetStats forgets to zero statsFrames or statsDeltasMs isn't fully overwritten.
        WaylandOutputRegistry.OnOutputMode(1, 60000);
        var sink = new StringWriter();
        var bridge = new FrameTimingBridge(logEnabled: true, logDeltas: true, logSink: sink);
        bridge.OnEnter(1);

        // First window at 60 Hz.
        ulong t = 1_000_000_000;
        bridge.OnPresented(t, 0);
        for (int i = 1; i <= FrameTimingBridge.StatsWindow; i++)
        {
            bridge.OnPresented(t + (ulong)i * NsPerFrame60Hz, 0);
        }

        // Second window at 30 Hz (33.33 ms deltas). First 30 Hz sample's delta is computed against the final 60 Hz timestamp, so skip that boundary value when asserting.
        const ulong NsPer30Hz = 33_333_333;
        ulong t2 = t + (ulong)FrameTimingBridge.StatsWindow * NsPerFrame60Hz;
        for (int i = 1; i <= FrameTimingBridge.StatsWindow; i++)
        {
            bridge.OnPresented(t2 + (ulong)i * NsPer30Hz, 0);
        }

        var deltasLines = new System.Collections.Generic.List<string>();
        foreach (var line in sink.ToString().Split('\n'))
        {
            if (line.StartsWith("[vompl] deltas (ms):"))
            {
                deltasLines.Add(line);
            }
        }
        Assert.That(deltasLines.Count, Is.EqualTo(2));
        var secondValues = deltasLines[1].Substring("[vompl] deltas (ms):".Length).Trim().Split(' ');
        Assert.That(secondValues.Length, Is.EqualTo(FrameTimingBridge.StatsWindow));
        // Skip index 0 (boundary delta spanning the window transition). The rest are pure 30 Hz samples.
        for (int i = 1; i < secondValues.Length; i++)
        {
            Assert.That(secondValues[i], Is.EqualTo("33.33"));
        }
    }

    [Test]
    public void DeltasLineUsesInvariantCultureUnderNonInvariantHostCulture()
    {
        WaylandOutputRegistry.OnOutputMode(1, 60000);
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var sink = new StringWriter();
            var bridge = new FrameTimingBridge(logEnabled: true, logDeltas: true, logSink: sink);
            bridge.OnEnter(1);
            ulong t = 1_000_000_000;
            bridge.OnPresented(t, 0);
            for (int i = 1; i <= FrameTimingBridge.StatsWindow; i++)
            {
                bridge.OnPresented(t + (ulong)i * NsPerFrame60Hz, 0);
            }
            string? deltasLine = null;
            foreach (var line in sink.ToString().Split('\n'))
            {
                if (line.StartsWith("[vompl] deltas (ms):"))
                {
                    deltasLine = line;
                    break;
                }
            }
            Assert.That(deltasLine, Is.Not.Null);
            Assert.That(deltasLine!, Does.Not.Contain(","));
            Assert.That(deltasLine!, Does.Contain("16.67"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Test]
    public void DeltasLineSuppressedWhenLogDeltasDisabled()
    {
        WaylandOutputRegistry.OnOutputMode(1, 60000);
        var sink = new StringWriter();
        var bridge = new FrameTimingBridge(logEnabled: true, logDeltas: false, logSink: sink);
        bridge.OnEnter(1);
        ulong t = 1_000_000_000;
        bridge.OnPresented(t, 0);
        for (int i = 1; i <= FrameTimingBridge.StatsWindow; i++)
        {
            bridge.OnPresented(t + (ulong)i * NsPerFrame60Hz, 0);
        }
        Assert.That(sink.ToString(), Does.Not.Contain("[vompl] deltas"));
    }

    [Test]
    public void LogLineUsesInvariantCultureUnderNonInvariantHostCulture()
    {
        WaylandOutputRegistry.OnOutputMode(1, 60000);
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var sink = new StringWriter();
            var bridge = new FrameTimingBridge(logEnabled: true, logDeltas: false, logSink: sink);
            bridge.OnEnter(1);
            ulong t = 1_000_000_000;
            bridge.OnPresented(t, 0);
            for (int i = 1; i <= FrameTimingBridge.StatsWindow; i++)
            {
                bridge.OnPresented(t + (ulong)i * NsPerFrame60Hz, 0);
            }
            Assert.That(sink.ToString(), Does.Contain("nominal=60.00Hz"));
            Assert.That(sink.ToString(), Does.Not.Contain(","));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
