using System.Globalization;
using Vomplayer.Wayland;

namespace Vomplayer.Controls;

// Pure-data snapshot consumed by the diagnostic overlay. Kept intentionally flat so the formatter has no GTK/mpv dependencies and is unit-testable without a runtime. IsWaylandPath is explicit rather than derived from DisplayIsHdr==null + VrrClass==Unknown, because those combinations are also valid pre-warmup states on the Wayland path.
//
// DisplayIsHdr vs HdrActive: DisplayIsHdr is the physical monitor's HDR capability (what the current wl_output's preferred image description advertises). HdrActive is whether the player has tagged the subsurface PQ — driven only by source HDR (×shim succeeded), no longer by display HDR-capability. When source is HDR but display is SDR, HdrActive is still true: we delegate the PQ→SDR tone-map to the compositor (KWin uses libplacebo, smplayer/gpu-next quality). The "(intended)" suffix on HdrActive=true hedges honestly — the protocol has no feedback channel, so we can only claim we set the tag, not prove the compositor honors it.
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
            "active:  " + FormatHdrActive(s.IsWaylandPath, s.HdrActive, s.DisplayIsHdr),
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

    private static string FormatHdrActive(bool isWaylandPath, bool hdrActive, bool? displayIsHdr)
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
        if (displayIsHdr == true)
        {
            return "HDR (intended)";
        }
        return "HDR→SDR (compositor)";
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
