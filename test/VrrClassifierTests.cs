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
    public void MiddleBandRmsClassifiesAsCantTell()
    {
        // RMS/T in the ambiguous band between ON_GRID (0.02) and OFF_GRID (0.05): push every sample ~3% off-grid with enough spread to pass σ/µ floor.
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
}
