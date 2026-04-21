using System;

namespace Vomplayer.Wayland;

public enum VrrClassification
{
    Unknown = 0,
    Vrr = 1,
    Fixed = 2,
    // Frames on the nominal T grid but the stream is too stable (or metrics in a middle band) to tell fixed-refresh from VRR-locked-to-content-rate. 60 fps content on a 60 Hz panel is the canonical example: behaviorally indistinguishable.
    CantTell = 3,
}

// Behavioral classifier: is the compositor scanning our frames out on a fixed vsync grid, or at variable intervals?
//
// Reference period T_nominal comes from wl_output.mode (current mode's refresh in mHz). This is "system status" — the panel's configured mode, which KWin/wlroots/Mutter all report even while they VRR-engage within the mode. NOT the wp_presentation refresh field: KWin sets refresh=0 on VRR-capable outputs in many cases where VRR isn't actually engaged for this surface, so refresh=0 is an unreliable engagement signal.
//
// For each observed delta D_i, residual R_i = D_i - round(D_i/T) * T measures how far off the nearest integer-multiple of T the delta sits. On a fixed-refresh panel, scanout can only happen on T-boundaries — every delta MUST be K*T + small jitter. On VRR, deltas take arbitrary values in the panel's VRR range and typically miss the K*T grid.
//
// Thresholds: RmsOffGrid and RmsOnGrid are expressed as fractions of T. A warm-system compositor on idle produces per-frame jitter on the order of 100–300 µs — ~1–2% of a 60 Hz period — so ON_GRID at 2% sits right at the jitter ceiling, and OFF_GRID at 5% (~830 µs on 60 Hz) is comfortably past it. MinVariation is the σ/µ floor: below it, the stream is so steady we can't tell fixed-rate-at-panel-Hz from VRR-locked-to-content-rate apart — return CantTell rather than guess. These numbers were chosen by observing traces on KWin/AMDGPU; retune if future traces misclassify.
//
// Order of checks matters: a constant-rate stream at a non-grid rate (e.g. 50 fps on a VRR 60 Hz panel) has σ/µ tiny but RMS/T large — VRR check first catches it correctly.
public static class VrrClassifier
{
    private const double RmsOffGrid = 0.05;
    private const double RmsOnGrid = 0.02;
    private const double MinVariation = 0.05;

    // Ring must be at least this full before we'll classify. The caller (FrameTimingBridge) owns the ring; this value matches its capacity so "full" maps to "all samples valid."
    public const int MinSamples = 60;

    // deltasUs: inter-frame deltas in microseconds. modeMhz: panel's current mode refresh in millihertz (wl_output.mode). hzCenti is populated whenever modeMhz > 0, even when classification is Unknown/CantTell — callers display it as the nominal rate label.
    public static (VrrClassification classification, int hzCenti) Classify(ReadOnlySpan<uint> deltasUs, int modeMhz)
    {
        if (deltasUs.Length < MinSamples)
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

        double sum = 0;
        for (int i = 0; i < deltasUs.Length; i++)
        {
            sum += deltasUs[i];
        }
        double mean = sum / deltasUs.Length;
        if (mean <= 0)
        {
            return (VrrClassification.Unknown, hzCenti);
        }

        double sumsq = 0;
        for (int i = 0; i < deltasUs.Length; i++)
        {
            double d = deltasUs[i] - mean;
            sumsq += d * d;
        }
        double stddev = Math.Sqrt(sumsq / deltasUs.Length);
        double sigmaOverMu = stddev / mean;

        double rmsSq = 0;
        for (int i = 0; i < deltasUs.Length; i++)
        {
            double d = deltasUs[i];
            double ratio = d / tUs;
            long k = (long)(ratio + 0.5);
            if (k < 1)
            {
                k = 1;
            }
            double residual = d - k * tUs;
            rmsSq += residual * residual;
        }
        double rmsOverT = Math.Sqrt(rmsSq / deltasUs.Length) / tUs;

        if (rmsOverT > RmsOffGrid)
        {
            return (VrrClassification.Vrr, hzCenti);
        }
        if (sigmaOverMu < MinVariation)
        {
            return (VrrClassification.CantTell, hzCenti);
        }
        if (rmsOverT < RmsOnGrid)
        {
            return (VrrClassification.Fixed, hzCenti);
        }
        return (VrrClassification.CantTell, hzCenti);
    }
}
