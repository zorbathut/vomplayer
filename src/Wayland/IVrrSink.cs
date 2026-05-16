using System;

namespace Vomplayer.Wayland;

// Resolved VRR window for a single output: the inclusive range of refresh rates the panel can adaptively scan at. Both bounds in Hz. Sourced from EDID's Display Range Limits descriptor today; UserConfig override surface is deferred to a follow-up.
public readonly record struct VrrRange(double MinHz, double MaxHz);

// Decision computed by VrrPolicy.Decide. Multiplier == 1 means "do not apply the fps filter"; in that case OutputFps echoes the source rate (or 0 when the source rate is unavailable) and Reason explains the no-op. For Multiplier ≥ 2, OutputFps is the post-filter rate.
public readonly record struct VrrDecision(int Multiplier, double OutputFps, string Reason);

// The slice of VideoSurface that a per-video VRR-policy implementation needs. Letting VideoContext (which is Wayland-agnostic — same class drives the GLArea fallback path) hold an IVrrSink? avoids dragging the concrete VideoSurface (and with it, every Wayland-specific dependency) into the ViewModels namespace. On the GLArea path the context is constructed with no sink attached and VRR policy short-circuits to a no-op (multiplier stays implicit ×1). Mirrors IHdrSink in shape — no Set* method here because the multiplier is applied via mpv (Playback.SetFrameMultiplier), not via a Wayland-side surface tag.
public interface IVrrSink
{
    // Resolved VRR window for the output the surface is currently entered on. null = unknown (no active output yet, output's name not yet observed, or EDID had no Range Limits descriptor).
    VrrRange? CurrentOutputVrrRange { get; }

    // Current scanout refresh rate (Hz) of the output the surface is entered on — read lazily from wl_output.mode each time the policy runs. null = unknown (no active output, or mode event not yet landed). VrrPolicy uses this as a hard ceiling: compositors won't VRR-scan above the configured mode's pixel clock, so EDID's "max VRR" can be unreachable in the current mode. No standalone change event — the realistic re-trigger paths (active-output change, VRR-range change) already cover mode transitions; a runtime user-driven mode switch on the same output without anything else changing is rare enough that the policy can stay stale until next file load.
    double? CurrentOutputRefreshHz { get; }

    // Fires on transitions of CurrentOutputVrrRange (deduped against last published value by the implementation). Same contract as CurrentOutputHdrChanged.
    event Action CurrentOutputVrrRangeChanged;
}
