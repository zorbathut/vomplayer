using Vomplayer.Util;

namespace Vomplayer.Tests;

[TestFixture]
public class PipLayoutCalcTests
{
    private const int Margin = 16;
    private const int MinW = 160;
    private const int MinH = 90;
    private const double Aspect169 = 16.0 / 9.0;

    [Test]
    public void DefaultLayoutIsBottomLeftQuarterWidth()
    {
        var r = PipLayoutCalc.Compute(1280, 720, Aspect169, Margin, userWidth: null, userMarginStart: null, userMarginTop: null, MinW, MinH);
        Assert.That(r.Width, Is.EqualTo(320));
        Assert.That(r.Height, Is.EqualTo(180));
        Assert.That(r.MarginStart, Is.EqualTo(Margin));
        Assert.That(r.MarginTop, Is.EqualTo(720 - 180 - Margin));
    }

    [Test]
    public void DefaultsRespectMinWidthFloorOnNarrowParent()
    {
        var r = PipLayoutCalc.Compute(400, 300, Aspect169, Margin, null, null, null, MinW, MinH);
        Assert.That(r.Width, Is.EqualTo(MinW));
    }

    [Test]
    public void PreAllocationFallsBackToMinTimesTwo()
    {
        var r = PipLayoutCalc.Compute(0, 0, Aspect169, Margin, null, null, null, MinW, MinH);
        Assert.That(r.Width, Is.EqualTo(MinW * 2));
        Assert.That(r.Height, Is.EqualTo((int)System.Math.Round(MinW * 2 / Aspect169)));
        Assert.That(r.MarginStart, Is.EqualTo(Margin));
        Assert.That(r.MarginTop, Is.EqualTo(0));
    }

    [Test]
    public void UserWidthWinsAndDerivesHeight()
    {
        var r = PipLayoutCalc.Compute(1280, 720, Aspect169, Margin, userWidth: 480, userMarginStart: null, userMarginTop: null, MinW, MinH);
        Assert.That(r.Width, Is.EqualTo(480));
        Assert.That(r.Height, Is.EqualTo(270));
    }

    [Test]
    public void UserWidthClampedToMin()
    {
        var r = PipLayoutCalc.Compute(1280, 720, Aspect169, Margin, userWidth: 30, userMarginStart: null, userMarginTop: null, MinW, MinH);
        Assert.That(r.Width, Is.EqualTo(MinW));
    }

    [Test]
    public void UserWidthCannotExceedParent()
    {
        var r = PipLayoutCalc.Compute(800, 600, Aspect169, Margin, userWidth: 1200, userMarginStart: null, userMarginTop: null, MinW, MinH);
        Assert.That(r.Width, Is.EqualTo(800));
    }

    [Test]
    public void UserMarginsRespectedWhenWithinBounds()
    {
        var r = PipLayoutCalc.Compute(1280, 720, Aspect169, Margin, userWidth: 320, userMarginStart: 100, userMarginTop: 50, MinW, MinH);
        Assert.That(r.MarginStart, Is.EqualTo(100));
        Assert.That(r.MarginTop, Is.EqualTo(50));
    }

    [Test]
    public void UserMarginsClampedToKeepPipFullyVisible()
    {
        var r = PipLayoutCalc.Compute(1280, 720, Aspect169, Margin, userWidth: 320, userMarginStart: 5000, userMarginTop: 5000, MinW, MinH);
        Assert.That(r.MarginStart, Is.EqualTo(1280 - 320));
        Assert.That(r.MarginTop, Is.EqualTo(720 - 180));
    }

    [Test]
    public void NegativeMarginsClampedToZero()
    {
        var r = PipLayoutCalc.Compute(1280, 720, Aspect169, Margin, userWidth: 320, userMarginStart: -50, userMarginTop: -50, MinW, MinH);
        Assert.That(r.MarginStart, Is.EqualTo(0));
        Assert.That(r.MarginTop, Is.EqualTo(0));
    }

    [Test]
    public void TallSourceFloorsAtMinHeight()
    {
        // 1:2 portrait source on a width that would yield height < MinH.
        var r = PipLayoutCalc.Compute(1280, 720, aspect: 0.5, Margin, userWidth: 50, userMarginStart: null, userMarginTop: null, MinW, MinH);
        Assert.That(r.Height, Is.GreaterThanOrEqualTo(MinH));
    }

    [Test]
    public void NonsenseAspectFallsBackTo169()
    {
        var r = PipLayoutCalc.Compute(1280, 720, aspect: -1, Margin, userWidth: 320, null, null, MinW, MinH);
        Assert.That(r.Height, Is.EqualTo(180));
    }

    [Test]
    public void ProjectAspectResizeAlongDiagonalIsOneToOne()
    {
        // Drag exactly along (1, 1/aspect) by 100px-of-width worth: dx=100, dy=100/aspect → projection = 100.
        double dy = 100.0 / Aspect169;
        Assert.That(PipLayoutCalc.ProjectAspectResize(100, dy, Aspect169), Is.EqualTo(100).Within(1e-9));
    }

    [Test]
    public void ProjectAspectResizePureXIsAttenuated()
    {
        // Pure-X drag of 100px, aspect=16/9: projection = 100 / (1 + (9/16)^2) ≈ 75.93.
        double t = PipLayoutCalc.ProjectAspectResize(100, 0, Aspect169);
        Assert.That(t, Is.LessThan(100));
        Assert.That(t, Is.GreaterThan(0));
    }

    [Test]
    public void ProjectAspectResizeNegativeShrinks()
    {
        double t = PipLayoutCalc.ProjectAspectResize(-50, -50.0 / Aspect169, Aspect169);
        Assert.That(t, Is.EqualTo(-50).Within(1e-9));
    }

    [Test]
    public void ProjectAspectResizePureYIsAttenuated()
    {
        // Pure-Y drag of 100px, aspect=16/9: dy is converted to width via aspect, then projected onto the aspect-locked line. Expected: t = (100/aspect) / (1 + (1/aspect)^2) for aspect=16/9 → ~43.3. This is intentionally smaller than 100 because the projection finds the closest point on the aspect line to the pure-Y offset; users dragging strictly down see proportionally less width growth than dragging diagonally along the aspect axis. Documented here so any future change to the formula must explicitly update this expectation.
        double t = PipLayoutCalc.ProjectAspectResize(0, 100, Aspect169);
        Assert.That(t, Is.GreaterThan(40));
        Assert.That(t, Is.LessThan(50));
    }

    [Test]
    public void ExtremePortraitOnTinyParentMayDropBelowMinWidth()
    {
        // Edge case: 1:2 portrait source on a 200x200 parent. Default width = max(160, 200/4=50) = 160. Height-from-aspect = 320 > parent 200, so height clamps to 200 and width back-derives to 100 — below minWidth=160. The current Compute does NOT re-floor in this case; the width comes out at 100 and the resize grip is somewhat smaller than the configured minimum. Pinned here as the documented behavior; if a future change adds a final width-floor, this test must be updated alongside the policy. The case is rare in practice (extreme aspect AND tiny parent).
        var r = PipLayoutCalc.Compute(200, 200, aspect: 0.5, Margin, userWidth: null, userMarginStart: null, userMarginTop: null, MinW, MinH);
        Assert.That(r.Width, Is.EqualTo(100));
        Assert.That(r.Height, Is.EqualTo(200));
    }

    [Test]
    public void StaleUserMarginGetsClampedAfterParentShrinks()
    {
        // User dragged PiP to right side at marginStart=900 when parent was 1280 wide. Window now 800 wide; pipUserWidth=320 still fits, but stored marginStart=900 + 320 = 1220 > 800 → clamp to 800-320=480. Verifies the "after a window-shrink-induced clamp, the returned marginStart is the clamped value" contract that MainWindow.Pip.cs writes back into pipUserMarginStart.
        var r = PipLayoutCalc.Compute(800, 600, Aspect169, Margin, userWidth: 320, userMarginStart: 900, userMarginTop: null, MinW, MinH);
        Assert.That(r.MarginStart, Is.EqualTo(800 - 320));
    }
}
