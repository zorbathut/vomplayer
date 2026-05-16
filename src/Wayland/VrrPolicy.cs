using System;

namespace Vomplayer.Wayland;

// Pure decision function: given source frame rate, the current output's VRR window, the current scanout refresh, and a trust flag, return the smallest integer multiplier N such that source × N lands inside the usable VRR range. Multiplier == 1 means "no filter" and Reason carries the no-op cause for diagnostics.
//
// Why ceil (smallest fitting N) rather than floor (largest fitting N): for 25 fps on a 40-144 Hz panel we want N=2 (50 Hz output), not N=5 (125 Hz). Once the output rate is inside the VRR window, judder is gone — higher N just multiplies compositor + GPU work for no perceptual gain.
//
// Cap rationale: compositors won't VRR-scan above the configured mode's pixel clock (KWin's drm backend clamps; same on wlroots/Mutter). EDID's Display Range Limits describe what the panel could do across all modes, not what's reachable in the current one. So the effective ceiling is min(maxHz, currentRefreshHz) — a hard limit, not a preference. The cap is dropped only when currentRefreshHz is unknown (mode event not yet landed, or zero/negative).
//
// Two-step algorithm: prefer a strict fit inside the actual monitor range; fall back to a slack-relaxed FLOOR only if strict can't find one. The slack absorbs manufacturer rounding at the bottom edge (23.976 × 2 = 47.952 vs a 48 Hz nominal floor — panels typically VRR that 0.05 Hz lower). The slack is NEVER applied at the top edge: the cap is a hard pixel-clock limit, and the original maxHz can't be exceeded just because of rounding (no symmetric NTSC story above max).
//
// Order of checks matters: the strict short-circuit (source >= minHz) runs BEFORE Step 1 so a source already in the panel's true range doesn't get pointlessly multiplied. Step 1 strict runs BEFORE the slack-band native check so a clean N≥2 fit is preferred over native-with-slack-tolerance (39.6 fps on [40, 144] gets N=2 → 79.2 Hz, NOT a native-pass at 39.6 just because 39.6 ≥ 40 − slack).
public static class VrrPolicy
{
    // Fractional slack at the floor only. 1% of MaxHz so 48 Hz panels get ~0.5 Hz and 480 Hz panels get ~5 Hz — proportional rather than a fixed 1 Hz that's tight on small ranges and absurdly loose on large ones. The original maxHz drives this even when the cap is engaged, because the slack is about panel-manufacturer rounding tolerance (a property of the panel, not the current mode).
    private const double SlackFraction = 0.01;

    // Sanity bounds for the source rate. <5 fps is a near-stillframe slideshow where multiplication doesn't help anyone; >240 fps is almost certainly a lying container (no real source content runs that fast yet, and panels can't VRR there either).
    private const double MinPlausibleSourceFps = 5.0;
    private const double MaxPlausibleSourceFps = 240.0;

    public static VrrDecision Decide(double? sourceFps, VrrRange? window, double? currentRefreshHz, bool isFpsTrusted)
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

        double minHz = window.Value.MinHz;
        double maxHz = window.Value.MaxHz;
        double slack = maxHz * SlackFraction;
        // Effective ceiling: clamp to the current scanout mode when known. A null/zero/negative currentRefreshHz means "unknown" — fall back to the EDID max, which is the pre-cap behavior.
        double effMax = currentRefreshHz.HasValue && currentRefreshHz.Value > 0 ? Math.Min(maxHz, currentRefreshHz.Value) : maxHz;

        // Strict short-circuit on the true floor: source is at or above the panel's VRR floor, no multiplier helps. Distinguish above-cap (mode can't scan source rate) from in-range (compositor will scan natively) so the diagnostic doesn't mislead — "in VRR range" should mean what it says.
        if (sourceFps.Value >= minHz)
        {
            if (sourceFps.Value > effMax)
            {
                return new VrrDecision(1, sourceFps.Value, "source above current refresh rate");
            }
            return new VrrDecision(1, sourceFps.Value, "source already in VRR range");
        }

        // Step 1 — strict: smallest N≥2 with N*source in [minHz, effMax]. nStrict is guaranteed ≥ 2 because source < minHz, so ceil(minHz/source) ≥ 2.
        int nStrict = (int)Math.Ceiling(minHz / sourceFps.Value);
        double outputStrict = sourceFps.Value * nStrict;
        if (outputStrict <= effMax)
        {
            return new VrrDecision(nStrict, outputStrict, "ok");
        }

        // Step 2 — relax the floor by slack. nSlack < 2 means source is itself in the slack band [minHz - slack, minHz) — Step 1 already proved no N≥2 fits under the cap, so the only honest answer is multiplier=1 with the panel scanning source natively within manufacturer tolerance.
        double slackedFloor = minHz - slack;
        int nSlack = (int)Math.Ceiling(slackedFloor / sourceFps.Value);
        if (nSlack < 2)
        {
            return new VrrDecision(1, sourceFps.Value, "source within slack of VRR floor");
        }
        double outputSlack = sourceFps.Value * nSlack;
        if (outputSlack <= effMax)
        {
            return new VrrDecision(nSlack, outputSlack, "ok (slack)");
        }

        return new VrrDecision(1, sourceFps.Value, "no integer multiple fits in VRR window");
    }
}
