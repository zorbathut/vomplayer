using System;

namespace Vomplayer.Wayland;

// Pure decision function: given source frame rate, the current output's VRR window, and a trust flag, return the smallest integer multiplier N such that source × N lands inside the VRR window. Multiplier == 1 means "no filter" and Reason carries the no-op cause for diagnostics.
//
// Why ceil (smallest fitting N) rather than floor (largest fitting N): for 25 fps on a 40-144 Hz panel we want N=2 (50 Hz output), not N=5 (125 Hz). Once the output rate is inside the VRR window, judder is gone — higher N just multiplies compositor + GPU work for no perceptual gain.
public static class VrrPolicy
{
    // Fractional slack on both edges of the VRR window. 1% of MaxHz so 48 Hz panels get ~0.5 Hz of slack and 480 Hz panels get ~5 Hz — proportional rather than a fixed 1 Hz that's tight on small ranges and absurdly loose on large ones. The floor slack lets 23.976 × 2 = 47.952 land on a 48 Hz nominal panel; the ceiling slack absorbs the same NTSC rounding above the max.
    private const double SlackFraction = 0.01;

    // Sanity bounds for the source rate. <5 fps is a near-stillframe slideshow where multiplication doesn't help anyone; >240 fps is almost certainly a lying container (no real source content runs that fast yet, and panels can't VRR there either).
    private const double MinPlausibleSourceFps = 5.0;
    private const double MaxPlausibleSourceFps = 240.0;

    public static VrrDecision Decide(double? sourceFps, VrrRange? window, bool isFpsTrusted)
    {
        // Echo back a sane sourceFps for diagnostics in every branch — even when we're rejecting on trust or out-of-range. Default to 0 only when sourceFps itself is null/non-finite/<=0 (no useful number to display).
        double echoFps = sourceFps.HasValue && double.IsFinite(sourceFps.Value) && sourceFps.Value > 0 ? sourceFps.Value : 0.0;
        if (!isFpsTrusted)
        {
            return new VrrDecision(1, echoFps, "source FPS untrusted (likely VFR)");
        }
        if (sourceFps == null || !double.IsFinite(sourceFps.Value) || sourceFps.Value < MinPlausibleSourceFps || sourceFps.Value > MaxPlausibleSourceFps)
        {
            return new VrrDecision(1, echoFps, "source FPS unavailable or out of range");
        }
        if (window == null)
        {
            return new VrrDecision(1, sourceFps.Value, "VRR window unknown");
        }
        double slack = window.Value.MaxHz * SlackFraction;
        // ceil((minHz - slack) / sourceFps) — smallest integer N with N * sourceFps ≥ minHz - slack.
        int n = (int)Math.Ceiling((window.Value.MinHz - slack) / sourceFps.Value);
        if (n < 2)
        {
            return new VrrDecision(1, sourceFps.Value, "source already in/above VRR range");
        }
        double output = sourceFps.Value * n;
        if (output > window.Value.MaxHz + slack)
        {
            return new VrrDecision(1, sourceFps.Value, "no integer multiple fits in VRR window");
        }
        return new VrrDecision(n, output, "ok");
    }
}
