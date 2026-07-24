using System.Globalization;
using Vomplayer.ViewModels;
using Vomplayer.Wayland;

namespace Vomplayer.Controls;

// Pure-data snapshot consumed by the diagnostic overlay. Kept intentionally flat so the formatter has no GTK/mpv dependencies and is unit-testable without a runtime. IsWaylandPath is explicit rather than derived from DisplayImageDescription==null + VrrClass==Unknown, because those combinations are also valid pre-warmup states on the Wayland path.
//
// DisplayImageDescription vs HdrActive: DisplayImageDescription is the raw preferred-image-description observation for the current wl_output; the display's HDR capability is classified from it via HdrClassifier (PQ/HLG tf OR luminance headroom) and the display: row shows which rule fired plus the raw values, so a compositor behavior change (like KWin 6.6 dropping the PQ preferred tf) is diagnosable at a glance. HdrActive is whether the player has tagged the subsurface PQ — driven only by source HDR (×shim succeeded), not by display HDR-capability. When source is HDR but display is SDR, HdrActive is still true: we delegate the PQ→SDR tone-map to the compositor (KWin uses libplacebo, smplayer/gpu-next quality). The "(intended)" suffix on HdrActive=true hedges honestly — the protocol has no feedback channel, so we can only claim we set the tag, not prove the compositor honors it.
//
// fps + vrr fields: SourceFps mirrors mpv's container-fps; EstimatedVfFps mirrors estimated-vf-fps (drives the trust monitor; surfaced for diagnostic visibility only). IsSourceFpsTrusted + FpsTrustReason describe the trust monitor's verdict for the current file. OutputVrrRange + ConnectorName describe the panel the surface is currently entered on. VrrDecision is the most recent ApplyVrrPolicy outcome.
public readonly record struct DiagnosticSnapshot(
    string? Hwdec,
    bool IsSourceHdr,
    OutputImageDescription? DisplayImageDescription,
    bool HdrActive,
    VrrClassification VrrClass,
    int VrrMeasuredHzCenti,
    bool IsWaylandPath,
    double? SourceFps,
    double? EstimatedVfFps,
    bool IsSourceFpsTrusted,
    string? FpsTrustReason,
    VrrRange? OutputVrrRange,
    string? ConnectorName,
    VrrDecision LastDecision,
    PipSyncDiagnostic Sync);

public static class DiagnosticFormatter
{
    public static string[] FormatLines(DiagnosticSnapshot s)
    {
        return new[]
        {
            "hwdec:   " + FormatHwdec(s.Hwdec),
            "source:  " + (s.IsSourceHdr ? "HDR (PQ/HLG)" : "SDR"),
            "display: " + FormatDisplayHdr(s.IsWaylandPath, s.DisplayImageDescription),
            "active:  " + FormatHdrActive(s.IsWaylandPath, s.HdrActive, s.DisplayImageDescription),
            "VRR:     " + FormatVrr(s.IsWaylandPath, s.VrrClass, s.VrrMeasuredHzCenti),
            "fps:     " + FormatFpsLine(s),
            "vrr:     " + FormatVrrPolicyLine(s),
            "sync:    " + FormatSyncLine(s.Sync),
            "pipspd:  " + FormatSyncSpeed(s.Sync),
        };
    }

    // PiP drift-sync: stored target offset, live current offset, catch-up required (= current − target), and the controller's latch mode. "off" when PiP is disabled; "pending" target until the offset is baselined.
    private static string FormatSyncLine(PipSyncDiagnostic sync)
    {
        if (!sync.Enabled)
        {
            return "off";
        }
        string tgt = sync.TargetOffsetSeconds.HasValue ? SignedSeconds(sync.TargetOffsetSeconds.Value) : "pending";
        string cur = SignedSeconds(sync.CurrentOffsetSeconds);
        string need = sync.DriftSeconds.HasValue ? SignedSeconds(sync.DriftSeconds.Value) : "?";
        return "tgt=" + tgt + " cur=" + cur + " need=" + need + " (" + sync.Mode + ")";
    }

    private static string FormatSyncSpeed(PipSyncDiagnostic sync)
    {
        if (!sync.Enabled)
        {
            return "—";
        }
        return sync.Speed.ToString("F4", CultureInfo.InvariantCulture);
    }

    private static string SignedSeconds(double seconds)
    {
        // ToString already prints a leading '-' for negatives; add an explicit '+' for non-negatives so drift direction reads at a glance.
        string body = seconds.ToString("F3", CultureInfo.InvariantCulture);
        return seconds >= 0 ? "+" + body : body;
    }

    private static string FormatHwdec(string? hwdec)
    {
        // Matches the normalization in Playback.UpdateHwdecCurrent — null and empty are both the "no hwdec" state.
        if (string.IsNullOrEmpty(hwdec))
        {
            return "(none)";
        }
        return hwdec;
    }

    // Renders the classification verdict, WHICH rule justified it, and the raw observed values, so a future compositor behavior change (KWin has already changed its preferred-description shape once) is diagnosable straight off the overlay.
    private static string FormatDisplayHdr(bool isWaylandPath, OutputImageDescription? displayDesc)
    {
        if (!isWaylandPath)
        {
            return "N/A (GLArea)";
        }
        if (displayDesc == null)
        {
            return "unknown";
        }
        OutputImageDescription d = displayDesc.Value;
        string tf = "tf=" + (d.HasTfNamed ? TfName(d.TfNamed) : "?");
        string prim = d.HasPrimariesNamed ? ", prim=" + PrimariesName(d.PrimariesNamed) : "";
        string lum = d.HasLuminances ? ", max=" + d.MaxLum + " ref=" + d.RefLum + " nit" : ", no luminances";
        switch (HdrClassifier.Classify(d))
        {
            case HdrJustification.TransferFunction:
                return "HDR (preferred " + tf + prim + lum + ")";
            case HdrJustification.LuminanceHeadroom:
                return "HDR (luminance headroom: max=" + d.MaxLum + " > ref=" + d.RefLum + " nit, " + tf + prim + ")";
            default:
                return "SDR (" + tf + prim + lum + ")";
        }
    }

    private static string FormatHdrActive(bool isWaylandPath, bool hdrActive, OutputImageDescription? displayDesc)
    {
        if (!isWaylandPath)
        {
            return "N/A (GLArea)";
        }
        if (!hdrActive)
        {
            return "SDR";
        }
        // hdrActive=true means we tagged the subsurface PQ and pinned mpv to PQ targets. Whether the user actually sees HDR scan-out depends on the display: HDR display = pass-through; SDR display = compositor tonemaps PQ→SDR (we delegate to libplacebo via KWin). The "(intended)" hedge applies in both cases — the protocol has no feedback channel proving the compositor honored the tag.
        if (displayDesc != null && HdrClassifier.IsHdr(displayDesc.Value))
        {
            return "HDR (intended)";
        }
        return "HDR→SDR (compositor)";
    }

    // Short names for the wp_color_manager_v1.transfer_function values we expect to encounter; raw number fallback keeps unexpected values diagnosable rather than hidden.
    private static string TfName(uint tf)
    {
        switch (tf)
        {
            case 1: return "bt1886";
            case 2: return "gamma22";
            case 5: return "ext_linear";
            case 9: return "srgb";
            case 11: return "st2084_pq";
            case 13: return "hlg";
            default: return tf.ToString(CultureInfo.InvariantCulture);
        }
    }

    // Short names for the wp_color_manager_v1.primaries values we expect to encounter.
    private static string PrimariesName(uint p)
    {
        switch (p)
        {
            case 1: return "srgb";
            case 6: return "bt2020";
            case 8: return "dci_p3";
            case 9: return "display_p3";
            default: return p.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string FormatVrr(bool isWaylandPath, VrrClassification cls, int measuredHzCenti)
    {
        if (!isWaylandPath)
        {
            return "N/A (GLArea)";
        }
        string label = cls switch
        {
            VrrClassification.Vrr => "VRR",
            VrrClassification.Fixed => "FIXED",
            VrrClassification.CantTell => "CANT-TELL",
            _ => "UNKNOWN",
        };
        // Unknown can mean ring not yet full, no active wl_output, or no mode known for the active output — the bridge collapses all three into Unknown. In every case the Hz number we have is meaningless, so just show the label.
        if (cls == VrrClassification.Unknown)
        {
            return label;
        }
        string hz = (measuredHzCenti / 100.0).ToString("F2", CultureInfo.InvariantCulture);
        return label + " @ " + hz + " Hz";
    }

    // Two-line frame-rate state. Line 1 (fps:) shows declared + estimated source FPS plus the trust verdict; line 2 (vrr:) shows the multiplier decision and the resolved output VRR window.
    private static string FormatFpsLine(DiagnosticSnapshot s)
    {
        if (s.SourceFps == null && s.EstimatedVfFps == null)
        {
            return "(no source)";
        }
        string src = s.SourceFps.HasValue ? s.SourceFps.Value.ToString("F3", CultureInfo.InvariantCulture) : "?";
        string est = s.EstimatedVfFps.HasValue ? s.EstimatedVfFps.Value.ToString("F3", CultureInfo.InvariantCulture) : "?";
        string trust;
        if (s.IsSourceFpsTrusted)
        {
            trust = "trusted";
        }
        else
        {
            trust = string.IsNullOrEmpty(s.FpsTrustReason) ? "untrusted" : "untrusted: " + s.FpsTrustReason;
        }
        return src + " src / " + est + " est (" + trust + ")";
    }

    private static string FormatVrrPolicyLine(DiagnosticSnapshot s)
    {
        if (!s.IsWaylandPath)
        {
            return "N/A (GLArea)";
        }
        string mult = "×" + s.LastDecision.Multiplier.ToString(CultureInfo.InvariantCulture);
        string fps = s.LastDecision.OutputFps > 0
            ? " → " + s.LastDecision.OutputFps.ToString("F3", CultureInfo.InvariantCulture)
            : "";
        string window = FormatVrrWindow(s.OutputVrrRange, s.ConnectorName);
        string status;
        if (s.LastDecision.Multiplier >= 2)
        {
            // Surface the slack-floor path so users troubleshooting borderline NTSC cases (e.g. ×2 → 47.952 on a 48 Hz panel) can see whether the policy took the strict route or the slack fallback. Fragile string match against VrrPolicy's reason; localized here so churn is contained.
            status = s.LastDecision.Reason == "ok (slack)" ? "active, slack-floor" : "active";
        }
        else
        {
            status = "no filter — " + s.LastDecision.Reason;
        }
        return mult + fps + " " + window + " (" + status + ")";
    }

    private static string FormatVrrWindow(VrrRange? range, string? connectorName)
    {
        string conn = string.IsNullOrEmpty(connectorName) ? "?" : connectorName;
        if (range == null)
        {
            return "[" + conn + ", window unknown]";
        }
        string min = range.Value.MinHz.ToString("0.#", CultureInfo.InvariantCulture);
        string max = range.Value.MaxHz.ToString("0.#", CultureInfo.InvariantCulture);
        return "[" + min + "-" + max + " " + conn + "]";
    }
}
