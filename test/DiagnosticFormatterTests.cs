using Vomplayer.Controls;
using Vomplayer.Wayland;

namespace Vomplayer.Tests;

[TestFixture]
public class DiagnosticFormatterTests
{
    private static DiagnosticSnapshot Baseline()
    {
        return new DiagnosticSnapshot(
            Hwdec: "vaapi",
            IsSourceHdr: false,
            OutputIsHdr: false,
            VrrClass: VrrClassification.Fixed,
            VrrHzCenti: 6000,
            VrrSampleCount: 60,
            IsWaylandPath: true);
    }

    [Test]
    public void HwdecNullRendersAsNone()
    {
        var s = Baseline() with { Hwdec = null };
        Assert.That(DiagnosticFormatter.FormatLines(s)[0], Is.EqualTo("hwdec:  (none)"));
    }

    [Test]
    public void HwdecEmptyRendersAsNone()
    {
        var s = Baseline() with { Hwdec = "" };
        Assert.That(DiagnosticFormatter.FormatLines(s)[0], Is.EqualTo("hwdec:  (none)"));
    }

    [Test]
    public void HwdecRendersVerbatim()
    {
        var s = Baseline() with { Hwdec = "nvdec" };
        Assert.That(DiagnosticFormatter.FormatLines(s)[0], Is.EqualTo("hwdec:  nvdec"));
    }

    // mpv reports literal "no" for software fallback in some contexts (alongside the null/empty paths Playback.UpdateHwdecCurrent normalizes). The formatter passes it through verbatim — same shape as the existing stderr log line "[vomplayer] hwdec: no" — rather than re-normalizing to "(none)". Pinning the current behavior here so a future "helpful" re-normalization is a deliberate test update.
    [Test]
    public void HwdecNoRendersVerbatim()
    {
        var s = Baseline() with { Hwdec = "no" };
        Assert.That(DiagnosticFormatter.FormatLines(s)[0], Is.EqualTo("hwdec:  no"));
    }

    [Test]
    public void SourceHdrRendersHdrTag()
    {
        var s = Baseline() with { IsSourceHdr = true };
        Assert.That(DiagnosticFormatter.FormatLines(s)[1], Is.EqualTo("source: HDR (PQ/HLG)"));
    }

    [Test]
    public void SourceSdrRendersSdr()
    {
        var s = Baseline() with { IsSourceHdr = false };
        Assert.That(DiagnosticFormatter.FormatLines(s)[1], Is.EqualTo("source: SDR"));
    }

    [Test]
    public void OutputHdrOnWaylandTrueRendersHdr()
    {
        var s = Baseline() with { OutputIsHdr = true };
        Assert.That(DiagnosticFormatter.FormatLines(s)[2], Is.EqualTo("output: HDR"));
    }

    [Test]
    public void OutputHdrOnWaylandFalseRendersSdr()
    {
        var s = Baseline() with { OutputIsHdr = false };
        Assert.That(DiagnosticFormatter.FormatLines(s)[2], Is.EqualTo("output: SDR"));
    }

    [Test]
    public void OutputHdrOnWaylandNullRendersUnknown()
    {
        var s = Baseline() with { OutputIsHdr = null };
        Assert.That(DiagnosticFormatter.FormatLines(s)[2], Is.EqualTo("output: unknown"));
    }

    [Test]
    public void OutputHdrOnGlareaRendersNA()
    {
        var s = Baseline() with { IsWaylandPath = false, OutputIsHdr = null };
        Assert.That(DiagnosticFormatter.FormatLines(s)[2], Is.EqualTo("output: N/A (GLArea)"));
    }

    [Test]
    public void VrrOnGlareaRendersNA()
    {
        var s = Baseline() with { IsWaylandPath = false, VrrClass = VrrClassification.Unknown, VrrHzCenti = 0, VrrSampleCount = 0 };
        Assert.That(DiagnosticFormatter.FormatLines(s)[3], Is.EqualTo("VRR:    N/A (GLArea)"));
    }

    [Test]
    public void VrrUnknownOnWaylandZeroSamples()
    {
        var s = Baseline() with { VrrClass = VrrClassification.Unknown, VrrHzCenti = 0, VrrSampleCount = 0 };
        Assert.That(DiagnosticFormatter.FormatLines(s)[3], Is.EqualTo("VRR:    UNKNOWN @ 0.00 Hz (0/60)"));
    }

    [Test]
    public void VrrFixedAt60Hz()
    {
        var s = Baseline() with { VrrClass = VrrClassification.Fixed, VrrHzCenti = 6000, VrrSampleCount = 60 };
        Assert.That(DiagnosticFormatter.FormatLines(s)[3], Is.EqualTo("VRR:    FIXED @ 60.00 Hz (60/60)"));
    }

    [Test]
    public void VrrVrrAt144Hz()
    {
        var s = Baseline() with { VrrClass = VrrClassification.Vrr, VrrHzCenti = 14400, VrrSampleCount = 60 };
        Assert.That(DiagnosticFormatter.FormatLines(s)[3], Is.EqualTo("VRR:    VRR @ 144.00 Hz (60/60)"));
    }

    [Test]
    public void VrrCantTellRenders()
    {
        var s = Baseline() with { VrrClass = VrrClassification.CantTell, VrrHzCenti = 6000, VrrSampleCount = 60 };
        Assert.That(DiagnosticFormatter.FormatLines(s)[3], Is.EqualTo("VRR:    CANT-TELL @ 60.00 Hz (60/60)"));
    }

    [Test]
    public void VrrSamplePartiallyFilled()
    {
        var s = Baseline() with { VrrClass = VrrClassification.Unknown, VrrHzCenti = 0, VrrSampleCount = 37 };
        Assert.That(DiagnosticFormatter.FormatLines(s)[3], Is.EqualTo("VRR:    UNKNOWN @ 0.00 Hz (37/60)"));
    }

    [Test]
    public void GoldenFullSnapshot()
    {
        var s = new DiagnosticSnapshot(
            Hwdec: "vaapi",
            IsSourceHdr: true,
            OutputIsHdr: true,
            VrrClass: VrrClassification.Fixed,
            VrrHzCenti: 6000,
            VrrSampleCount: 60,
            IsWaylandPath: true);
        Assert.That(DiagnosticFormatter.FormatLines(s), Is.EqualTo(new[]
        {
            "hwdec:  vaapi",
            "source: HDR (PQ/HLG)",
            "output: HDR",
            "VRR:    FIXED @ 60.00 Hz (60/60)",
        }));
    }
}
