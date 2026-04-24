using System.Globalization;
using Vomplayer.Wayland;

namespace Vomplayer.Controls;

// Pure-data snapshot consumed by the diagnostic overlay. Kept intentionally flat so the formatter has no GTK/mpv dependencies and is unit-testable without a runtime. IsWaylandPath is explicit rather than derived from DisplayIsHdr==null + VrrClass==Unknown, because those combinations are also valid pre-warmup states on the Wayland path.
//
// DisplayIsHdr vs HdrActive: DisplayIsHdr is the physical monitor's HDR capability (what the current wl_output's preferred image description advertises). HdrActive is whether the player has actually HDR-tagged the subsurface (the Hdr outcome of MainWindow.ApplyHdrPolicy — source PQ/HLG × display HDR × shim succeeded). HdrActive=true is rendered as "HDR (intended)" rather than "HDR" because the Wayland color-management protocol has no feedback channel: we can only claim we set the tag, not prove the compositor honors it at scan-out.
public readonly record struct DiagnosticSnapshot(
    string? Hwdec,
    bool IsSourceHdr,
    bool? DisplayIsHdr,
    bool HdrActive,
    VrrClassification VrrClass,
    int VrrMeasuredHzCenti,
    bool IsWaylandPath);

public static class DiagnosticFormatter
{
    public static string[] FormatLines(DiagnosticSnapshot s)
    {
        return new[]
        {
            "hwdec:   " + FormatHwdec(s.Hwdec),
            "source:  " + (s.IsSourceHdr ? "HDR (PQ/HLG)" : "SDR"),
            "display: " + FormatDisplayHdr(s.IsWaylandPath, s.DisplayIsHdr),
            "active:  " + FormatHdrActive(s.IsWaylandPath, s.HdrActive),
            "VRR:     " + FormatVrr(s.IsWaylandPath, s.VrrClass, s.VrrMeasuredHzCenti),
        };
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

    private static string FormatDisplayHdr(bool isWaylandPath, bool? displayIsHdr)
    {
        if (!isWaylandPath)
        {
            return "N/A (GLArea)";
        }
        if (displayIsHdr == null)
        {
            return "unknown";
        }
        return displayIsHdr.Value ? "HDR" : "SDR";
    }

    private static string FormatHdrActive(bool isWaylandPath, bool hdrActive)
    {
        if (!isWaylandPath)
        {
            return "N/A (GLArea)";
        }
        // "(intended)" hedges honestly: we've set the Wayland image description and mpv target-trc, but the protocol has no feedback channel that would let us prove the compositor is actually scanning out in HDR. If the user hits this line reading HDR and their eyes disagree, the culprit is somewhere past our last observable hop.
        return hdrActive ? "HDR (intended)" : "SDR";
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
}
