using System;
using Vomplayer.Wayland;

namespace Vomplayer.Controls;

// One video rendering slot, abstracting over the two runtime-selected render paths (Wayland subsurface vs Gtk.GLArea — see ARCHITECTURE.md "Rendering paths"). Before this seam, every consumer carried the nullable triple (VideoArea?, VideoView?, VideoSurface?) and re-derived the path with ??-chains; each dual-path site was a divergence waiting to happen (and several GLArea-path bugs came from exactly that). Consumers hold one non-null IVideoHost; the few genuinely Wayland-only capabilities (subsurface stacking, HDR/VRR sinks, presentation-feedback diagnostics) are reached through the single nullable WaylandSurface instead of three parallel fields.
//
// Implementations: VideoHostWayland (VideoArea placeholder + VideoSurface subsurface) and VideoHostGlArea (VideoView). Both are constructed by the path decision in MainWindow / PipController and are otherwise interchangeable.
public interface IVideoHost
{
    // The GTK widget that reserves layout space for the video (and receives input/CSS). Parent this into the widget tree.
    Gtk.Widget Widget { get; }

    // Wayland-only capabilities (PlaceAbove/PlaceAboveParent stacking, IHdrSink/IVrrSink, VRR diagnostics). Null on the GLArea path, and null after TeardownRenderSurface.
    VideoSurface? WaylandSurface { get; }

    // Fires on every successful render-context creation (can re-fire across unrealize/realize cycles — downstream one-shot latches live in the VM).
    event Action? RenderContextReady;

    event Action<int>? RenderFailed;

    // First mpv frame visibly rendered. Never fires on the GLArea path (no post-swap hook, and nothing there needs it — the GLArea draws into its own widget with no transparency gap to unmask).
    event Action? FirstFrameRendered;

    // The video widget's allocation changed (resize/reflow). Argument-free — consumers re-read current allocations themselves.
    event Action? GeometryChanged;

    // One-shot: route the Playback's render-surface attach to this host's widget. Must be called before the widget is first realized.
    void AttachPlayback(Playback.Playback playback);

    // Re-sync host-side geometry after a position-only change (an ancestor margin moved the widget without resizing it — GTK doesn't signal that). No-op on the GLArea path, where the FBO is drawn at the widget's actual position by GTK itself.
    void RefreshGeometry();

    // Free the mpv render context (Wayland: dispose the whole surface) — MUST run before the owning Playback destroys its mpv core; see MpvDispatcher.Dispose's ordering contract. Idempotent.
    void TeardownRenderSurface();
}
