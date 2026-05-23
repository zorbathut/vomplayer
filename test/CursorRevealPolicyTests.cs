using Vomplayer.Util;

namespace Vomplayer.Tests;

[TestFixture]
public class CursorRevealPolicyTests
{
    // Mirrors the production constants: 3 px jitter dead zone while visible, 40 px reveal threshold while hidden.
    private const double DeadZone = 3.0;
    private const double RevealThreshold = 40.0;

    private static bool Significant(double dx, double dy, bool hidden)
    {
        return CursorRevealPolicy.IsSignificantMotion(dx, dy, hidden, DeadZone, RevealThreshold);
    }

    [Test]
    public void VisibleCursorTinyMotionIgnored()
    {
        Assert.That(Significant(2, 0, false), Is.False);
    }

    [Test]
    public void VisibleCursorPastDeadZoneSignificant()
    {
        Assert.That(Significant(5, 0, false), Is.True);
    }

    [Test]
    public void HiddenCursorMediumMotionIgnored()
    {
        // 20 px is real motion, but below the reveal threshold — an accidental bump shouldn't un-hide.
        Assert.That(Significant(20, 0, true), Is.False);
    }

    [Test]
    public void HiddenCursorLongMotionSignificant()
    {
        Assert.That(Significant(50, 0, true), Is.True);
    }

    // The load-bearing case: the same mid-range displacement reveals while visible (re-arms the timer) but not while hidden (stays put).
    [Test]
    public void MidRangeMotionCrossesRegime()
    {
        Assert.That(Significant(20, 0, false), Is.True, "should be significant while visible");
        Assert.That(Significant(20, 0, true), Is.False, "should be ignored while hidden");
    }

    [Test]
    public void HiddenThresholdBoundaryInclusive()
    {
        Assert.That(Significant(RevealThreshold, 0, true), Is.True);
    }

    [Test]
    public void VisibleDeadZoneBoundaryInclusive()
    {
        Assert.That(Significant(DeadZone, 0, false), Is.True);
    }

    // Distance is Euclidean, not Manhattan/max: dx=dy=30 is ~42.4 px (crosses 40), dx=dy=28 is ~39.6 px (does not).
    [Test]
    public void HiddenDiagonalUsesEuclideanDistance()
    {
        Assert.That(Significant(30, 30, true), Is.True);
        Assert.That(Significant(28, 28, true), Is.False);
    }
}
