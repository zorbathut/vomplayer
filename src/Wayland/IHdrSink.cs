using System;

namespace Vomplayer.Wayland;

// The slice of VideoSurface that a per-video HDR-policy implementation needs. Letting VideoContext (which is Wayland-agnostic — same class drives the GLArea fallback path) hold an IHdrSink? avoids dragging the concrete VideoSurface (and with it, every Wayland-specific dependency) into the ViewModels namespace. On the GLArea path the context is constructed with no sink attached and HDR policy short-circuits to a no-op.
//
// Contract:
//  - SetHdr(true) attaches a PQ/BT.2020 image description; SetHdr(false) attaches GAMMA22/BT.709. Returns 0 on success, -1 on failure or pre-realize.
//  - CurrentOutputIsHdr is whatever the underlying surface believes about the output it's currently entered (null = unknown).
//  - CurrentOutputHdrChanged fires on transitions of CurrentOutputIsHdr (deduped against the last published value by the implementation).
public interface IHdrSink
{
    int SetHdr(bool enable);
    bool? CurrentOutputIsHdr { get; }
    event Action CurrentOutputHdrChanged;
}
