using Vomplayer.Wayland;

namespace Vomplayer.Tests;

[TestFixture]
public class HdrClassifierTests
{
    // Builders for the two real-world shapes: legacy compositors advertise an HDR transfer function (PQ/HLG) outright; modern KWin (observed 6.6.5) advertises gamma22 and expresses HDR-enabled as luminance headroom (max_lum > reference_lum).
    private static OutputImageDescription TfOnly(uint tf)
    {
        return new OutputImageDescription(HasTfNamed: true, TfNamed: tf, HasPrimariesNamed: false, PrimariesNamed: 0, HasLuminances: false, MinLum: 0, MaxLum: 0, RefLum: 0);
    }

    private static OutputImageDescription TfWithLum(uint tf, uint maxLum, uint refLum)
    {
        return new OutputImageDescription(HasTfNamed: true, TfNamed: tf, HasPrimariesNamed: false, PrimariesNamed: 0, HasLuminances: true, MinLum: 0, MaxLum: maxLum, RefLum: refLum);
    }

    [Test]
    public void PqIsHdrViaTransferFunction()
    {
        Assert.That(HdrClassifier.Classify(TfOnly(HdrClassifier.TransferFunctionSt2084Pq)), Is.EqualTo(HdrJustification.TransferFunction));
    }

    [Test]
    public void HlgIsHdrViaTransferFunction()
    {
        Assert.That(HdrClassifier.Classify(TfOnly(HdrClassifier.TransferFunctionHlg)), Is.EqualTo(HdrJustification.TransferFunction));
    }

    [Test]
    public void Gamma22WithHeadroomIsHdrViaLuminanceHeadroom()
    {
        // The exact shape KWin 6.6.5 reports for an HDR-enabled 400-nit panel with SDR brightness at 250 nits: tf=gamma22, max=390, ref=201.
        Assert.That(HdrClassifier.Classify(TfWithLum(2, maxLum: 390, refLum: 201)), Is.EqualTo(HdrJustification.LuminanceHeadroom));
    }

    [Test]
    public void Gamma22WithoutHeadroomIsSdr()
    {
        // KWin's SDR-output shape: max == ref exactly. Strict > keeps these SDR.
        Assert.That(HdrClassifier.Classify(TfWithLum(2, maxLum: 200, refLum: 200)), Is.EqualTo(HdrJustification.None));
    }

    [Test]
    public void MaxBelowRefIsSdr()
    {
        Assert.That(HdrClassifier.Classify(TfWithLum(2, maxLum: 150, refLum: 200)), Is.EqualTo(HdrJustification.None));
    }

    [Test]
    public void SdrTfWithoutLuminancesIsSdr()
    {
        // sRGB = 9, BT.1886 = 1, gamma22 = 2 per wp_color_manager_v1.transfer_function.
        Assert.That(HdrClassifier.Classify(TfOnly(9)), Is.EqualTo(HdrJustification.None));
        Assert.That(HdrClassifier.Classify(TfOnly(1)), Is.EqualTo(HdrJustification.None));
        Assert.That(HdrClassifier.Classify(TfOnly(2)), Is.EqualTo(HdrJustification.None));
    }

    [Test]
    public void AbsentTfWithHeadroomIsHdrViaLuminanceHeadroom()
    {
        // The headroom rule stands alone — a compositor describing the output via tf_power (no tf_named) can still signal HDR through luminances.
        var d = new OutputImageDescription(HasTfNamed: false, TfNamed: 0, HasPrimariesNamed: false, PrimariesNamed: 0, HasLuminances: true, MinLum: 0, MaxLum: 1000, RefLum: 203);
        Assert.That(HdrClassifier.Classify(d), Is.EqualTo(HdrJustification.LuminanceHeadroom));
    }

    [Test]
    public void PqWithHeadroomJustifiesViaTransferFunction()
    {
        // Both signals present: the explicit HDR transfer function is the stronger, unambiguous one and wins the justification.
        Assert.That(HdrClassifier.Classify(TfWithLum(HdrClassifier.TransferFunctionSt2084Pq, maxLum: 1000, refLum: 203)), Is.EqualTo(HdrJustification.TransferFunction));
    }

    [Test]
    public void AbsentEverythingIsSdr()
    {
        var d = new OutputImageDescription(HasTfNamed: false, TfNamed: 0, HasPrimariesNamed: false, PrimariesNamed: 0, HasLuminances: false, MinLum: 0, MaxLum: 0, RefLum: 0);
        Assert.That(HdrClassifier.Classify(d), Is.EqualTo(HdrJustification.None));
    }

    [Test]
    public void WideGamutPrimariesDoNotAffectClassification()
    {
        // BT.2020 primaries (6) are diagnostic-only — wide-gamut SDR panels exist, so primaries must never flip the verdict.
        var d = new OutputImageDescription(HasTfNamed: true, TfNamed: 2, HasPrimariesNamed: true, PrimariesNamed: 6, HasLuminances: true, MinLum: 0, MaxLum: 200, RefLum: 200);
        Assert.That(HdrClassifier.Classify(d), Is.EqualTo(HdrJustification.None));
    }

    [Test]
    public void IsHdrIsTrueForEitherJustification()
    {
        Assert.That(HdrClassifier.IsHdr(TfOnly(HdrClassifier.TransferFunctionSt2084Pq)), Is.True);
        Assert.That(HdrClassifier.IsHdr(TfWithLum(2, maxLum: 390, refLum: 201)), Is.True);
        Assert.That(HdrClassifier.IsHdr(TfWithLum(2, maxLum: 200, refLum: 200)), Is.False);
    }
}
