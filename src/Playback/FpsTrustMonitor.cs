using System;
using System.Globalization;

namespace Vomplayer.Playback;

public enum FpsTrust { Trusted, Untrusted }

// State machine answering "is mpv's filter chain producing the rate we expect?". Pure data with an injected clock — Playback feeds it `Stopwatch.GetElapsedTime()` style seconds, tests pass synthetic values.
//
// Two distinct comparators:
//   - declaredFps: only used to pick the well-known-CFR tolerance band (a 25 fps source gets 10% slack; a 27.34 fps source gets 5%).
//   - expectedFps: the rate `estimated-vf-fps` is currently expected to converge to. Equals declared when no fps filter is active; equals declared × multiplier while our VRR filter is applied. The divergence check is `|estimated − expected|/expected`. Without this distinction, applying our own ×N filter would itself look like sustained divergence (mpv reads the post-filter rate), the monitor would flip Untrusted, ApplyVrrPolicy would unwind the filter, and we'd loop.
//
// The monitor's role shifts depending on filter state:
//   - Filter off → estimated-vf-fps is the source rate. Divergence catches mistagged CFR (declared 25, actually 30).
//   - Filter on → estimated-vf-fps is the post-filter rate (forced constant by the fps filter). Divergence catches filter-chain failures, but cannot detect source-side VFR (the filter masks input variability). Documented limitation: VFR detection is effective only for files where ApplyVrrPolicy did NOT apply a multiplier in the first place.
//
// Why not just check estimated-vs-expected once and decide: estimated-vf-fps takes a couple of seconds to stabilize after a load, seek, or filter-chain rebuild. Single-shot comparisons would mistrust most CFR files for the first second of playback. The state machine adds a warmup window (samples discarded), a sustained-disagreement requirement (a single spike never flips state), and one-way stickiness within a file load (Trusted → Untrusted only). Net effect: at most one filter-chain reinit per detected mismatch, and zero spurious reinits on the common case.
public sealed class FpsTrustMonitor
{
    public const double WarmupSeconds = 2.0;
    public const double SustainedDisagreementSeconds = 3.0;
    private const double WellKnownTolerance = 0.10;
    private const double OffGridTolerance = 0.05;
    private const double WellKnownProximity = 0.005;

    private static readonly double[] WellKnownCfrRates = { 23.976, 24.0, 25.0, 29.97, 30.0, 48.0, 50.0, 59.94, 60.0, 120.0 };

    private FpsTrust state = FpsTrust.Trusted;
    private string lastReason = "";
    // The rate the source FPS reading represents — used only to pick the well-known-CFR tolerance band. Distinct from expectedFps because the band is a property of the *declared source*, not what we're currently expecting estimated-vf-fps to read.
    private double declaredFps;
    // The rate we expect mpv's `estimated-vf-fps` to converge to. Equals declaredFps when no fps filter is active; equals declaredFps × multiplier when the VRR filter is applied. SetExpectedFps updates this when the policy changes the multiplier so the divergence check stays meaningful — without it, applying the filter would itself look like sustained divergence and the monitor would flip Untrusted and unwind the filter, then loop.
    private double expectedFps;
    private double tolerance = WellKnownTolerance;
    private double lastResetSeconds;
    private double? lastSampleSeconds;
    private double disagreementSecondsAccumulated;

    public FpsTrust State
    {
        get
        {
            return state;
        }
    }

    public string LastReason
    {
        get
        {
            return lastReason;
        }
    }

    public double DisagreementSecondsAccumulated
    {
        get
        {
            return disagreementSecondsAccumulated;
        }
    }

    // Returns the active tolerance fraction for the current file. Surfaced for diagnostics; the same value drives the divergence check in OnEstimatedFps.
    public double DivergenceToleranceFraction
    {
        get
        {
            return tolerance;
        }
    }

    // Reset for a fresh file. Trust starts Trusted (optimistic — most content is CFR; one cleanup flash on rare VFR beats pessimistic-and-reveal). The well-known-CFR check sets the tolerance band that will gate this file's divergence checks. Initial expected = declared (no filter active); the policy will call SetExpectedFps once it decides to apply a multiplier.
    public void OnFileLoaded(double declaredFps, double nowSeconds)
    {
        state = FpsTrust.Trusted;
        this.declaredFps = declaredFps;
        this.expectedFps = declaredFps;
        tolerance = IsWellKnownCfrRate(declaredFps) ? WellKnownTolerance : OffGridTolerance;
        lastResetSeconds = nowSeconds;
        lastSampleSeconds = null;
        disagreementSecondsAccumulated = 0;
        lastReason = "";
    }

    // Restart warmup and clear the disagreement accumulator. State is left alone (sticky if Untrusted): seeking inside a known-VFR file shouldn't undo the verdict.
    public void OnSeekEnded(double nowSeconds)
    {
        lastResetSeconds = nowSeconds;
        lastSampleSeconds = null;
        disagreementSecondsAccumulated = 0;
    }

    // The rate `estimated-vf-fps` should be converging to has changed — typically because the VRR policy applied or removed an fps multiplier. Restart the warmup window so mpv's filter chain has time to re-stabilize at the new rate before we resume judgment. State unchanged (sticky if Untrusted).
    public void SetExpectedFps(double expectedFps, double nowSeconds)
    {
        this.expectedFps = expectedFps;
        lastResetSeconds = nowSeconds;
        lastSampleSeconds = null;
        disagreementSecondsAccumulated = 0;
    }

    public void OnEstimatedFps(double estimatedFps, double nowSeconds)
    {
        if (state == FpsTrust.Untrusted)
        {
            return;
        }
        if (!double.IsFinite(estimatedFps) || estimatedFps <= 0)
        {
            return;
        }
        if (expectedFps <= 0)
        {
            return;
        }
        if (nowSeconds - lastResetSeconds < WarmupSeconds)
        {
            // Discard entirely — including as a dt baseline. Priming lastSampleSeconds here would make the first judged post-warmup sample accumulate an interval that partially predates warmup end, letting a single sparse reading flip Untrusted early.
            return;
        }
        // Compare against expectedFps (post-any-filter rate), not declaredFps. With our vf=fps filter active, estimated-vf-fps reads the post-filter output rate; comparing against declaredFps would mistake our own filter's rate-doubling for source-side VFR and unwind the filter that's working correctly.
        double dev = Math.Abs(estimatedFps - expectedFps) / expectedFps;
        if (dev > tolerance)
        {
            // Accumulate time since the previous sample (whatever the cadence) — sums to real wall time of sustained disagreement regardless of how often estimated-vf-fps fires.
            double dt = lastSampleSeconds.HasValue ? Math.Max(0.0, nowSeconds - lastSampleSeconds.Value) : 0.0;
            disagreementSecondsAccumulated += dt;
            if (disagreementSecondsAccumulated >= SustainedDisagreementSeconds)
            {
                state = FpsTrust.Untrusted;
                lastReason = string.Format(
                    CultureInfo.InvariantCulture,
                    "divergence sustained {0:F1}s (expected={1:F3}, est={2:F3})",
                    disagreementSecondsAccumulated,
                    expectedFps,
                    estimatedFps);
            }
        }
        else
        {
            // Re-agreement zeroes the accumulator: a string of in-spec samples should fully forgive a brief earlier divergence.
            disagreementSecondsAccumulated = 0;
        }
        lastSampleSeconds = nowSeconds;
    }

    private static bool IsWellKnownCfrRate(double declaredFps)
    {
        if (declaredFps <= 0)
        {
            return false;
        }
        foreach (var r in WellKnownCfrRates)
        {
            if (Math.Abs(declaredFps - r) / r <= WellKnownProximity)
            {
                return true;
            }
        }
        return false;
    }
}
