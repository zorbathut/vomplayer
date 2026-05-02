using Vomplayer.Util;

namespace Vomplayer.Tests;

[TestFixture]
public class PipLayoutCalcTests
{
    private const int Margin = 16;
    private const int MinW = 160;
    private const int MinH = 90;
    private const double Aspect169 = 16.0 / 9.0;

    private static PipLayoutCalc.VideoRect FullRect(int w, int h)
    {
        return new PipLayoutCalc.VideoRect(0, 0, w, h);
    }

    [Test]
    public void DefaultLayoutIsBottomLeftQuarterWidth()
    {
        var r = PipLayoutCalc.Compute(FullRect(1280, 720), Aspect169, Margin, userWidthFraction: null, userMarginStartFraction: null, userMarginTopFraction: null, MinW, MinH);
        Assert.That(r.Width, Is.EqualTo(320));
        Assert.That(r.Height, Is.EqualTo(180));
        Assert.That(r.MarginStart, Is.EqualTo(Margin));
        Assert.That(r.MarginTop, Is.EqualTo(720 - 180 - Margin));
    }

    [Test]
    public void DefaultsRespectMinWidthFloorOnNarrowParent()
    {
        var r = PipLayoutCalc.Compute(FullRect(400, 300), Aspect169, Margin, null, null, null, MinW, MinH);
        Assert.That(r.Width, Is.EqualTo(MinW));
    }

    [Test]
    public void PreAllocationFallsBackToMinTimesTwo()
    {
        var r = PipLayoutCalc.Compute(FullRect(0, 0), Aspect169, Margin, null, null, null, MinW, MinH);
        Assert.That(r.Width, Is.EqualTo(MinW * 2));
        Assert.That(r.Height, Is.EqualTo((int)System.Math.Round(MinW * 2 / Aspect169)));
        Assert.That(r.MarginStart, Is.EqualTo(Margin));
        Assert.That(r.MarginTop, Is.EqualTo(0));
    }

    [Test]
    public void UserWidthWinsAndDerivesHeight()
    {
        // 480/1280 = 0.375 → user-set width fraction.
        var r = PipLayoutCalc.Compute(FullRect(1280, 720), Aspect169, Margin, userWidthFraction: 0.375, userMarginStartFraction: null, userMarginTopFraction: null, MinW, MinH);
        Assert.That(r.Width, Is.EqualTo(480));
        Assert.That(r.Height, Is.EqualTo(270));
    }

    [Test]
    public void UserWidthClampedToMin()
    {
        // 30/1280 ≈ 0.0234 → resolves below minWidth, should clamp up.
        var r = PipLayoutCalc.Compute(FullRect(1280, 720), Aspect169, Margin, userWidthFraction: 30.0 / 1280.0, userMarginStartFraction: null, userMarginTopFraction: null, MinW, MinH);
        Assert.That(r.Width, Is.EqualTo(MinW));
    }

    [Test]
    public void UserWidthCannotExceedParent()
    {
        // 1.5 → 150% of parent, should clamp down to parent width.
        var r = PipLayoutCalc.Compute(FullRect(800, 600), Aspect169, Margin, userWidthFraction: 1.5, userMarginStartFraction: null, userMarginTopFraction: null, MinW, MinH);
        Assert.That(r.Width, Is.EqualTo(800));
    }

    [Test]
    public void UserMarginsRespectedWhenWithinBounds()
    {
        var r = PipLayoutCalc.Compute(FullRect(1280, 720), Aspect169, Margin, userWidthFraction: 0.25, userMarginStartFraction: 100.0 / 1280.0, userMarginTopFraction: 50.0 / 720.0, MinW, MinH);
        Assert.That(r.MarginStart, Is.EqualTo(100));
        Assert.That(r.MarginTop, Is.EqualTo(50));
    }

    [Test]
    public void UserMarginsClampedToKeepPipFullyVisible()
    {
        // Way-out-of-range fractions (>1) should clamp so the PiP stays fully on-screen.
        var r = PipLayoutCalc.Compute(FullRect(1280, 720), Aspect169, Margin, userWidthFraction: 0.25, userMarginStartFraction: 4.0, userMarginTopFraction: 7.0, MinW, MinH);
        Assert.That(r.MarginStart, Is.EqualTo(1280 - 320));
        Assert.That(r.MarginTop, Is.EqualTo(720 - 180));
    }

    [Test]
    public void NegativeMarginsClampedToZero()
    {
        var r = PipLayoutCalc.Compute(FullRect(1280, 720), Aspect169, Margin, userWidthFraction: 0.25, userMarginStartFraction: -0.05, userMarginTopFraction: -0.07, MinW, MinH);
        Assert.That(r.MarginStart, Is.EqualTo(0));
        Assert.That(r.MarginTop, Is.EqualTo(0));
    }

    [Test]
    public void TallSourceFloorsAtMinHeight()
    {
        // 1:2 portrait source on a width fraction that would yield height < MinH.
        var r = PipLayoutCalc.Compute(FullRect(1280, 720), aspect: 0.5, Margin, userWidthFraction: 50.0 / 1280.0, userMarginStartFraction: null, userMarginTopFraction: null, MinW, MinH);
        Assert.That(r.Height, Is.GreaterThanOrEqualTo(MinH));
    }

    [Test]
    public void NonsenseAspectFallsBackTo169()
    {
        var r = PipLayoutCalc.Compute(FullRect(1280, 720), aspect: -1, Margin, userWidthFraction: 0.25, null, null, MinW, MinH);
        Assert.That(r.Height, Is.EqualTo(180));
    }

    [Test]
    public void ProjectAspectResizeAlongDiagonalIsOneToOne()
    {
        double dy = 100.0 / Aspect169;
        Assert.That(PipLayoutCalc.ProjectAspectResize(100, dy, Aspect169), Is.EqualTo(100).Within(1e-9));
    }

    [Test]
    public void ProjectAspectResizePureXIsAttenuated()
    {
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
        // Edge case: 1:2 portrait source on a 200x200 rect. Default width = max(160, 200/4=50) = 160. Height-from-aspect = 320 > rect 200, so height clamps to 200 and width back-derives to 100 — below minWidth=160. The current Compute does NOT re-floor in this case; the width comes out at 100 and the resize grip is somewhat smaller than the configured minimum. Pinned here as the documented behavior; if a future change adds a final width-floor, this test must be updated alongside the policy.
        var r = PipLayoutCalc.Compute(FullRect(200, 200), aspect: 0.5, Margin, userWidthFraction: null, userMarginStartFraction: null, userMarginTopFraction: null, MinW, MinH);
        Assert.That(r.Width, Is.EqualTo(100));
        Assert.That(r.Height, Is.EqualTo(200));
    }

    [Test]
    public void StaleUserMarginGetsClampedAfterParentShrinks()
    {
        var r = PipLayoutCalc.Compute(FullRect(800, 600), Aspect169, Margin, userWidthFraction: 0.4, userMarginStartFraction: 900.0 / 1280.0, userMarginTopFraction: null, MinW, MinH);
        Assert.That(r.MarginStart, Is.EqualTo(800 - 320));
    }

    [Test]
    public void UserFractionsScaleWithParentSize()
    {
        // Same fractions on a 1280×720 parent and a 1920×1080 parent — both rects are full-widget — should produce pixel results at exactly 1.5× the smaller layout (the proportional-rescale property).
        var small = PipLayoutCalc.Compute(FullRect(1280, 720), Aspect169, Margin, userWidthFraction: 0.3, userMarginStartFraction: 0.1, userMarginTopFraction: 0.2, MinW, MinH);
        var large = PipLayoutCalc.Compute(FullRect(1920, 1080), Aspect169, Margin, userWidthFraction: 0.3, userMarginStartFraction: 0.1, userMarginTopFraction: 0.2, MinW, MinH);
        Assert.That(small.Width, Is.EqualTo(384));
        Assert.That(large.Width, Is.EqualTo(576));
        Assert.That(small.MarginStart, Is.EqualTo(128));
        Assert.That(large.MarginStart, Is.EqualTo(192));
        Assert.That(small.MarginTop, Is.EqualTo(144));
        Assert.That(large.MarginTop, Is.EqualTo(216));
    }

    [Test]
    public void PreAllocationDropsFractionOverridesAndUsesDefaults()
    {
        var r = PipLayoutCalc.Compute(FullRect(0, 0), Aspect169, Margin, userWidthFraction: 0.5, userMarginStartFraction: 0.5, userMarginTopFraction: 0.5, MinW, MinH);
        Assert.That(r.Width, Is.EqualTo(MinW * 2));
        Assert.That(r.MarginStart, Is.EqualTo(Margin));
        Assert.That(r.MarginTop, Is.EqualTo(0));
    }

    // ---- ComputeVideoRect ----

    [Test]
    public void VideoRectMatchesWidgetWhenAspectsMatch()
    {
        var r = PipLayoutCalc.ComputeVideoRect(1920, 1080, Aspect169);
        Assert.That(r.X, Is.EqualTo(0));
        Assert.That(r.Y, Is.EqualTo(0));
        Assert.That(r.Width, Is.EqualTo(1920));
        Assert.That(r.Height, Is.EqualTo(1080));
    }

    [Test]
    public void VideoRectPillarboxesWhenWidgetWiderThanVideo()
    {
        // 4:3 video in a 16:9 widget → video height fills, width=height*4/3, centered horizontally.
        var r = PipLayoutCalc.ComputeVideoRect(1920, 1080, primaryAspect: 4.0 / 3.0);
        Assert.That(r.Y, Is.EqualTo(0));
        Assert.That(r.Height, Is.EqualTo(1080));
        // Expected video width = 1080 * 4/3 = 1440. Pillarbox = (1920-1440)/2 = 240.
        Assert.That(r.Width, Is.EqualTo(1440));
        Assert.That(r.X, Is.EqualTo(240));
    }

    [Test]
    public void VideoRectLetterboxesWhenWidgetTallerThanVideo()
    {
        // 16:9 video in a 4:3 widget → video width fills, height=width*9/16, centered vertically.
        var r = PipLayoutCalc.ComputeVideoRect(1200, 900, primaryAspect: Aspect169);
        Assert.That(r.X, Is.EqualTo(0));
        Assert.That(r.Width, Is.EqualTo(1200));
        // Expected video height = 1200 * 9/16 = 675. Letterbox = (900-675)/2 = 112.
        Assert.That(r.Height, Is.EqualTo(675));
        Assert.That(r.Y, Is.EqualTo(112));
    }

    [Test]
    public void VideoRectFullWidgetWhenPrimaryAspectUnknown()
    {
        // No primary file loaded: rect spans the entire widget so the PiP defaults to widget-relative placement until a Primary aspect lands.
        var r = PipLayoutCalc.ComputeVideoRect(1280, 720, primaryAspect: null);
        Assert.That(r.X, Is.EqualTo(0));
        Assert.That(r.Y, Is.EqualTo(0));
        Assert.That(r.Width, Is.EqualTo(1280));
        Assert.That(r.Height, Is.EqualTo(720));
    }

    [Test]
    public void VideoRectFullWidgetWhenPrimaryAspectInvalid()
    {
        var r1 = PipLayoutCalc.ComputeVideoRect(1280, 720, primaryAspect: 0);
        Assert.That(r1, Is.EqualTo(new PipLayoutCalc.VideoRect(0, 0, 1280, 720)));
        var r2 = PipLayoutCalc.ComputeVideoRect(1280, 720, primaryAspect: -1);
        Assert.That(r2, Is.EqualTo(new PipLayoutCalc.VideoRect(0, 0, 1280, 720)));
        var r3 = PipLayoutCalc.ComputeVideoRect(1280, 720, primaryAspect: double.NaN);
        Assert.That(r3, Is.EqualTo(new PipLayoutCalc.VideoRect(0, 0, 1280, 720)));
        var r4 = PipLayoutCalc.ComputeVideoRect(1280, 720, primaryAspect: double.PositiveInfinity);
        Assert.That(r4, Is.EqualTo(new PipLayoutCalc.VideoRect(0, 0, 1280, 720)));
    }

    [Test]
    public void VideoRectZeroSizeWhenWidgetUnallocated()
    {
        var r = PipLayoutCalc.ComputeVideoRect(0, 0, primaryAspect: Aspect169);
        Assert.That(r.Width, Is.EqualTo(0));
        Assert.That(r.Height, Is.EqualTo(0));
    }

    // ---- Compute against pillarboxed/letterboxed rects ----

    [Test]
    public void DefaultPlacementIsRelativeToVideoRectNotWidget()
    {
        // 4:3 video in 1920x1080 widget → video rect is 1440x1080 starting at x=240. Default PiP placement (16px from bottom-left of the *video rect*) should land at widget x=240+16=256, not x=16 (which would be in the pillarbox).
        var rect = PipLayoutCalc.ComputeVideoRect(1920, 1080, 4.0 / 3.0);
        var r = PipLayoutCalc.Compute(rect, Aspect169, Margin, null, null, null, MinW, MinH);
        Assert.That(r.MarginStart, Is.EqualTo(240 + Margin));
        // Default width = max(160, 1440/4=360) = 360; height = 360/(16/9) = 202.5 → 202 (Math.Round defaults to ToEven). MarginTop = (videoH - height - margin) + videoY = (1080 - 202 - 16) + 0 = 862.
        Assert.That(r.Width, Is.EqualTo(360));
        Assert.That(r.MarginTop, Is.EqualTo(1080 - 202 - Margin));
    }

    [Test]
    public void UserMarginsAreRectRelativeNotWidgetRelative()
    {
        // Pillarboxed setup: rect = (240, 0, 1440, 1080). User dropped PiP at fraction (0.5, 0.5) of the video rect — should be at widget x=240+720=960 (NOT x=720, which would be widget-relative).
        var rect = PipLayoutCalc.ComputeVideoRect(1920, 1080, 4.0 / 3.0);
        var r = PipLayoutCalc.Compute(rect, Aspect169, Margin, userWidthFraction: 0.25, userMarginStartFraction: 0.5, userMarginTopFraction: 0.5, MinW, MinH);
        // Width = 0.25 * 1440 = 360. MarginStart in-rect = 0.5 * 1440 = 720. Output marginStart = 240 + 720 = 960.
        Assert.That(r.Width, Is.EqualTo(360));
        Assert.That(r.MarginStart, Is.EqualTo(240 + 720));
        // MarginTop in-rect = 0.5 * 1080 = 540, with rect.Y=0 → output 540.
        Assert.That(r.MarginTop, Is.EqualTo(540));
    }

    [Test]
    public void UserMarginsClampedToVideoRectNotWidget()
    {
        // Letterboxed setup: rect = (0, 112, 1200, 675). PiP width 0.3 → 360px (height 202). Fraction 0.95 horizontally would put marginStart at 1140 in-rect — but max is 1200-360=840. Clamp to 840 in-rect → output 840. Bottom edge: 0.95 vertically → 641 in-rect, max 675-202=473 → clamp to 473, output 112+473=585. Critically, the PiP doesn't spill into the letterbox (y >= 675 + something), it stays inside the video rect.
        var rect = PipLayoutCalc.ComputeVideoRect(1200, 900, Aspect169);
        var r = PipLayoutCalc.Compute(rect, Aspect169, Margin, userWidthFraction: 0.3, userMarginStartFraction: 0.95, userMarginTopFraction: 0.95, MinW, MinH);
        Assert.That(r.Width, Is.EqualTo(360));
        Assert.That(r.Height, Is.EqualTo(202));
        Assert.That(r.MarginStart, Is.EqualTo(1200 - 360));
        Assert.That(r.MarginTop, Is.EqualTo(112 + (675 - 202)));
    }

    [Test]
    public void NegativeMarginsClampToVideoRectOriginNotWidgetOrigin()
    {
        // Pillarboxed setup: negative fraction → in-rect 0, output = rect.X (i.e. left edge of the video, NOT left edge of the widget at x=0).
        var rect = PipLayoutCalc.ComputeVideoRect(1920, 1080, 4.0 / 3.0);
        var r = PipLayoutCalc.Compute(rect, Aspect169, Margin, userWidthFraction: 0.25, userMarginStartFraction: -0.1, userMarginTopFraction: -0.1, MinW, MinH);
        Assert.That(r.MarginStart, Is.EqualTo(240));
        Assert.That(r.MarginTop, Is.EqualTo(0));
    }

    [Test]
    public void PiPStaysGluedToVideoAcrossViewportAspectChanges()
    {
        // Core property: same user fractions, same primary video (4:3), different widget aspect → the PiP's *in-rect* position is identical (and the output margins are the rect.X/Y-shifted versions of that). This is the property the previous widget-relative implementation violated.
        var rectWide = PipLayoutCalc.ComputeVideoRect(1920, 1080, 4.0 / 3.0); // pillarbox
        var rectSquare = PipLayoutCalc.ComputeVideoRect(1080, 1080, 4.0 / 3.0); // letterbox

        var wide = PipLayoutCalc.Compute(rectWide, Aspect169, Margin, userWidthFraction: 0.25, userMarginStartFraction: 0.5, userMarginTopFraction: 0.5, MinW, MinH);
        var square = PipLayoutCalc.Compute(rectSquare, Aspect169, Margin, userWidthFraction: 0.25, userMarginStartFraction: 0.5, userMarginTopFraction: 0.5, MinW, MinH);

        // Strip the rect offset to compare in-rect placement.
        int wideInRectStart = wide.MarginStart - rectWide.X;
        int squareInRectStart = square.MarginStart - rectSquare.X;
        int wideInRectTop = wide.MarginTop - rectWide.Y;
        int squareInRectTop = square.MarginTop - rectSquare.Y;

        // In-rect fractional positions (0.5, 0.5) should map to half-rect offsets in each.
        Assert.That(wideInRectStart, Is.EqualTo(rectWide.Width / 2));
        Assert.That(squareInRectStart, Is.EqualTo(rectSquare.Width / 2));
        Assert.That(wideInRectTop, Is.EqualTo(rectWide.Height / 2));
        Assert.That(squareInRectTop, Is.EqualTo(rectSquare.Height / 2));
    }
}
