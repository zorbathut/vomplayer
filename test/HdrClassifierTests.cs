using Vomplayer.Wayland;

namespace Vomplayer.Tests;

[TestFixture]
public class HdrClassifierTests
{
    [Test]
    public void PqIsHdr()
    {
        Assert.That(HdrClassifier.IsHdr(true, HdrClassifier.TransferFunctionSt2084Pq), Is.True);
    }

    [Test]
    public void HlgIsHdr()
    {
        Assert.That(HdrClassifier.IsHdr(true, HdrClassifier.TransferFunctionHlg), Is.True);
    }

    [Test]
    public void SrgbIsSdr()
    {
        // sRGB = 9 per wp_color_manager_v1.transfer_function.
        Assert.That(HdrClassifier.IsHdr(true, 9), Is.False);
    }

    [Test]
    public void Bt1886IsSdr()
    {
        // BT.1886 = 1.
        Assert.That(HdrClassifier.IsHdr(true, 1), Is.False);
    }

    [Test]
    public void Gamma22IsSdr()
    {
        // GAMMA22 = 2.
        Assert.That(HdrClassifier.IsHdr(true, 2), Is.False);
    }

    [Test]
    public void AbsentTfNamedIsSdr()
    {
        Assert.That(HdrClassifier.IsHdr(false, 0), Is.False);
        Assert.That(HdrClassifier.IsHdr(false, HdrClassifier.TransferFunctionSt2084Pq), Is.False);
    }
}
