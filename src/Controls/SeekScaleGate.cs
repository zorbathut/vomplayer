using System;

namespace Vomplayer.Controls;

// The seek scrubber's press/release/settle state machine, extracted pure from SeekScaleController (which keeps the GTK plumbing: the Scale, the raw legacy-controller signal, and the re-entrancy guard around SetValue). Each method returns whether the caller should push the current VM value into the widget; the caller supplies the live inputs (is the user pressing, is mpv seeking) since both are queried from GTK/mpv state rather than stored here.
//
// The machine exists to solve one flicker: after a seek, mpv fires `seeking=false` and the new `time-pos` from the same playback-loop step, but the events can arrive in either order. Pushing the VM value at `seeking=false` would push the PRE-seek stale position and flicker old→new on the next tick. So the settle gate holds pushes until the reported position has actually moved off the press-time baseline — the reliable "the seek landed" signal. Capturing the baseline at press (not release) matters: mpv's echo of a click-to-seek can arrive during the brief click-hold, so a release-time capture would record the destination and the gate could never open while paused.
public sealed class SeekScaleGate
{
    private bool awaitingSeekSettle;
    private double seekValueAtPress;
    private double currentVmSeekValue;
    // Dedupe for user-driven emissions, reset to NaN on each press. NaN comparison is always false so the first post-press value-change always seeks; subsequent emissions at the same value (from any source) are skipped. Cheap defense against spurious re-emissions.
    private double lastUserSeek = double.NaN;

    // The last VM-side value seen; the caller pushes this into the widget whenever a method returns true.
    public double CurrentVmSeekValue
    {
        get
        {
            return currentVmSeekValue;
        }
    }

    // Press (ButtonPress/TouchBegin): capture the pre-seek position as the settle baseline now, before any seek echo lands, and reset the user-seek dedupe.
    public void OnPress()
    {
        awaitingSeekSettle = false;
        lastUserSeek = double.NaN;
        seekValueAtPress = currentVmSeekValue;
    }

    // Release (ButtonRelease/TouchEnd/TouchCancel/GrabBroken): if a seek is still in flight, arm the settle gate and suppress; otherwise push immediately.
    public bool OnRelease(bool isSeeking)
    {
        if (isSeeking)
        {
            awaitingSeekSettle = true;
            return false;
        }
        return true;
    }

    // A VM-side SeekValue update arrived. Cache it, then gate: suppressed while the user is pressing, and while settling until the value moves off the press-time baseline.
    public bool OnVmSeekValue(double value, bool userPressing)
    {
        currentVmSeekValue = value;
        if (userPressing)
        {
            return false;
        }
        if (awaitingSeekSettle)
        {
            if (value == seekValueAtPress)
            {
                return false;
            }
            awaitingSeekSettle = false;
        }
        return true;
    }

    // Safety net for the `seeking` property edge: push once the seek has ended AND the cached value has moved off the baseline — which covers the paused case, where no further time-pos tick will arrive to drive OnVmSeekValue.
    public bool OnSeekingChanged(bool isSeeking, bool userPressing)
    {
        if (awaitingSeekSettle && !isSeeking && !userPressing && currentVmSeekValue != seekValueAtPress)
        {
            awaitingSeekSettle = false;
            return true;
        }
        return false;
    }

    // A user-driven widget value change: emit at most once per distinct value (see lastUserSeek).
    public bool ShouldEmitUserSeek(double value)
    {
        if (value == lastUserSeek)
        {
            return false;
        }
        lastUserSeek = value;
        return true;
    }
}
