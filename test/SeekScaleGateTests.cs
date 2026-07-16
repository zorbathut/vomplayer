using Vomplayer.Controls;

namespace Vomplayer.Tests;

// The seek scrubber's press/release/settle state machine, previously untestable inside the GTK shell. Each scenario below was documented in prose on the old field comments; now they're pinned.
[TestFixture]
public class SeekScaleGateTests
{
    [Test]
    public void IdleVmUpdatesPushStraightThrough()
    {
        var g = new SeekScaleGate();
        Assert.That(g.OnVmSeekValue(0.25, userPressing: false), Is.True);
        Assert.That(g.CurrentVmSeekValue, Is.EqualTo(0.25));
    }

    [Test]
    public void VmUpdatesAreSuppressedWhileHolding()
    {
        var g = new SeekScaleGate();
        g.OnVmSeekValue(0.2, userPressing: false);
        g.OnPress();
        Assert.That(g.OnVmSeekValue(0.21, userPressing: true), Is.False, "the scale must not fight the user's drag");
        Assert.That(g.CurrentVmSeekValue, Is.EqualTo(0.21), "the value is still cached for the eventual push");
    }

    [Test]
    public void ReleaseWithNoInFlightSeekPushesImmediately()
    {
        var g = new SeekScaleGate();
        g.OnVmSeekValue(0.2, userPressing: false);
        g.OnPress();
        Assert.That(g.OnRelease(isSeeking: false), Is.True);
    }

    [Test]
    public void SettleGateHoldsUntilPositionMovesOffThePressBaseline()
    {
        // The flicker this machine exists to prevent: mpv can echo `seeking=false` before the new time-pos, so a stale SeekValue equal to the pre-seek baseline must be suppressed; the first value that differs opens the gate.
        var g = new SeekScaleGate();
        g.OnVmSeekValue(0.2, userPressing: false);
        g.OnPress();                                    // baseline = 0.2
        Assert.That(g.OnRelease(isSeeking: true), Is.False, "in-flight seek arms the settle gate");
        Assert.That(g.OnVmSeekValue(0.2, userPressing: false), Is.False, "stale pre-seek echo suppressed");
        Assert.That(g.OnVmSeekValue(0.7, userPressing: false), Is.True, "the seek landed — resume tracking");
        Assert.That(g.OnVmSeekValue(0.71, userPressing: false), Is.True, "gate stays open afterwards");
    }

    [Test]
    public void PausedClickEdgeBaselineIsCapturedAtPressNotRelease()
    {
        // While paused, mpv's echo of a click-to-seek can arrive during the brief click-hold. A release-time capture would record the DESTINATION as baseline and the gate could never open; press-time capture keeps the pre-seek value as baseline so the mid-hold echo opens the gate on the first post-release push.
        var g = new SeekScaleGate();
        g.OnVmSeekValue(0.2, userPressing: false);
        g.OnPress();                                    // baseline = 0.2
        g.OnVmSeekValue(0.7, userPressing: true);       // destination echo lands mid-hold, suppressed
        Assert.That(g.OnRelease(isSeeking: true), Is.False);
        Assert.That(g.OnVmSeekValue(0.7, userPressing: false), Is.True, "0.7 != press baseline 0.2 — gate opens");
    }

    [Test]
    public void SeekingFalseEdgeAlonePushesOnlyOncePositionMoved()
    {
        // The safety-net path for the paused case, where no further time-pos tick will arrive to drive OnVmSeekValue: `seeking=false` with the cached value still at the baseline must NOT push (that's the stale-flicker race); once the value has moved, the edge pushes.
        var g = new SeekScaleGate();
        g.OnVmSeekValue(0.2, userPressing: false);
        g.OnPress();
        g.OnRelease(isSeeking: true);
        Assert.That(g.OnSeekingChanged(isSeeking: false, userPressing: false), Is.False, "value still at baseline — the seeking edge arrived before time-pos");
        g.OnVmSeekValue(0.7, userPressing: true);       // value arrives while (somehow) pressing again — cached, not pushed
        Assert.That(g.OnSeekingChanged(isSeeking: false, userPressing: false), Is.True, "now the cached value is off-baseline");
        Assert.That(g.OnSeekingChanged(isSeeking: false, userPressing: false), Is.False, "one-shot: the gate is spent");
    }

    [Test]
    public void SeekingChangedIgnoredWhileStillSeekingOrPressing()
    {
        var g = new SeekScaleGate();
        g.OnVmSeekValue(0.2, userPressing: false);
        g.OnPress();
        g.OnRelease(isSeeking: true);
        g.OnVmSeekValue(0.7, userPressing: true);
        Assert.That(g.OnSeekingChanged(isSeeking: true, userPressing: false), Is.False);
        Assert.That(g.OnSeekingChanged(isSeeking: false, userPressing: true), Is.False);
    }

    [Test]
    public void UserSeekEmissionsDedupePerValueAndResetOnPress()
    {
        var g = new SeekScaleGate();
        Assert.That(g.ShouldEmitUserSeek(0.5), Is.True);
        Assert.That(g.ShouldEmitUserSeek(0.5), Is.False, "repeat emission at the same value is suppressed");
        Assert.That(g.ShouldEmitUserSeek(0.6), Is.True);
        g.OnPress();
        Assert.That(g.ShouldEmitUserSeek(0.6), Is.True, "press resets the dedupe — the first post-press change always seeks");
    }

    [Test]
    public void PressCancelsAnArmedSettleGate()
    {
        // A new press while settling re-baselines: the user grabbed the scrubber again before the previous seek landed.
        var g = new SeekScaleGate();
        g.OnVmSeekValue(0.2, userPressing: false);
        g.OnPress();
        g.OnRelease(isSeeking: true);
        g.OnVmSeekValue(0.4, userPressing: true);
        g.OnPress();                                    // new baseline = 0.4, settle cleared
        Assert.That(g.OnVmSeekValue(0.4, userPressing: false), Is.True, "no settle gate active after the re-press");
    }
}
