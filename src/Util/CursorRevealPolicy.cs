namespace Vomplayer.Util;

// Decides whether a fullscreen pointer-motion event is significant enough to wake the UI. Visible cursor: filter only sub-pixel jitter (deadZonePx) so the auto-hide timer re-arms on any real motion. Hidden cursor: require a larger committed movement (revealThresholdPx) before un-hiding, so an accidental bump doesn't bring the cursor and controls back. dx/dy are the displacement from the caller's reference point (while hidden, the pointer's resting position when the cursor hid); distance is compared in squared form to avoid a sqrt.
internal static class CursorRevealPolicy
{
    internal static bool IsSignificantMotion(double dx, double dy, bool cursorHidden, double deadZonePx, double revealThresholdPx)
    {
        double threshold = cursorHidden ? revealThresholdPx : deadZonePx;
        return dx * dx + dy * dy >= threshold * threshold;
    }
}
