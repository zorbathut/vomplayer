using Vomplayer.Wayland;

namespace Vomplayer.Tests;

[TestFixture]
public class VrrPolicyTests
{
    private static readonly VrrRange Window4860 = new(48, 60);
    private static readonly VrrRange Window4060 = new(40, 60);
    private static readonly VrrRange Window40144 = new(40, 144);
    private static readonly VrrRange Window100144 = new(100, 144);
    private static readonly VrrRange Window48240 = new(48, 240);
    private static readonly VrrRange Window4850 = new(48, 50);

    [Test]
    public void Untrusted_ClearsRegardlessOfOtherInputs()
    {
        var d = VrrPolicy.Decide(25.0, Window4860, currentRefreshHz: null, isFpsTrusted: false);
        Assert.That(d.Multiplier, Is.EqualTo(1));
        Assert.That(d.Reason, Does.Contain("untrusted"));
    }

    [Test]
    public void NullSourceFps_NoFilter()
    {
        var d = VrrPolicy.Decide(null, Window4860, currentRefreshHz: null, isFpsTrusted: true);
        Assert.That(d.Multiplier, Is.EqualTo(1));
        Assert.That(d.Reason, Does.Contain("unavailable or out of range"));
    }

    [Test]
    public void OutOfRangeSourceFps_NoFilter()
    {
        Assert.That(VrrPolicy.Decide(0.0, Window4860, null, true).Multiplier, Is.EqualTo(1));
        Assert.That(VrrPolicy.Decide(4.0, Window4860, null, true).Multiplier, Is.EqualTo(1));
        Assert.That(VrrPolicy.Decide(241.0, Window4860, null, true).Multiplier, Is.EqualTo(1));
        Assert.That(VrrPolicy.Decide(double.NaN, Window4860, null, true).Multiplier, Is.EqualTo(1));
        Assert.That(VrrPolicy.Decide(double.PositiveInfinity, Window4860, null, true).Multiplier, Is.EqualTo(1));
    }

    [Test]
    public void NullWindow_NoFilter()
    {
        var d = VrrPolicy.Decide(25.0, null, null, true);
        Assert.That(d.Multiplier, Is.EqualTo(1));
        Assert.That(d.Reason, Does.Contain("VRR window unknown"));
    }

    [Test]
    public void TwentyFiveFps_FortyEightToSixty_PicksDouble()
    {
        var d = VrrPolicy.Decide(25.0, Window4860, currentRefreshHz: 60.0, isFpsTrusted: true);
        Assert.That(d.Multiplier, Is.EqualTo(2));
        Assert.That(d.OutputFps, Is.EqualTo(50.0).Within(1e-9));
        Assert.That(d.Reason, Is.EqualTo("ok"));
    }

    [Test]
    public void TwentyFourFps_TightPanel_StrictFailsThenSlackFloorFits()
    {
        // 23.976 on a 48-60 panel @ 60Hz. Strict ceil = ceil(48/23.976) = 3 → 71.928 — but that's above the 60 ceiling. Strict fails; slack-floor fallback finds N=2 at 47.952 (within slack of 48).
        var d = VrrPolicy.Decide(23.976, Window4860, currentRefreshHz: 60.0, isFpsTrusted: true);
        Assert.That(d.Multiplier, Is.EqualTo(2));
        Assert.That(d.OutputFps, Is.EqualTo(47.952).Within(1e-9));
        Assert.That(d.Reason, Is.EqualTo("ok (slack)"));
    }

    [Test]
    public void TwentyFourFps_HighRefresh_StrictPicksTriple()
    {
        // 23.976 on a [48, 240] panel @ 144Hz mode: strict ceil=3 → 71.928, fits in [48, 144]. No slack needed.
        var d = VrrPolicy.Decide(23.976, Window48240, currentRefreshHz: 144.0, isFpsTrusted: true);
        Assert.That(d.Multiplier, Is.EqualTo(3));
        Assert.That(d.OutputFps, Is.EqualTo(71.928).Within(1e-9));
        Assert.That(d.Reason, Is.EqualTo("ok"));
    }

    [Test]
    public void TwentyFourFpsExact_FortyEightToSixty_PicksDoubleStrict()
    {
        // 24.0 exact (not NTSC) on [48, 60] cap=60: ceil(48/24) = 2 → 48 exactly, strict fit. No slack involved.
        var d = VrrPolicy.Decide(24.0, Window4860, currentRefreshHz: 60.0, isFpsTrusted: true);
        Assert.That(d.Multiplier, Is.EqualTo(2));
        Assert.That(d.OutputFps, Is.EqualTo(48.0).Within(1e-9));
        Assert.That(d.Reason, Is.EqualTo("ok"));
    }

    [Test]
    public void TwentyNineNineSeven_FortyToSixty_PicksDoubleStrict()
    {
        // NTSC 29.97 on [40, 60] cap=60: ceil(40/29.97) = 2 → 59.94 ≤ 60, strict fit.
        var d = VrrPolicy.Decide(29.97, Window4060, currentRefreshHz: 60.0, isFpsTrusted: true);
        Assert.That(d.Multiplier, Is.EqualTo(2));
        Assert.That(d.OutputFps, Is.EqualTo(59.94).Within(1e-9));
        Assert.That(d.Reason, Is.EqualTo("ok"));
    }

    [Test]
    public void TwentyFiveFps_FortyToOneFortyFour_PicksDoubleNotQuintuple()
    {
        // ceil(40 / 25) = 2. ×2 lands at 50 Hz, not ×5 → 125 Hz.
        var d = VrrPolicy.Decide(25.0, Window40144, currentRefreshHz: 144.0, isFpsTrusted: true);
        Assert.That(d.Multiplier, Is.EqualTo(2));
        Assert.That(d.OutputFps, Is.EqualTo(50.0).Within(1e-9));
    }

    [Test]
    public void FiftyFps_FortyEightToSixty_AlreadyInRange()
    {
        var d = VrrPolicy.Decide(50.0, Window4860, currentRefreshHz: 60.0, isFpsTrusted: true);
        Assert.That(d.Multiplier, Is.EqualTo(1));
        Assert.That(d.Reason, Is.EqualTo("source already in VRR range"));
    }

    [Test]
    public void SixtyFps_FortyEightToSixty_AtCeilingStillInRange()
    {
        // 60 fps source = effMax. Inclusive: 60 > 60 is false, so it's "in range" not "above cap".
        var d = VrrPolicy.Decide(60.0, Window4860, currentRefreshHz: 60.0, isFpsTrusted: true);
        Assert.That(d.Multiplier, Is.EqualTo(1));
        Assert.That(d.Reason, Is.EqualTo("source already in VRR range"));
    }

    [Test]
    public void SourceAboveCap_DistinctFromInRange()
    {
        // 70 fps on a [40, 144] panel — within advertised range, but if the user is in a 60 Hz mode the compositor can't scan there. Distinct reason from "in range".
        var d = VrrPolicy.Decide(70.0, Window40144, currentRefreshHz: 60.0, isFpsTrusted: true);
        Assert.That(d.Multiplier, Is.EqualTo(1));
        Assert.That(d.Reason, Is.EqualTo("source above current refresh rate"));
    }

    [Test]
    public void TwentyFiveFps_OneHundredToOneFortyFour_PicksQuadruple()
    {
        // ceil(100 / 25) = 4. ×4 = 100 Hz.
        var d = VrrPolicy.Decide(25.0, Window100144, currentRefreshHz: 144.0, isFpsTrusted: true);
        Assert.That(d.Multiplier, Is.EqualTo(4));
        Assert.That(d.OutputFps, Is.EqualTo(100.0).Within(1e-9));
    }

    [Test]
    public void ThirtyFps_OneHundredToOneFortyFour_PicksQuadrupleAtCeiling()
    {
        // ceil(100 / 30) = 4. ×4 = 120 Hz, well within 144.
        var d = VrrPolicy.Decide(30.0, Window100144, currentRefreshHz: 144.0, isFpsTrusted: true);
        Assert.That(d.Multiplier, Is.EqualTo(4));
        Assert.That(d.OutputFps, Is.EqualTo(120.0).Within(1e-9));
    }

    [Test]
    public void ThirtyFps_NoIntegerFitsRejects()
    {
        // 33-46 Hz window, 30 fps source: ×1 below floor, ×2 (60) above ceiling. Reject.
        var window = new VrrRange(33, 46);
        var d = VrrPolicy.Decide(30.0, window, currentRefreshHz: 46.0, isFpsTrusted: true);
        Assert.That(d.Multiplier, Is.EqualTo(1));
        Assert.That(d.Reason, Does.Contain("no integer multiple"));
    }

    [Test]
    public void TwentyFiveFps_FortyToSixty_PicksDouble_FloorSlackNotNeeded()
    {
        // 25 × 2 = 50, comfortably above 40 floor.
        var d = VrrPolicy.Decide(25.0, Window4060, currentRefreshHz: 60.0, isFpsTrusted: true);
        Assert.That(d.Multiplier, Is.EqualTo(2));
        Assert.That(d.OutputFps, Is.EqualTo(50.0).Within(1e-9));
        Assert.That(d.Reason, Is.EqualTo("ok"));
    }

    [Test]
    public void SourceJustBelowFloor_PrefersStrictN2_NotSlackBandNative()
    {
        // 39.6 fps on a [40, 144] panel: in the slack band ([38.56, 40)). The old algorithm short-circuited to N=1 at 39.6 — but N=2 → 79.2 Hz is a clean strict fit, and that's what we want. Pins the fix for the slack-band short-circuit regression.
        var d = VrrPolicy.Decide(39.6, Window40144, currentRefreshHz: 144.0, isFpsTrusted: true);
        Assert.That(d.Multiplier, Is.EqualTo(2));
        Assert.That(d.OutputFps, Is.EqualTo(79.2).Within(1e-9));
        Assert.That(d.Reason, Is.EqualTo("ok"));
    }

    [Test]
    public void SourceInSlackBand_CapBlocksMultiplier_NativeFallback()
    {
        // 47.5 fps on a [48, 50] panel cap=50. In the slack band of the 48 floor (slack=0.5 → slackedFloor=47.5, inclusive). Strict step 1: N=2 → 95 > 50, fails. Step 2 finds nSlack=1, so we land on native multiplier=1 — distinct reason from "already in range" because we DID try and reject a multiplier.
        var d = VrrPolicy.Decide(47.5, Window4850, currentRefreshHz: 50.0, isFpsTrusted: true);
        Assert.That(d.Multiplier, Is.EqualTo(1));
        Assert.That(d.Reason, Is.EqualTo("source within slack of VRR floor"));
    }

    // --- Cap-specific tests ---

    [Test]
    public void Cap_RestrictsCeilingBelowAdvertisedMax()
    {
        // 50 fps source, panel [40, 144] but current mode = 60 Hz. With cap, ×1 = 50 already in [40, 60] → "already in VRR range".
        var d = VrrPolicy.Decide(50.0, Window40144, currentRefreshHz: 60.0, isFpsTrusted: true);
        Assert.That(d.Multiplier, Is.EqualTo(1));
        Assert.That(d.Reason, Is.EqualTo("source already in VRR range"));
    }

    [Test]
    public void Cap_ForcesNoFilter_WhereUncappedWouldDouble()
    {
        // 25 fps on a [40, 144] panel — uncapped would multiply ×2 → 50. But if the user is running a 45 Hz mode (currentRefreshHz=45), ×2 = 50 exceeds the cap. No integer multiple fits.
        var d = VrrPolicy.Decide(25.0, Window40144, currentRefreshHz: 45.0, isFpsTrusted: true);
        Assert.That(d.Multiplier, Is.EqualTo(1));
        Assert.That(d.Reason, Does.Contain("no integer multiple"));
    }

    [Test]
    public void Cap_HigherThanAdvertisedMax_IsAClampNoop()
    {
        // 25 fps on a [40, 60] panel; if the compositor somehow reports currentRefreshHz=144, the cap is min(60, 144) = 60. Identical to no cap.
        var d = VrrPolicy.Decide(25.0, Window4060, currentRefreshHz: 144.0, isFpsTrusted: true);
        Assert.That(d.Multiplier, Is.EqualTo(2));
        Assert.That(d.OutputFps, Is.EqualTo(50.0).Within(1e-9));
        Assert.That(d.Reason, Is.EqualTo("ok"));
    }

    [Test]
    public void Cap_Null_BehavesLikeUncapped()
    {
        // 25 fps on [40, 144] with no cap known: strict step 1 finds N=2 → 50 in [40, 144]. Same as cap=144.
        var d = VrrPolicy.Decide(25.0, Window40144, currentRefreshHz: null, isFpsTrusted: true);
        Assert.That(d.Multiplier, Is.EqualTo(2));
        Assert.That(d.OutputFps, Is.EqualTo(50.0).Within(1e-9));
        Assert.That(d.Reason, Is.EqualTo("ok"));
    }

    [Test]
    public void Cap_ZeroOrNegative_TreatedAsUnknown()
    {
        // Defensive: a bogus refresh value (0 mHz mode, etc.) shouldn't cap the ceiling to zero and kill all multipliers.
        var d = VrrPolicy.Decide(25.0, Window40144, currentRefreshHz: 0.0, isFpsTrusted: true);
        Assert.That(d.Multiplier, Is.EqualTo(2));
        Assert.That(d.OutputFps, Is.EqualTo(50.0).Within(1e-9));
    }

    [Test]
    public void Cap_AtSourceRate_PreferStrictOverSlack_24fpsOn60HzPanel()
    {
        // 23.976 NTSC on a [48, 60] panel @ 60 Hz mode. Strict ceil = 3 → 71.928 above cap=60 → strict fails. Slack-floor finds N=2 → 47.952 within slack of 48, ≤ cap=60 → accept.
        var d = VrrPolicy.Decide(23.976, Window4860, currentRefreshHz: 60.0, isFpsTrusted: true);
        Assert.That(d.Multiplier, Is.EqualTo(2));
        Assert.That(d.OutputFps, Is.EqualTo(47.952).Within(1e-9));
        Assert.That(d.Reason, Is.EqualTo("ok (slack)"));
    }

    [Test]
    public void Cap_RespectedByStep2_NotJustStep1()
    {
        // The regression an earlier draft would have shipped: when strict step 1 fails because output exceeds cap, step 2 (slack) must NOT silently allow the same N by widening the ceiling. Cap stays hard in both steps.
        // 23.976 fps, [40, 240] panel — uncapped slack would find N=2 → 47.952 in [39.6, 242.4] easily.
        // With currentRefreshHz=45, strict fails (N=2 → 47.952 > 45), and slack step 2 must ALSO fail because 47.952 > 45 still.
        var d = VrrPolicy.Decide(23.976, new VrrRange(40, 240), currentRefreshHz: 45.0, isFpsTrusted: true);
        Assert.That(d.Multiplier, Is.EqualTo(1));
        Assert.That(d.Reason, Does.Contain("no integer multiple"));
    }

    [Test]
    public void Cap_ExactlyAtMultipleOutput_Accepts()
    {
        // 30 fps on [40, 144] cap=60: strict N=2 → 60 exactly = effMax. <= comparison must accept.
        var d = VrrPolicy.Decide(30.0, Window40144, currentRefreshHz: 60.0, isFpsTrusted: true);
        Assert.That(d.Multiplier, Is.EqualTo(2));
        Assert.That(d.OutputFps, Is.EqualTo(60.0).Within(1e-9));
        Assert.That(d.Reason, Is.EqualTo("ok"));
    }

    [Test]
    public void Cap_JustBelowMultipleOutput_NoIntegerFits()
    {
        // 30 fps on [40, 144] cap=59.94 (NTSC 60Hz mode): strict N=2 → 60 > 59.94. Step 2 nSlack=2 (slackedFloor=38.56), output=60 > 59.94. No fit.
        var d = VrrPolicy.Decide(30.0, Window40144, currentRefreshHz: 59.94, isFpsTrusted: true);
        Assert.That(d.Multiplier, Is.EqualTo(1));
        Assert.That(d.Reason, Does.Contain("no integer multiple"));
    }

    [Test]
    public void Cap_BelowAdvertisedMin_DegenerateWindow_NoFit()
    {
        // Pathological: panel reports [80, 144] but compositor is in a 45 Hz mode. effMax=45 < minHz=80. No multiplier of any plausible source fits in [80, 45] (empty interval).
        var d = VrrPolicy.Decide(25.0, new VrrRange(80, 144), currentRefreshHz: 45.0, isFpsTrusted: true);
        Assert.That(d.Multiplier, Is.EqualTo(1));
        Assert.That(d.Reason, Does.Contain("no integer multiple"));
    }
}
