using System;

namespace Vomplayer.Wayland;

public enum VrrClassification
{
    Unknown = 0,
    Vrr = 1,
    Fixed = 2,
    // Stream too close to a single grid rate to distinguish fixed-refresh from VRR-locked-to-content-rate. 60 fps content on a 60 Hz panel is the canonical case: behaviorally indistinguishable. Also covers VRR-locked streams whose content rate lands too near T_nominal (roughly 52–71 fps on a 60 Hz panel) to rule out jittery fixed refresh — honest admission rather than a guess.
    CantTell = 3,
}

// Behavioral classifier: is the compositor scanning our frames out on a fixed vsync grid, or at variable intervals?
//
// Reference period T_nominal comes from wl_output.mode (current mode's refresh in mHz). This is "system status" — the panel's configured mode, which KWin/wlroots/Mutter all report even while they VRR-engage within the mode. NOT the wp_presentation refresh field: KWin sets refresh=0 on VRR-capable outputs in many cases where VRR isn't actually engaged for this surface, so refresh=0 is an unreliable engagement signal.
//
// For each observed delta D_i, residual R_i = D_i - round(D_i/T) * T measures how far off the nearest integer-multiple of T the delta sits. On a fixed-refresh panel, scanout can only happen on T-boundaries — every delta MUST be K*T + small jitter. On VRR, deltas take arbitrary values in the panel's VRR range and typically miss the K*T grid.
//
// We classify on the MEDIAN of |R_i|/T rather than RMS because real-world fixed-refresh streams sometimes mix clean K*T samples with a minority of off-grid outliers (observed: 24 fps windowed playback on KWin where ~8% of frames land +4.25 ms off-grid from an otherwise-clean 3:2 pulldown). RMS is outlier-sensitive — a single sample at residual/T = 0.25 in a 60-sample ring pushes rms/T past 5%, false-positiving VRR even though the compositor is clearly scanning on-grid. Median requires the majority (>50%) of samples to be off-grid before it flips, which matches the actual semantic "the compositor is varying intervals."
//
// Thresholds:
//   MedianOffGrid (0.15) — chosen relative to realistic median residuals, not single-sample maxima. VRR content locked to clearly-non-grid rates (24/50 fps on a 60 Hz panel) lands well above; chaotic VRR sweeping through mixed K boundaries still typically reads median ~0.15+. Consequence: steady VRR rates within about ±15% of T_nominal (roughly 52–71 fps on 60 Hz) produce medians below this threshold and fall into CantTell — the honest answer, since they're behaviorally indistinguishable from slightly-jittery fixed refresh.
//   MedianOnGrid (0.02) — ~333 µs on a 60 Hz period; above typical timestamp quantization and small frame-timing jitter but below what multi-millisecond off-grid outliers would produce when they constitute the majority of samples.
//   MinVariation (0.05) — σ/µ floor: below it the stream is so steady we can't tell fixed-at-panel-Hz from VRR-locked-to-grid-rate apart. This is orthogonal to the median axis — catches the degenerate steady-grid-rate case even when median == 0.
//
// Order of checks matters: a constant-rate stream at a non-grid rate (e.g. 50 fps on a VRR 60 Hz panel) has σ/µ ≈ 0 but median(|R|/T) large — the VRR check must run first to catch it before the MinVariation gate would swallow it as CantTell.
public static class VrrClassifier
{
    private const double MedianOffGrid = 0.15;
    private const double MedianOnGrid = 0.02;
    private const double MinVariation = 0.05;

    // Classifier operates on exactly this many samples. Matches the caller's (FrameTimingBridge) ring capacity so "ring full" maps to "classify-ready." A fixed size also bounds the stackalloc below at compile time — no public-API stack-overflow footgun.
    public const int SampleCount = 60;

    // deltasUs: inter-frame deltas in microseconds; must be exactly SampleCount long or Unknown is returned. modeMhz: panel's current mode refresh in millihertz (wl_output.mode). hzCenti is populated whenever modeMhz > 0, even when classification is Unknown/CantTell — callers display it as the nominal rate label.
    public static (VrrClassification classification, int hzCenti) Classify(ReadOnlySpan<uint> deltasUs, int modeMhz)
    {
        if (deltasUs.Length != SampleCount)
        {
            return (VrrClassification.Unknown, 0);
        }
        if (modeMhz <= 0)
        {
            return (VrrClassification.Unknown, 0);
        }

        // wl_output.mode.refresh is in millihertz. T_us = 1e6 µs/s / (mHz/1000) Hz = 1e9 / mHz.
        double tUs = 1e9 / modeMhz;
        int hzCenti = (modeMhz + 5) / 10;

        Span<double> absResOverT = stackalloc double[SampleCount];
        double sum = 0;
        for (int i = 0; i < SampleCount; i++)
        {
            double d = deltasUs[i];
            sum += d;
            double ratio = d / tUs;
            long k = (long)(ratio + 0.5);
            if (k < 1)
            {
                k = 1;
            }
            double residual = d - k * tUs;
            absResOverT[i] = Math.Abs(residual) / tUs;
        }
        double mean = sum / SampleCount;
        if (mean <= 0)
        {
            return (VrrClassification.Unknown, hzCenti);
        }

        double sumsq = 0;
        for (int i = 0; i < SampleCount; i++)
        {
            double dev = deltasUs[i] - mean;
            sumsq += dev * dev;
        }
        double sigmaOverMu = Math.Sqrt(sumsq / SampleCount) / mean;

        absResOverT.Sort();
        // Even-count median of SampleCount elements: average of the two middle values.
        double median = 0.5 * (absResOverT[SampleCount / 2 - 1] + absResOverT[SampleCount / 2]);

        if (median > MedianOffGrid)
        {
            return (VrrClassification.Vrr, hzCenti);
        }
        if (sigmaOverMu < MinVariation)
        {
            return (VrrClassification.CantTell, hzCenti);
        }
        if (median < MedianOnGrid)
        {
            return (VrrClassification.Fixed, hzCenti);
        }
        return (VrrClassification.CantTell, hzCenti);
    }
}
