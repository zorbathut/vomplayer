using System;
using System.Collections.Generic;
using Vomplayer.Playback;

namespace Vomplayer.Util;

// Pure resolver for chapter-seek landing times with a configurable "preroll" — chapter seeks land `prerollSeconds` before the cue so playback starts slightly ahead of the cue point. preroll == 0 reduces to landing exactly on the cue, so this is the single algorithm for all preroll values (no special-casing of 0). Assumes chapters are in non-decreasing TimeSeconds order, which is how mpv's chapter-list arrives.
//
// The subtle part is relative (next/previous) stepping. A position computed as cue - preroll sits physically inside the *previous* chapter, so a position-derived "current chapter" would re-target the same chapter on the next press and the user would oscillate at one boundary. Two coupled rules prevent that:
//
//   1. Floored landing: Landing(K) = max(floor(K), cue[K] - preroll), floor(K) = (K==0 ? 0 : cue[K-1]). Each chapter's landing stays inside its own [floor(K), cue[K]) band, so distinct chapters get distinct landings — the preroll is shortened (never spills past the previous chapter) when chapters are tighter than the preroll.
//   2. Landing-based current index: current(p) = largest K with Landing(K) <= p. Comparing against the floored landings (not the raw cues) makes stepping monotonic: forward always advances to a strictly greater landing, backward to a <= landing, so repeated next/previous converge instead of sticking.
//
// The one residual quirk is benign: two chapters that both want to start at ~0 (cue[0]=0 and cue[1] within preroll of 0) share a landing and can't be visited distinctly — unavoidable for any stateless position->chapter mapping, and only in pathological tight-near-zero spacing.
internal static class ChapterStep
{
    // A `seek absolute+exact` lands on the frame at-or-just-below the requested time, so the position mpv reports back sits up to ~one frame *below* the seek target. CurrentIndex treats a position within this tolerance below a landing as having reached it — otherwise a paused next/previous would recompute the same chapter (position < cue) and appear stuck (the bug doesn't show while playing because playback advances past the cue before the next press). 0.1s comfortably exceeds a video frame at any normal frame rate while staying far below any real chapter gap.
    private const double LandingToleranceSeconds = 0.1;

    // Floored landing time for the chapter at `index`. Caller guarantees 0 <= index < chapters.Count.
    internal static double Landing(IReadOnlyList<MediaChapter> chapters, int index, double prerollSeconds)
    {
        double floor = index == 0 ? 0.0 : chapters[index - 1].TimeSeconds;
        double landed = chapters[index].TimeSeconds - prerollSeconds;
        return Math.Max(floor, landed);
    }

    // Floored landing for an absolute cue time (chapter-marker clicks). The floor is the largest cue strictly less than cueSeconds (else 0), which equals Landing(K)'s floor for the chapter at cueSeconds — so a click and a keyboard step that reach the same chapter land identically.
    internal static double LandingForCue(IReadOnlyList<MediaChapter> chapters, double cueSeconds, double prerollSeconds)
    {
        double floor = 0.0;
        for (int i = 0; i < chapters.Count; i++)
        {
            double t = chapters[i].TimeSeconds;
            if (t < cueSeconds && t > floor)
            {
                floor = t;
            }
        }
        double landed = cueSeconds - prerollSeconds;
        return Math.Max(floor, landed);
    }

    // Absolute seek target for a relative chapter step (delta is ±1), or null when there's nowhere to go: no chapters; "next" past the last; "previous" before the first.
    internal static double? ResolveTarget(IReadOnlyList<MediaChapter> chapters, double positionSeconds, int delta, double prerollSeconds)
    {
        if (chapters.Count == 0)
        {
            return null;
        }
        int current = CurrentIndex(chapters, positionSeconds, prerollSeconds);
        int target = current + delta;
        if (target < 0)
        {
            if (current < 0)
            {
                // Position is before the first chapter's landing and the step goes earlier still — nothing there; do not jump forward.
                return null;
            }
            // current == 0 stepping back: re-cue the first chapter's landing (there's no earlier chapter).
            target = 0;
        }
        else if (target >= chapters.Count)
        {
            // Past the last chapter — nothing later.
            return null;
        }
        return Landing(chapters, target, prerollSeconds);
    }

    // Largest index K with Landing(K) <= positionSeconds (within LandingToleranceSeconds — see its comment), or -1 when the position is before the first chapter's landing. The scan deliberately does NOT early-break: floored landings are non-decreasing but can be *equal* (tight-near-zero spacing collapses adjacent landings), and on such a plateau the largest qualifying index is the one we want — an early break would stop at the first of the equal landings instead.
    private static int CurrentIndex(IReadOnlyList<MediaChapter> chapters, double positionSeconds, double prerollSeconds)
    {
        int found = -1;
        for (int i = 0; i < chapters.Count; i++)
        {
            if (Landing(chapters, i, prerollSeconds) <= positionSeconds + LandingToleranceSeconds)
            {
                found = i;
            }
        }
        return found;
    }
}
