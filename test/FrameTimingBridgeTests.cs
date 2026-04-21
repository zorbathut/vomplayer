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
        var bridge = new FrameTimingBridge(logEnabled: false, logSink: TextWriter.Null);
        bridge.OnPresented(1_000_000_000, 0);
        Assert.That(bridge.RingCount, Is.EqualTo(0));
    }

    [Test]
    public void SubsequentPresentsProduceDeltas()
    {
        var bridge = new FrameTimingBridge(logEnabled: false, logSink: TextWriter.Null);
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
        var bridge = new FrameTimingBridge(logEnabled: false, logSink: TextWriter.Null);
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
        var bridge = new FrameTimingBridge(logEnabled: false, logSink: TextWriter.Null);
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
        var bridge = new FrameTimingBridge(logEnabled: false, logSink: TextWriter.Null);
        bridge.OnDiscarded();
        bridge.OnDiscarded();
        Assert.That(bridge.StatsDiscarded, Is.EqualTo(2));
    }

    [Test]
    public void RingWrapsAtCapacity()
    {
        var bridge = new FrameTimingBridge(logEnabled: false, logSink: TextWriter.Null);
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
        var bridge = new FrameTimingBridge(logEnabled: false, logSink: TextWriter.Null);
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
        var bridge = new FrameTimingBridge(logEnabled: false, logSink: TextWriter.Null);
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
    public void LeaveClearsActiveOutputOnlyIfMatching()
    {
        var bridge = new FrameTimingBridge(logEnabled: false, logSink: TextWriter.Null);
        bridge.OnEnter(5);
        bridge.OnLeave(7);
        Assert.That(bridge.ActiveOutput, Is.EqualTo((uint)5));
        bridge.OnLeave(5);
        Assert.That(bridge.ActiveOutput, Is.Null);
    }

    [Test]
    public void EnterIsFirstWinsOnMultiOutput()
    {
        // Multi-monitor subsurface straddling two panels: compositor fires enter per-output. We keep the first so the classifier has a stable "home" reference until the subsurface actually leaves the primary.
        var bridge = new FrameTimingBridge(logEnabled: false, logSink: TextWriter.Null);
        bridge.OnEnter(5);
        bridge.OnEnter(7);
        Assert.That(bridge.ActiveOutput, Is.EqualTo((uint)5));
    }

    [Test]
    public void LogLineEmittedAtWindowBoundary()
    {
        WaylandOutputRegistry.OnOutputMode(1, 60000);
        var sink = new StringWriter();
        var bridge = new FrameTimingBridge(logEnabled: true, logSink: sink);
        bridge.OnEnter(1);

        ulong t = 1_000_000_000;
        bridge.OnPresented(t, 0);
        for (int i = 1; i <= FrameTimingBridge.StatsWindow; i++)
        {
            bridge.OnPresented(t + (ulong)i * NsPerFrame60Hz, 0);
        }

        var output = sink.ToString();
        Assert.That(output, Does.Contain($"[vom_wayland] presentation {FrameTimingBridge.StatsWindow} frames"));
        // Invariant-culture decimals: the log line must use '.' not ',' regardless of host locale.
        Assert.That(output, Does.Contain("nominal=60.00Hz"));
    }

    [Test]
    public void LogLineSuppressedWhenDisabled()
    {
        WaylandOutputRegistry.OnOutputMode(1, 60000);
        var sink = new StringWriter();
        var bridge = new FrameTimingBridge(logEnabled: false, logSink: sink);
        bridge.OnEnter(1);
        ulong t = 1_000_000_000;
        for (int i = 0; i <= FrameTimingBridge.StatsWindow + 10; i++)
        {
            bridge.OnPresented(t + (ulong)i * NsPerFrame60Hz, 0);
        }
        Assert.That(sink.ToString(), Is.Empty);
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
            var bridge = new FrameTimingBridge(logEnabled: true, logSink: sink);
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
