using System.Collections.Generic;
using Vomplayer.Playback;

namespace Vomplayer.Util;

// Pure hit-test for chapter-marker clicks. The visual layout maps a chapter at TimeSeconds=t to pixel x = troughLeftPx + (t/durationSeconds) * troughWidthPx, matching how Gtk.Scale.AddMark positions a tick (the trough-rect range, not the scale's full widget width — the slider thumb's reachable range is inset on both sides). Returns the chapter time whose pixel position is closest to clickX *and* within toleranceTpx; null if nothing's in range.
internal static class ChapterHitTest
{
    internal static double? NearestTimeSeconds(
        double clickX,
        double troughLeftPx,
        double troughWidthPx,
        double durationSeconds,
        IReadOnlyList<MediaChapter> chapters,
        double toleranceTpx)
    {
        if (durationSeconds <= 0)
        {
            return null;
        }
        if (troughWidthPx <= 0)
        {
            return null;
        }
        double bestDist = double.MaxValue;
        double? bestTime = null;
        for (int i = 0; i < chapters.Count; i++)
        {
            double t = chapters[i].TimeSeconds;
            // Skip chapters at file start / end: they're either redundant with "click on left edge" or unreachable seek targets at EOF.
            if (t <= 0 || t >= durationSeconds)
            {
                continue;
            }
            double markerX = troughLeftPx + (t / durationSeconds) * troughWidthPx;
            double dist = clickX - markerX;
            if (dist < 0)
            {
                dist = -dist;
            }
            if (dist <= toleranceTpx && dist < bestDist)
            {
                bestDist = dist;
                bestTime = t;
            }
        }
        return bestTime;
    }
}
