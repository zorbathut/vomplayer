using Vomplayer.Playback;

namespace Vomplayer.Tests;

[TestFixture]
public class FpsTrustMonitorTests
{
    [Test]
    public void DefaultStateAfterLoadIsTrusted()
    {
        var m = new FpsTrustMonitor();
        m.OnFileLoaded(25.0, 0);
        Assert.That(m.State, Is.EqualTo(FpsTrust.Trusted));
        Assert.That(m.LastReason, Is.EqualTo(""));
    }

    [Test]
    public void DivergentSamplesDuringWarmupAreDiscarded()
    {
        var m = new FpsTrustMonitor();
        m.OnFileLoaded(25.0, 0);
        // Wildly divergent estimated values within the 2s warmup window — must not accumulate.
        m.OnEstimatedFps(50.0, 0.1);
        m.OnEstimatedFps(50.0, 0.5);
        m.OnEstimatedFps(50.0, 1.0);
        m.OnEstimatedFps(50.0, 1.9);
        Assert.That(m.State, Is.EqualTo(FpsTrust.Trusted));
        Assert.That(m.DisagreementSecondsAccumulated, Is.EqualTo(0));
    }

    [Test]
    public void BriefDisagreementUnderThresholdDoesNotFlip()
    {
        var m = new FpsTrustMonitor();
        m.OnFileLoaded(25.0, 0);
        // Past warmup: 2 seconds of disagreement, below the 3s threshold.
        m.OnEstimatedFps(15.0, 2.5); // first post-warmup sample → no dt accumulated yet (no prior sample)
        m.OnEstimatedFps(15.0, 3.5); // 1.0 s
        m.OnEstimatedFps(15.0, 4.4); // +0.9 s = 1.9 s total
        Assert.That(m.State, Is.EqualTo(FpsTrust.Trusted));
    }

    [Test]
    public void SustainedDisagreementFlipsToUntrusted()
    {
        var m = new FpsTrustMonitor();
        m.OnFileLoaded(25.0, 0);
        m.OnEstimatedFps(15.0, 2.5);
        m.OnEstimatedFps(15.0, 3.5);
        m.OnEstimatedFps(15.0, 4.5);
        m.OnEstimatedFps(15.0, 5.6); // ~3.1 s accumulated
        Assert.That(m.State, Is.EqualTo(FpsTrust.Untrusted));
        Assert.That(m.LastReason, Does.Contain("divergence sustained"));
        Assert.That(m.LastReason, Does.Contain("expected=25"));
    }

    [Test]
    public void SetExpectedFpsAccountsForFilterMultiplication()
    {
        // Regression: applying our own vf=fps filter doubles estimated-vf-fps without indicating VFR. The monitor must compare estimated against the post-filter expected rate, not the source declared rate.
        var m = new FpsTrustMonitor();
        m.OnFileLoaded(25.0, 0);
        // Policy applies multiplier ×2 → expected post-filter = 50.
        m.SetExpectedFps(50.0, 0.5);
        // mpv's estimated-vf-fps converges to ~50 (post-filter rate) — sustained, but matches expected.
        m.OnEstimatedFps(50.0, 3.0);
        m.OnEstimatedFps(49.95, 5.0);
        m.OnEstimatedFps(50.05, 7.0);
        m.OnEstimatedFps(50.0, 9.0);
        Assert.That(m.State, Is.EqualTo(FpsTrust.Trusted));
    }

    [Test]
    public void SetExpectedFpsRestartsWarmup()
    {
        // Mid-playback expected change (e.g., user dragged window to a panel where the multiplier differs) should restart the warmup window so mpv's filter-chain rebuild has time to stabilize at the new rate without the transient triggering a false-mistrust.
        var m = new FpsTrustMonitor();
        m.OnFileLoaded(25.0, 0);
        // Apply ×2 at t=0.5; warmup fresh.
        m.SetExpectedFps(50.0, 0.5);
        // Move to ×4 panel at t=10; new expected = 100.
        m.SetExpectedFps(100.0, 10.0);
        // Within new warmup, divergent samples must be discarded.
        m.OnEstimatedFps(50.0, 11.0);
        m.OnEstimatedFps(50.0, 12.0);
        Assert.That(m.State, Is.EqualTo(FpsTrust.Trusted));
    }

    [Test]
    public void DivergenceDetectableEvenWithExpectedSet()
    {
        // After SetExpectedFps, sustained divergence from the new expected rate (e.g., filter is failing to maintain output rate) still flips to Untrusted.
        var m = new FpsTrustMonitor();
        m.OnFileLoaded(25.0, 0);
        m.SetExpectedFps(50.0, 0.5);
        // Past warmup, estimated stuck way off the new expected.
        m.OnEstimatedFps(20.0, 3.0);
        m.OnEstimatedFps(20.0, 6.5);
        Assert.That(m.State, Is.EqualTo(FpsTrust.Untrusted));
    }

    [Test]
    public void StickyAcrossInSpecSamplesAfterFlip()
    {
        var m = new FpsTrustMonitor();
        m.OnFileLoaded(25.0, 0);
        m.OnEstimatedFps(15.0, 2.5);
        m.OnEstimatedFps(15.0, 5.6); // accumulator hits 3.1 s on this sample → Untrusted
        Assert.That(m.State, Is.EqualTo(FpsTrust.Untrusted));
        // Now feed in-spec samples — must stay Untrusted.
        m.OnEstimatedFps(25.0, 7.0);
        m.OnEstimatedFps(25.0, 9.0);
        m.OnEstimatedFps(24.99, 12.0);
        Assert.That(m.State, Is.EqualTo(FpsTrust.Untrusted));
    }

    [Test]
    public void SeekResetsAccumulatorButDoesNotUnstickUntrusted()
    {
        var m = new FpsTrustMonitor();
        m.OnFileLoaded(25.0, 0);
        m.OnEstimatedFps(15.0, 2.5);
        m.OnEstimatedFps(15.0, 5.6);
        Assert.That(m.State, Is.EqualTo(FpsTrust.Untrusted));
        m.OnSeekEnded(10.0);
        Assert.That(m.State, Is.EqualTo(FpsTrust.Untrusted));
        Assert.That(m.DisagreementSecondsAccumulated, Is.EqualTo(0));
    }

    [Test]
    public void SeekDuringPreFlipDisagreementResetsAccumulator()
    {
        var m = new FpsTrustMonitor();
        m.OnFileLoaded(25.0, 0);
        m.OnEstimatedFps(15.0, 2.5);
        m.OnEstimatedFps(15.0, 4.0); // 1.5 s accumulated
        Assert.That(m.DisagreementSecondsAccumulated, Is.GreaterThan(0));
        m.OnSeekEnded(5.0);
        Assert.That(m.DisagreementSecondsAccumulated, Is.EqualTo(0));
        Assert.That(m.State, Is.EqualTo(FpsTrust.Trusted));
    }

    [Test]
    public void ReAgreementDuringPreFlipZeroesAccumulator()
    {
        var m = new FpsTrustMonitor();
        m.OnFileLoaded(25.0, 0);
        m.OnEstimatedFps(15.0, 2.5);
        m.OnEstimatedFps(15.0, 4.0); // ~1.5 s accumulated
        m.OnEstimatedFps(25.0, 4.5); // back in spec → reset to 0
        Assert.That(m.DisagreementSecondsAccumulated, Is.EqualTo(0));
        Assert.That(m.State, Is.EqualTo(FpsTrust.Trusted));
    }

    [Test]
    public void WellKnownCfrUsesTenPercentTolerance()
    {
        var m = new FpsTrustMonitor();
        m.OnFileLoaded(25.0, 0);
        Assert.That(m.DivergenceToleranceFraction, Is.EqualTo(0.10));
        // 25 ± 10% = 22.5..27.5. 23.0 is in spec for 25 — must not accumulate.
        m.OnEstimatedFps(23.0, 2.5);
        m.OnEstimatedFps(23.0, 5.6);
        Assert.That(m.State, Is.EqualTo(FpsTrust.Trusted));
    }

    [Test]
    public void OffGridDeclaredUsesFivePercentTolerance()
    {
        var m = new FpsTrustMonitor();
        m.OnFileLoaded(27.34, 0);
        Assert.That(m.DivergenceToleranceFraction, Is.EqualTo(0.05));
        // 27.34 ± 5% = 25.97..28.71. 23.0 is out of spec for 27.34.
        m.OnEstimatedFps(23.0, 2.5);
        m.OnEstimatedFps(23.0, 5.6);
        Assert.That(m.State, Is.EqualTo(FpsTrust.Untrusted));
    }

    [Test]
    public void NtscRateRecognizedAsWellKnown()
    {
        var m = new FpsTrustMonitor();
        m.OnFileLoaded(23.976, 0);
        Assert.That(m.DivergenceToleranceFraction, Is.EqualTo(0.10));
    }

    [Test]
    public void NonFiniteOrNegativeEstimatedIgnored()
    {
        var m = new FpsTrustMonitor();
        m.OnFileLoaded(25.0, 0);
        m.OnEstimatedFps(double.NaN, 2.5);
        m.OnEstimatedFps(double.PositiveInfinity, 3.5);
        m.OnEstimatedFps(0.0, 4.5);
        m.OnEstimatedFps(-1.0, 5.5);
        Assert.That(m.State, Is.EqualTo(FpsTrust.Trusted));
    }

    [Test]
    public void RepeatedLoadsResetState()
    {
        var m = new FpsTrustMonitor();
        m.OnFileLoaded(25.0, 0);
        m.OnEstimatedFps(15.0, 2.5);
        m.OnEstimatedFps(15.0, 5.6);
        Assert.That(m.State, Is.EqualTo(FpsTrust.Untrusted));
        m.OnFileLoaded(50.0, 100.0);
        Assert.That(m.State, Is.EqualTo(FpsTrust.Trusted));
    }
}
