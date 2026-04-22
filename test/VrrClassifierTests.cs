using System;
using Vomplayer.Wayland;

namespace Vomplayer.Tests;

[TestFixture]
public class VrrClassifierTests
{
    // 60 Hz exactly: 1e9 µs/s / 60 Hz ≈ 16667 µs period. wl_output.mode gives this as 60000 mHz.
    private const int Mode60HzMhz = 60000;
    private const int Mode144HzMhz = 144000;
    private const int RingFull = 60;

    [Test]
    public void RingNotFullReturnsUnknown()
    {
        var deltas = new uint[RingFull - 1];
        Array.Fill(deltas, 16667u);
        var (cls, hzCenti) = VrrClassifier.Classify(deltas, Mode60HzMhz);
        Assert.That(cls, Is.EqualTo(VrrClassification.Unknown));
        Assert.That(hzCenti, Is.EqualTo(0));
    }

    [Test]
    public void ModeUnknownReturnsUnknown()
    {
        var deltas = new uint[RingFull];
        Array.Fill(deltas, 16667u);
        var (cls, _) = VrrClassifier.Classify(deltas, 0);
        Assert.That(cls, Is.EqualTo(VrrClassification.Unknown));
    }

    [Test]
    public void FrameDropsOnSixtyHzPanelClassifyAsFixed()
    {
        // Fixed-refresh signature: K*T boundaries but with occasional frame drops (K=1 mostly, K=2 sometimes). Residuals stay small (on-grid); σ/µ gets lifted out of the "too stable" band by the K transitions. A truly steady 60 fps stream on a 60 Hz panel can't be distinguished from VRR-locked-to-content-rate, so it correctly returns CantTell (covered by UltraStableGridRateClassifiesAsCantTell). This test covers the case that unambiguously proves the grid.
        var rng = new Random(12345);
        var deltas = new uint[RingFull];
        for (int i = 0; i < RingFull; i++)
        {
            // Every ~6th sample is a dropped-frame K=2. Small ±80 µs jitter on both K to keep rms/T in the on-grid band.
            int k = (i % 6 == 0) ? 2 : 1;
            int jitter = rng.Next(-80, 81);
            deltas[i] = (uint)(k * 16667 + jitter);
        }
        var (cls, hzCenti) = VrrClassifier.Classify(deltas, Mode60HzMhz);
        Assert.That(cls, Is.EqualTo(VrrClassification.Fixed));
        Assert.That(hzCenti, Is.EqualTo(6000));
    }

    [Test]
    public void ChaoticDeltasClassifyAsVrr()
    {
        // Random intervals across a VRR range that miss the 60 Hz grid.
        var rng = new Random(54321);
        var deltas = new uint[RingFull];
        for (int i = 0; i < RingFull; i++)
        {
            deltas[i] = (uint)rng.Next(10000, 22000);
        }
        var (cls, hzCenti) = VrrClassifier.Classify(deltas, Mode60HzMhz);
        Assert.That(cls, Is.EqualTo(VrrClassification.Vrr));
        Assert.That(hzCenti, Is.EqualTo(6000));
    }

    [Test]
    public void SteadyNonGridRateClassifiesAsVrr()
    {
        // 50 fps exact (20000 µs) streaming to a 60 Hz panel. σ/µ near 0 but RMS/T large — VRR check runs first.
        var deltas = new uint[RingFull];
        Array.Fill(deltas, 20000u);
        var (cls, hzCenti) = VrrClassifier.Classify(deltas, Mode60HzMhz);
        Assert.That(cls, Is.EqualTo(VrrClassification.Vrr));
        Assert.That(hzCenti, Is.EqualTo(6000));
    }

    [Test]
    public void UltraStableGridRateClassifiesAsCantTell()
    {
        // Zero-jitter 16667 µs stream on a 60 Hz panel. σ/µ = 0 < MIN_VARIATION; RMS/T tiny — can't tell fixed-at-60 from VRR-locked-to-60.
        var deltas = new uint[RingFull];
        Array.Fill(deltas, 16667u);
        var (cls, hzCenti) = VrrClassifier.Classify(deltas, Mode60HzMhz);
        Assert.That(cls, Is.EqualTo(VrrClassification.CantTell));
        Assert.That(hzCenti, Is.EqualTo(6000));
    }

    [Test]
    public void HzCentiReflectsModeRate()
    {
        // 144 Hz exactly → 14400 centihertz.
        var deltas = new uint[RingFull];
        Array.Fill(deltas, (uint)(1_000_000_000L / Mode144HzMhz));
        var (_, hzCenti) = VrrClassifier.Classify(deltas, Mode144HzMhz);
        Assert.That(hzCenti, Is.EqualTo(14400));
    }

    [Test]
    public void MiddleBandClassifiesAsCantTell()
    {
        // Residuals in the ambiguous band between on-grid and off-grid thresholds: push every sample ~3% off-grid with enough spread to pass σ/µ floor.
        var deltas = new uint[RingFull];
        double tUs = 1e9 / Mode60HzMhz;
        var rng = new Random(9);
        for (int i = 0; i < RingFull; i++)
        {
            double off = (i % 2 == 0) ? 0.03 : -0.03;
            double jitter = rng.NextDouble() * 0.02 - 0.01;
            deltas[i] = (uint)(tUs * (1 + off + jitter));
        }
        var (cls, _) = VrrClassifier.Classify(deltas, Mode60HzMhz);
        Assert.That(cls, Is.EqualTo(VrrClassification.CantTell));
    }

    [Test]
    public void TwentyFourFpsPulldownWithOutliersClassifiesAsFixed()
    {
        // Real-world 24fps-on-60Hz-windowed trace: mostly-clean 3:2 pulldown (33.40/50.10) with ~8% of samples landing +4.25ms off-grid (37.58 / 54.27). The compositor is not VRR-engaging, but RMS-based classification false-positived VRR because a single 25%-off-grid sample in a 60-sample ring is enough to exceed a 5%-of-T RMS threshold. Median-based classification sees the majority of samples are on-grid and correctly calls it FIXED. Regression test for the trace captured in-situ; boundary coverage lives in the MajorityOn/OffGrid tests below.
        double tUs = 1e9 / Mode60HzMhz;
        var deltas = new uint[RingFull];
        for (int i = 0; i < RingFull; i++)
        {
            deltas[i] = (uint)(tUs * ((i & 1) == 0 ? 2 : 3));
        }
        deltas[7] = (uint)(tUs * 2 + 4250);
        deltas[19] = (uint)(tUs * 3 + 4250);
        deltas[31] = (uint)(tUs * 2 + 4250);
        deltas[43] = (uint)(tUs * 3 + 4250);
        deltas[55] = (uint)(tUs * 2 + 4250);
        var (cls, _) = VrrClassifier.Classify(deltas, Mode60HzMhz);
        Assert.That(cls, Is.EqualTo(VrrClassification.Fixed));
    }

    [Test]
    public void MajorityOnGridClassifiesAsFixed()
    {
        // 29 of 60 samples off-grid (48.3%) — just under half. Sorted |res|/T: 31 zeros, then 29 at ~0.255. Even-count median averages indices 29 & 30, both zero. The on-grid branch must fire. Pins the median-decision boundary at "majority on-grid."
        double tUs = 1e9 / Mode60HzMhz;
        var deltas = new uint[RingFull];
        for (int i = 0; i < RingFull; i++)
        {
            int k = ((i & 1) == 0) ? 2 : 3;
            bool off = i < 29;
            deltas[i] = (uint)(tUs * k + (off ? 4250 : 0));
        }
        var (cls, _) = VrrClassifier.Classify(deltas, Mode60HzMhz);
        Assert.That(cls, Is.EqualTo(VrrClassification.Fixed));
    }

    [Test]
    public void MajorityOffGridClassifiesAsVrr()
    {
        // 31 of 60 samples off-grid (51.7%) — just over half. Sorted |res|/T: 29 zeros, then 31 at ~0.255. Even-count median averages indices 29 & 30, both 0.255 — above MedianOffGrid. Paired with MajorityOnGridClassifiesAsFixed to pin the threshold.
        double tUs = 1e9 / Mode60HzMhz;
        var deltas = new uint[RingFull];
        for (int i = 0; i < RingFull; i++)
        {
            int k = ((i & 1) == 0) ? 2 : 3;
            bool off = i < 31;
            deltas[i] = (uint)(tUs * k + (off ? 4250 : 0));
        }
        var (cls, _) = VrrClassifier.Classify(deltas, Mode60HzMhz);
        Assert.That(cls, Is.EqualTo(VrrClassification.Vrr));
    }

    [Test]
    public void BoundaryMedianWithVariationClassifiesAsCantTell()
    {
        // Exactly 30 of 60 samples off-grid (50%): median ~= 0.1275 — in the ambiguous band (> MedianOnGrid, < MedianOffGrid). The σ/µ from K=2/K=3 pulldown alternation is well above MinVariation, so the stream doesn't fall through the too-steady gate. Exercises the final fallback CantTell branch that nothing else covers.
        double tUs = 1e9 / Mode60HzMhz;
        var deltas = new uint[RingFull];
        for (int i = 0; i < RingFull; i++)
        {
            int k = ((i & 1) == 0) ? 2 : 3;
            bool off = i < 30;
            deltas[i] = (uint)(tUs * k + (off ? 4250 : 0));
        }
        var (cls, _) = VrrClassifier.Classify(deltas, Mode60HzMhz);
        Assert.That(cls, Is.EqualTo(VrrClassification.CantTell));
    }

    [Test]
    public void SteadyRateWithinJitterBandClassifiesAsCantTell()
    {
        // 59 fps VRR-locked stream on a 60 Hz panel with realistic ±10 µs jitter. σ/µ is tiny (≈ 6e-4), well below MinVariation, so the too-steady-to-disambiguate gate fires — behaviorally indistinguishable from fixed-60Hz drifting through quantization. Documents the "honest CantTell for steady near-grid rates" guarantee the user asked for; tests the MinVariation gate, like UltraStableGridRate, but at a non-integer multiple of T where the rate is plausibly real VRR content.
        var deltas = new uint[RingFull];
        var rng = new Random(99);
        const uint nominalUs = 16949; // 1e6 / 59
        for (int i = 0; i < RingFull; i++)
        {
            deltas[i] = (uint)((int)nominalUs + rng.Next(-10, 11));
        }
        var (cls, _) = VrrClassifier.Classify(deltas, Mode60HzMhz);
        Assert.That(cls, Is.EqualTo(VrrClassification.CantTell));
    }
}
