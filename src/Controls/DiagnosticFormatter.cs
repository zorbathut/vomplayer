using System.Globalization;
using Vomplayer.Wayland;

namespace Vomplayer.Controls;

// Pure-data snapshot consumed by the diagnostic overlay. Kept intentionally flat so the formatter has no GTK/mpv dependencies and is unit-testable without a runtime. IsWaylandPath is explicit rather than derived from OutputIsHdr==null + VrrClass==Unknown, because those combinations are also valid pre-warmup states on the Wayland path.
public readonly record struct DiagnosticSnapshot(
    string? Hwdec,
    bool IsSourceHdr,
    bool? OutputIsHdr,
    VrrClassification VrrClass,
    int VrrHzCenti,
    int VrrSampleCount,
    bool IsWaylandPath);

public static class DiagnosticFormatter
{
    public static string[] FormatLines(DiagnosticSnapshot s)
    {
        return new[]
        {
            "hwdec:  " + FormatHwdec(s.Hwdec),
            "source: " + (s.IsSourceHdr ? "HDR (PQ/HLG)" : "SDR"),
            "output: " + FormatOutputHdr(s.IsWaylandPath, s.OutputIsHdr),
            "VRR:    " + FormatVrr(s.IsWaylandPath, s.VrrClass, s.VrrHzCenti, s.VrrSampleCount),
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

    private static string FormatOutputHdr(bool isWaylandPath, bool? outputIsHdr)
    {
        if (!isWaylandPath)
        {
            return "N/A (GLArea)";
        }
        if (outputIsHdr == null)
        {
            return "unknown";
        }
        return outputIsHdr.Value ? "HDR" : "SDR";
    }

    private static string FormatVrr(bool isWaylandPath, VrrClassification cls, int hzCenti, int sampleCount)
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
        string hz = (hzCenti / 100.0).ToString("F2", CultureInfo.InvariantCulture);
        return label + " @ " + hz + " Hz (" + sampleCount + "/" + FrameTimingBridge.RingSize + ")";
    }
}
