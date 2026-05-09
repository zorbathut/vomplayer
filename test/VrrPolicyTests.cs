using Vomplayer.Wayland;

namespace Vomplayer.Tests;

[TestFixture]
public class VrrPolicyTests
{
    private static readonly VrrRange Window4860 = new(48, 60);
    private static readonly VrrRange Window4060 = new(40, 60);
    private static readonly VrrRange Window40144 = new(40, 144);
    private static readonly VrrRange Window100144 = new(100, 144);

    [Test]
    public void Untrusted_ClearsRegardlessOfOtherInputs()
    {
        var d = VrrPolicy.Decide(25.0, Window4860, isFpsTrusted: false);
        Assert.That(d.Multiplier, Is.EqualTo(1));
        Assert.That(d.Reason, Does.Contain("untrusted"));
    }

    [Test]
    public void NullSourceFps_NoFilter()
    {
        var d = VrrPolicy.Decide(null, Window4860, isFpsTrusted: true);
        Assert.That(d.Multiplier, Is.EqualTo(1));
        Assert.That(d.Reason, Does.Contain("unavailable or out of range"));
    }

    [Test]
    public void OutOfRangeSourceFps_NoFilter()
    {
        Assert.That(VrrPolicy.Decide(0.0, Window4860, true).Multiplier, Is.EqualTo(1));
        Assert.That(VrrPolicy.Decide(4.0, Window4860, true).Multiplier, Is.EqualTo(1));
        Assert.That(VrrPolicy.Decide(241.0, Window4860, true).Multiplier, Is.EqualTo(1));
        Assert.That(VrrPolicy.Decide(double.NaN, Window4860, true).Multiplier, Is.EqualTo(1));
        Assert.That(VrrPolicy.Decide(double.PositiveInfinity, Window4860, true).Multiplier, Is.EqualTo(1));
    }

    [Test]
    public void NullWindow_NoFilter()
    {
        var d = VrrPolicy.Decide(25.0, null, true);
        Assert.That(d.Multiplier, Is.EqualTo(1));
        Assert.That(d.Reason, Does.Contain("VRR window unknown"));
    }

    [Test]
    public void TwentyFiveFps_FortyEightToSixty_PicksDouble()
    {
        var d = VrrPolicy.Decide(25.0, Window4860, true);
        Assert.That(d.Multiplier, Is.EqualTo(2));
        Assert.That(d.OutputFps, Is.EqualTo(50.0).Within(1e-9));
        Assert.That(d.Reason, Is.EqualTo("ok"));
    }

    [Test]
    public void TwentyFourFps_FortyEightToSixty_PicksDouble_NtscSlackAbsorbed()
    {
        // 23.976 × 2 = 47.952; floor slack (1% of 60 = 0.6 Hz) catches it.
        var d = VrrPolicy.Decide(23.976, Window4860, true);
        Assert.That(d.Multiplier, Is.EqualTo(2));
        Assert.That(d.OutputFps, Is.EqualTo(47.952).Within(1e-9));
    }

    [Test]
    public void TwentyFiveFps_FortyToOneFortyFour_PicksDoubleNotQuintuple()
    {
        // ceil((40 - 1.44) / 25) = ceil(1.5424) = 2. Smallest fitting N — ×2 lands at 50 Hz, not ×5 → 125 Hz.
        var d = VrrPolicy.Decide(25.0, Window40144, true);
        Assert.That(d.Multiplier, Is.EqualTo(2));
        Assert.That(d.OutputFps, Is.EqualTo(50.0).Within(1e-9));
    }

    [Test]
    public void FiftyFps_FortyEightToSixty_AlreadyInRange()
    {
        var d = VrrPolicy.Decide(50.0, Window4860, true);
        Assert.That(d.Multiplier, Is.EqualTo(1));
        Assert.That(d.Reason, Does.Contain("already in/above"));
    }

    [Test]
    public void SixtyFps_FortyEightToSixty_AboveRange()
    {
        var d = VrrPolicy.Decide(60.0, Window4860, true);
        Assert.That(d.Multiplier, Is.EqualTo(1));
    }

    [Test]
    public void TwentyFiveFps_OneHundredToOneFortyFour_PicksQuadruple()
    {
        // ceil((100 - 1.44) / 25) = ceil(3.94) = 4. ×4 = 100 Hz.
        var d = VrrPolicy.Decide(25.0, Window100144, true);
        Assert.That(d.Multiplier, Is.EqualTo(4));
        Assert.That(d.OutputFps, Is.EqualTo(100.0).Within(1e-9));
    }

    [Test]
    public void ThirtyFps_OneHundredToOneFortyFour_PicksQuadrupleAtCeiling()
    {
        // ceil((100 - 1.44) / 30) = ceil(3.285) = 4. ×4 = 120 Hz, well within 144.
        var d = VrrPolicy.Decide(30.0, Window100144, true);
        Assert.That(d.Multiplier, Is.EqualTo(4));
        Assert.That(d.OutputFps, Is.EqualTo(120.0).Within(1e-9));
    }

    [Test]
    public void ThirtyFps_NoIntegerFitsRejects()
    {
        // 33-46 Hz window, 30 fps source: ×1 below floor, ×2 (60) above ceiling. Reject.
        var window = new VrrRange(33, 46);
        var d = VrrPolicy.Decide(30.0, window, true);
        Assert.That(d.Multiplier, Is.EqualTo(1));
        Assert.That(d.Reason, Does.Contain("no integer multiple"));
    }

    [Test]
    public void TwentyFiveFps_FortyToSixty_PicksDouble_FloorSlackNotNeeded()
    {
        // 25 × 2 = 50, comfortably above 40 floor.
        var d = VrrPolicy.Decide(25.0, Window4060, true);
        Assert.That(d.Multiplier, Is.EqualTo(2));
        Assert.That(d.OutputFps, Is.EqualTo(50.0).Within(1e-9));
    }
}
