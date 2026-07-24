using Vomplayer.Controls;
using Vomplayer.ViewModels;
using Vomplayer.Wayland;

namespace Vomplayer.Tests;

[TestFixture]
public class DiagnosticFormatterTests
{
    // Real-world description shapes as observed on KWin 6.6.5 (see HdrClassifier): SDR outputs report gamma22/sRGB with max==ref; the HDR-enabled output reports gamma22/BT.2020 with luminance headroom; legacy compositors report a PQ preferred tf outright.
    private static readonly OutputImageDescription SdrDesc = new(HasTfNamed: true, TfNamed: 2, HasPrimariesNamed: true, PrimariesNamed: 1, HasLuminances: true, MinLum: 100, MaxLum: 200, RefLum: 200);
    private static readonly OutputImageDescription HdrHeadroomDesc = new(HasTfNamed: true, TfNamed: 2, HasPrimariesNamed: true, PrimariesNamed: 6, HasLuminances: true, MinLum: 0, MaxLum: 390, RefLum: 201);
    private static readonly OutputImageDescription HdrPqDesc = new(HasTfNamed: true, TfNamed: 11, HasPrimariesNamed: false, PrimariesNamed: 0, HasLuminances: true, MinLum: 0, MaxLum: 1000, RefLum: 203);

    private static DiagnosticSnapshot Baseline()
    {
        return new DiagnosticSnapshot(
            Hwdec: "vaapi",
            IsSourceHdr: false,
            DisplayImageDescription: SdrDesc,
            HdrActive: false,
            VrrClass: VrrClassification.Fixed,
            VrrMeasuredHzCenti: 6000,
            IsWaylandPath: true,
            SourceFps: 25.0,
            EstimatedVfFps: 25.001,
            IsSourceFpsTrusted: true,
            FpsTrustReason: "",
            OutputVrrRange: new VrrRange(48, 60),
            ConnectorName: "HDMI-A-1",
            LastDecision: new VrrDecision(2, 50.0, "ok"),
            Sync: new PipSyncDiagnostic(false, null, 0, null, 1.0, "off"));
    }

    [Test]
    public void HwdecNullRendersAsNone()
    {
        var s = Baseline() with { Hwdec = null };
        Assert.That(DiagnosticFormatter.FormatLines(s)[0], Is.EqualTo("hwdec:   (none)"));
    }

    [Test]
    public void HwdecEmptyRendersAsNone()
    {
        var s = Baseline() with { Hwdec = "" };
        Assert.That(DiagnosticFormatter.FormatLines(s)[0], Is.EqualTo("hwdec:   (none)"));
    }

    [Test]
    public void HwdecRendersVerbatim()
    {
        var s = Baseline() with { Hwdec = "nvdec" };
        Assert.That(DiagnosticFormatter.FormatLines(s)[0], Is.EqualTo("hwdec:   nvdec"));
    }

    // mpv reports literal "no" for software fallback in some contexts (alongside the null/empty paths Playback.UpdateHwdecCurrent normalizes). The formatter passes it through verbatim — same shape as the existing stderr log line "[vomplayer] hwdec: no" — rather than re-normalizing to "(none)". Pinning the current behavior here so a future "helpful" re-normalization is a deliberate test update.
    [Test]
    public void HwdecNoRendersVerbatim()
    {
        var s = Baseline() with { Hwdec = "no" };
        Assert.That(DiagnosticFormatter.FormatLines(s)[0], Is.EqualTo("hwdec:   no"));
    }

    [Test]
    public void SourceHdrRendersHdrTag()
    {
        var s = Baseline() with { IsSourceHdr = true };
        Assert.That(DiagnosticFormatter.FormatLines(s)[1], Is.EqualTo("source:  HDR (PQ/HLG)"));
    }

    [Test]
    public void SourceSdrRendersSdr()
    {
        var s = Baseline() with { IsSourceHdr = false };
        Assert.That(DiagnosticFormatter.FormatLines(s)[1], Is.EqualTo("source:  SDR"));
    }

    // The display: row surfaces the classification verdict, WHICH rule justified it, and the raw observed values, so a compositor-side behavior change (KWin 6.6 dropped the PQ preferred tf in favor of luminance headroom) is diagnosable straight off the overlay.
    [Test]
    public void DisplayHdrViaHeadroomRendersJustificationAndValues()
    {
        var s = Baseline() with { DisplayImageDescription = HdrHeadroomDesc };
        Assert.That(DiagnosticFormatter.FormatLines(s)[2], Is.EqualTo("display: HDR (luminance headroom: max=390 > ref=201 nit, tf=gamma22, prim=bt2020)"));
    }

    [Test]
    public void DisplayHdrViaPreferredTfRendersJustificationAndValues()
    {
        var s = Baseline() with { DisplayImageDescription = HdrPqDesc };
        Assert.That(DiagnosticFormatter.FormatLines(s)[2], Is.EqualTo("display: HDR (preferred tf=st2084_pq, max=1000 ref=203 nit)"));
    }

    [Test]
    public void DisplayHdrViaPreferredTfWithoutLuminancesRendersAbsence()
    {
        var s = Baseline() with { DisplayImageDescription = HdrPqDesc with { HasLuminances = false } };
        Assert.That(DiagnosticFormatter.FormatLines(s)[2], Is.EqualTo("display: HDR (preferred tf=st2084_pq, no luminances)"));
    }

    [Test]
    public void DisplaySdrRendersRawValues()
    {
        var s = Baseline() with { DisplayImageDescription = SdrDesc };
        Assert.That(DiagnosticFormatter.FormatLines(s)[2], Is.EqualTo("display: SDR (tf=gamma22, prim=srgb, max=200 ref=200 nit)"));
    }

    [Test]
    public void DisplaySdrWithUnknownTfValueRendersRawNumber()
    {
        // A tf value outside the short-name map must surface as the raw enum number, not vanish.
        var s = Baseline() with { DisplayImageDescription = SdrDesc with { TfNamed = 4, HasPrimariesNamed = false, HasLuminances = false } };
        Assert.That(DiagnosticFormatter.FormatLines(s)[2], Is.EqualTo("display: SDR (tf=4, no luminances)"));
    }

    [Test]
    public void DisplaySdrWithAbsentTfRendersQuestionMark()
    {
        var s = Baseline() with { DisplayImageDescription = new OutputImageDescription(HasTfNamed: false, TfNamed: 0, HasPrimariesNamed: false, PrimariesNamed: 0, HasLuminances: true, MinLum: 100, MaxLum: 200, RefLum: 200) };
        Assert.That(DiagnosticFormatter.FormatLines(s)[2], Is.EqualTo("display: SDR (tf=?, max=200 ref=200 nit)"));
    }

    [Test]
    public void DisplayHdrOnWaylandNullRendersUnknown()
    {
        var s = Baseline() with { DisplayImageDescription = null };
        Assert.That(DiagnosticFormatter.FormatLines(s)[2], Is.EqualTo("display: unknown"));
    }

    [Test]
    public void DisplayHdrOnGlareaRendersNA()
    {
        var s = Baseline() with { IsWaylandPath = false, DisplayImageDescription = null };
        Assert.That(DiagnosticFormatter.FormatLines(s)[2], Is.EqualTo("display: N/A (GLArea)"));
    }

    // The "active:" line reports whether the player has HDR-tagged the subsurface — the Hdr outcome of MainWindow.ApplyHdrPolicy. The label splits by display HDR-capability: HDR display = "HDR (intended)" (compositor passes PQ through to scan-out); SDR display = "HDR→SDR (compositor)" (compositor tonemaps PQ→SDR, since we deliberately delegate that to libplacebo via KWin instead of mpv's gl_video). HdrActive=false collapses to "SDR" regardless of display. The "(intended)" hedge is deliberate on the HDR-display branch: Wayland color-management has no feedback channel proving the compositor honors our request at scan-out.
    [Test]
    public void HdrActiveOnHdrDisplayRendersIntended()
    {
        // Classification for the active: line runs on the record — headroom-HDR counts as an HDR display just like a PQ preferred tf would.
        var s = Baseline() with { HdrActive = true, DisplayImageDescription = HdrHeadroomDesc };
        Assert.That(DiagnosticFormatter.FormatLines(s)[3], Is.EqualTo("active:  HDR (intended)"));
    }

    [Test]
    public void HdrActiveOnSdrDisplayRendersCompositorTonemap()
    {
        var s = Baseline() with { HdrActive = true, DisplayImageDescription = SdrDesc };
        Assert.That(DiagnosticFormatter.FormatLines(s)[3], Is.EqualTo("active:  HDR→SDR (compositor)"));
    }

    [Test]
    public void HdrActiveOnUnknownDisplayRendersCompositorTonemap()
    {
        // Unknown display capability: assume the conservative "the compositor will handle it" framing rather than promising HDR scan-out we can't verify. Matches the SDR-display branch.
        var s = Baseline() with { HdrActive = true, DisplayImageDescription = null };
        Assert.That(DiagnosticFormatter.FormatLines(s)[3], Is.EqualTo("active:  HDR→SDR (compositor)"));
    }

    [Test]
    public void HdrActiveFalseRendersSdr()
    {
        var s = Baseline() with { HdrActive = false };
        Assert.That(DiagnosticFormatter.FormatLines(s)[3], Is.EqualTo("active:  SDR"));
    }

    [Test]
    public void HdrActiveOnGlareaRendersNA()
    {
        // On GLArea, HdrActive is ignored because IsWaylandPath=false short-circuits. Verify by setting it to true (which would otherwise render an HDR-flavor label).
        var s = Baseline() with { IsWaylandPath = false, DisplayImageDescription = null, HdrActive = true };
        Assert.That(DiagnosticFormatter.FormatLines(s)[3], Is.EqualTo("active:  N/A (GLArea)"));
    }

    [Test]
    public void VrrOnGlareaRendersNA()
    {
        var s = Baseline() with { IsWaylandPath = false, VrrClass = VrrClassification.Unknown, VrrMeasuredHzCenti = 0, DisplayImageDescription = null };
        Assert.That(DiagnosticFormatter.FormatLines(s)[4], Is.EqualTo("VRR:     N/A (GLArea)"));
    }

    [Test]
    public void VrrUnknownRendersLabelOnly()
    {
        // Pre-warmup: ring not yet full → classifier returns Unknown. We deliberately omit the Hz reading because a partial-ring measurement would be misleading; "UNKNOWN" alone is enough to communicate "still warming up."
        var s = Baseline() with { VrrClass = VrrClassification.Unknown, VrrMeasuredHzCenti = 0 };
        Assert.That(DiagnosticFormatter.FormatLines(s)[4], Is.EqualTo("VRR:     UNKNOWN"));
    }

    [Test]
    public void VrrUnknownSuppressesNonZeroMeasured()
    {
        // Defensive: the formatter must not leak measured Hz when classification is Unknown, even if the bridge happens to have a partial-ring measurement. Whether the bridge ever does this is implementation detail of the bridge — the formatter contract is "Unknown ⇒ no Hz."
        var s = Baseline() with { VrrClass = VrrClassification.Unknown, VrrMeasuredHzCenti = 5000 };
        Assert.That(DiagnosticFormatter.FormatLines(s)[4], Is.EqualTo("VRR:     UNKNOWN"));
    }

    [Test]
    public void VrrFixedAt60Hz()
    {
        var s = Baseline() with { VrrClass = VrrClassification.Fixed, VrrMeasuredHzCenti = 6000 };
        Assert.That(DiagnosticFormatter.FormatLines(s)[4], Is.EqualTo("VRR:     FIXED @ 60.00 Hz"));
    }

    [Test]
    public void VrrVrrAt50Hz()
    {
        // Canonical bug case: 50 fps content scanned out via VRR. Measured rate (50.00) is the useful number; the panel's 60Hz nominal mode is irrelevant to what the player is presenting.
        var s = Baseline() with { VrrClass = VrrClassification.Vrr, VrrMeasuredHzCenti = 5000 };
        Assert.That(DiagnosticFormatter.FormatLines(s)[4], Is.EqualTo("VRR:     VRR @ 50.00 Hz"));
    }

    [Test]
    public void VrrVrrAt144Hz()
    {
        var s = Baseline() with { VrrClass = VrrClassification.Vrr, VrrMeasuredHzCenti = 14400 };
        Assert.That(DiagnosticFormatter.FormatLines(s)[4], Is.EqualTo("VRR:     VRR @ 144.00 Hz"));
    }

    [Test]
    public void VrrCantTellRenders()
    {
        var s = Baseline() with { VrrClass = VrrClassification.CantTell, VrrMeasuredHzCenti = 6000 };
        Assert.That(DiagnosticFormatter.FormatLines(s)[4], Is.EqualTo("VRR:     CANT-TELL @ 60.00 Hz"));
    }

    [Test]
    public void GoldenFullSnapshot()
    {
        var s = new DiagnosticSnapshot(
            Hwdec: "vaapi",
            IsSourceHdr: true,
            DisplayImageDescription: HdrHeadroomDesc,
            HdrActive: true,
            VrrClass: VrrClassification.Fixed,
            VrrMeasuredHzCenti: 6000,
            IsWaylandPath: true,
            SourceFps: 25.0,
            EstimatedVfFps: 25.001,
            IsSourceFpsTrusted: true,
            FpsTrustReason: "",
            OutputVrrRange: new VrrRange(48, 60),
            ConnectorName: "HDMI-A-1",
            LastDecision: new VrrDecision(2, 50.0, "ok"),
            Sync: new PipSyncDiagnostic(true, 0.5, 0.52, 0.02, 1.05, "catchup"));
        Assert.That(DiagnosticFormatter.FormatLines(s), Is.EqualTo(new[]
        {
            "hwdec:   vaapi",
            "source:  HDR (PQ/HLG)",
            "display: HDR (luminance headroom: max=390 > ref=201 nit, tf=gamma22, prim=bt2020)",
            "active:  HDR (intended)",
            "VRR:     FIXED @ 60.00 Hz",
            "fps:     25.000 src / 25.001 est (trusted)",
            "vrr:     ×2 → 50.000 [48-60 HDMI-A-1] (active)",
            "sync:    tgt=+0.500 cur=+0.520 need=+0.020 (catchup)",
            "pipspd:  1.0500",
        }));
    }

    [Test]
    public void FpsLineRendersTrustedTwentyFiveFps()
    {
        var s = Baseline();
        Assert.That(DiagnosticFormatter.FormatLines(s)[5], Is.EqualTo("fps:     25.000 src / 25.001 est (trusted)"));
    }

    [Test]
    public void FpsLineRendersUntrustedWithReason()
    {
        var s = Baseline() with
        {
            SourceFps = 27.3,
            EstimatedVfFps = 23.1,
            IsSourceFpsTrusted = false,
            FpsTrustReason = "divergence sustained 4.2s (declared=27.300, est=23.100)",
        };
        Assert.That(DiagnosticFormatter.FormatLines(s)[5], Does.StartWith("fps:     27.300 src / 23.100 est (untrusted: "));
        Assert.That(DiagnosticFormatter.FormatLines(s)[5], Does.Contain("divergence sustained 4.2s"));
    }

    [Test]
    public void FpsLineRendersNoSourceWhenBothNull()
    {
        var s = Baseline() with { SourceFps = null, EstimatedVfFps = null };
        Assert.That(DiagnosticFormatter.FormatLines(s)[5], Is.EqualTo("fps:     (no source)"));
    }

    [Test]
    public void FpsLineRendersQuestionMarkForMissingValues()
    {
        var s = Baseline() with { EstimatedVfFps = null };
        Assert.That(DiagnosticFormatter.FormatLines(s)[5], Is.EqualTo("fps:     25.000 src / ? est (trusted)"));
    }

    [Test]
    public void VrrPolicyLineRendersActiveDouble()
    {
        var s = Baseline();
        Assert.That(DiagnosticFormatter.FormatLines(s)[6], Is.EqualTo("vrr:     ×2 → 50.000 [48-60 HDMI-A-1] (active)"));
    }

    [Test]
    public void VrrPolicyLineRendersSlackFloorActive()
    {
        // ×2 at 47.952 on a 48 Hz nominal floor — the policy took the slack-floor fallback (NTSC 23.976 × 2 = 47.952 below the strict floor but within manufacturer tolerance). Diagnostic should surface "slack-floor" so the user knows which path was taken.
        var s = Baseline() with
        {
            SourceFps = 23.976,
            LastDecision = new VrrDecision(2, 47.952, "ok (slack)"),
        };
        Assert.That(DiagnosticFormatter.FormatLines(s)[6], Is.EqualTo("vrr:     ×2 → 47.952 [48-60 HDMI-A-1] (active, slack-floor)"));
    }

    [Test]
    public void VrrPolicyLineRendersWindowUnknown()
    {
        var s = Baseline() with
        {
            OutputVrrRange = null,
            LastDecision = new VrrDecision(1, 25.0, "VRR window unknown"),
        };
        Assert.That(DiagnosticFormatter.FormatLines(s)[6], Is.EqualTo("vrr:     ×1 → 25.000 [HDMI-A-1, window unknown] (no filter — VRR window unknown)"));
    }

    [Test]
    public void VrrPolicyLineRendersUntrustedNoFilter()
    {
        var s = Baseline() with
        {
            IsSourceFpsTrusted = false,
            LastDecision = new VrrDecision(1, 27.3, "source FPS untrusted (likely VFR)"),
        };
        Assert.That(DiagnosticFormatter.FormatLines(s)[6], Does.Contain("(no filter — source FPS untrusted"));
    }

    [Test]
    public void VrrPolicyLineOnGlareaRendersNA()
    {
        var s = Baseline() with { IsWaylandPath = false };
        Assert.That(DiagnosticFormatter.FormatLines(s)[6], Is.EqualTo("vrr:     N/A (GLArea)"));
    }

    [Test]
    public void VrrPolicyLineRendersUnknownConnector()
    {
        var s = Baseline() with
        {
            ConnectorName = null,
            OutputVrrRange = null,
            LastDecision = new VrrDecision(1, 25.0, "VRR window unknown"),
        };
        Assert.That(DiagnosticFormatter.FormatLines(s)[6], Is.EqualTo("vrr:     ×1 → 25.000 [?, window unknown] (no filter — VRR window unknown)"));
    }

    [Test]
    public void SyncLineRendersOffWhenPipDisabled()
    {
        var s = Baseline() with { Sync = new PipSyncDiagnostic(false, null, 0, null, 1.0, "off") };
        Assert.That(DiagnosticFormatter.FormatLines(s)[7], Is.EqualTo("sync:    off"));
        Assert.That(DiagnosticFormatter.FormatLines(s)[8], Is.EqualTo("pipspd:  —"));
    }

    [Test]
    public void SyncLineRendersSignedOffsetsDriftAndMode()
    {
        var s = Baseline() with { Sync = new PipSyncDiagnostic(true, -0.2, -0.15, 0.05, 0.95, "approach") };
        Assert.That(DiagnosticFormatter.FormatLines(s)[7], Is.EqualTo("sync:    tgt=-0.200 cur=-0.150 need=+0.050 (approach)"));
        Assert.That(DiagnosticFormatter.FormatLines(s)[8], Is.EqualTo("pipspd:  0.9500"));
    }

    [Test]
    public void SyncLineRendersPendingTargetWhenNotBaselined()
    {
        var s = Baseline() with { Sync = new PipSyncDiagnostic(true, null, 0.3, null, 1.0, "approach") };
        Assert.That(DiagnosticFormatter.FormatLines(s)[7], Is.EqualTo("sync:    tgt=pending cur=+0.300 need=? (approach)"));
    }
}
