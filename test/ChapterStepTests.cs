using System;
using System.Collections.Generic;
using Vomplayer.Playback;
using Vomplayer.Util;

namespace Vomplayer.Tests;

[TestFixture]
public class ChapterStepTests
{
    private static IReadOnlyList<MediaChapter> Build(params double[] times)
    {
        var list = new List<MediaChapter>();
        for (int i = 0; i < times.Length; i++)
        {
            list.Add(new MediaChapter(i, $"ch{i}", times[i]));
        }
        return list;
    }

    // ---- Landing ----

    [Test]
    public void LandingSubtractsPrerollWhenItFits()
    {
        // floor (= previous cue, 100) is below cue - preroll (190), so the full preroll applies.
        Assert.That(ChapterStep.Landing(Build(0, 100, 200), 2, 10), Is.EqualTo(190));
    }

    [Test]
    public void LandingFloorsAtPreviousCueWhenChaptersTighterThanPreroll()
    {
        // cue 105 - preroll 10 = 95, but the previous cue is 100 — floor wins, preroll shortened.
        Assert.That(ChapterStep.Landing(Build(0, 100, 105), 2, 10), Is.EqualTo(100));
    }

    [Test]
    public void LandingFloorsAtZeroForFirstChapter()
    {
        Assert.That(ChapterStep.Landing(Build(5, 100), 0, 10), Is.EqualTo(0));
        Assert.That(ChapterStep.Landing(Build(5, 100), 0, 2), Is.EqualTo(3));
    }

    [Test]
    public void LandingWithZeroPrerollIsTheCue()
    {
        Assert.That(ChapterStep.Landing(Build(0, 100, 200), 1, 0), Is.EqualTo(100));
        Assert.That(ChapterStep.Landing(Build(0, 100, 200), 2, 0), Is.EqualTo(200));
    }

    // ---- LandingForCue (marker clicks) agrees with Landing(index) ----

    [Test]
    public void LandingForCueMatchesIndexLanding()
    {
        var chapters = Build(0, 100, 105);
        Assert.That(ChapterStep.LandingForCue(chapters, 200, 10), Is.EqualTo(ChapterStep.Landing(Build(0, 100, 200), 2, 10)));
        // Floored case: clicking the 105 cue lands at 100 (previous cue), same as the keyboard step would.
        Assert.That(ChapterStep.LandingForCue(chapters, 105, 10), Is.EqualTo(ChapterStep.Landing(chapters, 2, 10)));
        Assert.That(ChapterStep.LandingForCue(chapters, 105, 10), Is.EqualTo(100));
    }

    [Test]
    public void LandingForCueClampsFirstChapterAtZero()
    {
        Assert.That(ChapterStep.LandingForCue(Build(5, 100), 5, 10), Is.EqualTo(0));
    }

    // ---- ResolveTarget: empty / nowhere-to-go ----

    [Test]
    public void ResolveTargetEmptyChaptersIsNull()
    {
        Assert.That(ChapterStep.ResolveTarget(Array.Empty<MediaChapter>(), 0, 1, 0), Is.Null);
    }

    [Test]
    public void ResolveTargetNextPastLastIsNull()
    {
        // At chapter 1's landing (90), next has nowhere to go.
        Assert.That(ChapterStep.ResolveTarget(Build(0, 100), 90, 1, 10), Is.Null);
    }

    [Test]
    public void ResolveTargetPrevBeforeFirstChapterIsNull()
    {
        // Position 50 is before the first chapter's landing (Landing(0)=90); previous goes nowhere.
        Assert.That(ChapterStep.ResolveTarget(Build(100, 200), 50, -1, 10), Is.Null);
        // ...but next lands on the first chapter.
        Assert.That(ChapterStep.ResolveTarget(Build(100, 200), 50, 1, 10), Is.EqualTo(90));
    }

    [Test]
    public void ResolveTargetPrevFromFirstChapterRecuesItsLanding()
    {
        // current == 0 stepping back: re-cue the first chapter's landing (0 here), not null.
        Assert.That(ChapterStep.ResolveTarget(Build(0, 100), 50, -1, 10), Is.EqualTo(0));
    }

    // ---- ResolveTarget: monotonic happy path (wide spacing, floor never triggers) ----

    [Test]
    public void ResolveTargetSteppingIsMonotonicWithWideSpacing()
    {
        var chapters = Build(0, 100, 200, 300); // Landings = [0, 90, 190, 290]
        Assert.That(ChapterStep.ResolveTarget(chapters, 90, 1, 10), Is.EqualTo(190));
        Assert.That(ChapterStep.ResolveTarget(chapters, 190, -1, 10), Is.EqualTo(90));
    }

    [Test]
    public void ResolveTargetTreatsPrerollWindowAsTheUpcomingChapter()
    {
        // Position 195 is within the preroll window before cue 200 → "current" is chapter 2, so Next advances
        // to chapter 3 (290), NOT back to chapter 2 (190). A position-derived current would oscillate here.
        var chapters = Build(0, 100, 200, 300); // Landings = [0, 90, 190, 290]
        Assert.That(ChapterStep.ResolveTarget(chapters, 195, 1, 10), Is.EqualTo(290));
        Assert.That(ChapterStep.ResolveTarget(chapters, 195, -1, 10), Is.EqualTo(90));
    }

    [Test]
    public void ResolveTargetFirstChapterNotAtTimeZero()
    {
        var chapters = Build(20, 120); // Landings = [15, 115]
        Assert.That(ChapterStep.ResolveTarget(chapters, 0, 1, 5), Is.EqualTo(15));
        Assert.That(ChapterStep.ResolveTarget(chapters, 0, -1, 5), Is.Null);
    }

    // ---- ResolveTarget: the B1 regression cases (these break the naive cue-minus-preroll design) ----

    [Test]
    public void RepeatedPreviousConvergesWhenEarlyCuesAreWithinPreroll()
    {
        // cues [0,1,2,50], preroll 5 → Landings [0,0,1,45]. The naive design (current = largest cue-preroll<=pos)
        // would jump 45→0, skip the landing at 1, and then stick. The floored, landing-based design steps
        // 45 → 1 → 0 → 0: monotonically non-increasing, visits the intermediate landing, never sticks/skips.
        var chapters = Build(0, 1, 2, 50);
        Assert.That(ChapterStep.ResolveTarget(chapters, 45, -1, 5), Is.EqualTo(1));
        Assert.That(ChapterStep.ResolveTarget(chapters, 1, -1, 5), Is.EqualTo(0));
        Assert.That(ChapterStep.ResolveTarget(chapters, 0, -1, 5), Is.EqualTo(0));
    }

    [Test]
    public void RepeatedNextStrictlyAdvancesWithTightSpacing()
    {
        // cues [0,2,4,6], preroll 5 → Landings [0,0,2,4]. Next steps 0 → 2 → 4 → null: strictly advances,
        // terminates past the last, never gets stuck.
        var chapters = Build(0, 2, 4, 6);
        Assert.That(ChapterStep.ResolveTarget(chapters, 0, 1, 5), Is.EqualTo(2));
        Assert.That(ChapterStep.ResolveTarget(chapters, 2, 1, 5), Is.EqualTo(4));
        Assert.That(ChapterStep.ResolveTarget(chapters, 4, 1, 5), Is.Null);
    }

    // ---- ResolveTarget: preroll == 0 reproduces exact-cue stepping ----

    [Test]
    public void ResolveTargetZeroPrerollLandsExactlyOnCues()
    {
        var chapters = Build(0, 100, 200);
        Assert.That(ChapterStep.ResolveTarget(chapters, 100, 1, 0), Is.EqualTo(200));
        Assert.That(ChapterStep.ResolveTarget(chapters, 100, -1, 0), Is.EqualTo(0));
    }

    // A `seek absolute+exact` to a cue lands on the frame at-or-just-below it, so the position reported back
    // sits a sub-frame *below* the seek target. Without a tolerance, the next "next chapter" recomputes the
    // same chapter (position < cue) and the user is stuck — visible only while paused (playback otherwise
    // advances past the cue). Intermittent in practice: cues on a frame boundary land exactly, others a hair below.

    [Test]
    public void ResolveTargetToleratesSubFrameLandingBelowTheCue()
    {
        // Stepped to chapter 1 (cue 60) and echoed back at 59.96 (~one frame at 25fps); next must reach chapter 2 (120), not re-cue 60.
        var chapters = Build(0, 60, 120);
        Assert.That(ChapterStep.ResolveTarget(chapters, 59.96, 1, 0), Is.EqualTo(120));
    }

    [Test]
    public void ResolveTargetToleratesSubFrameLandingBelowPrerolledTarget()
    {
        // preroll 5 → landings [0, 55, 115]. Stepped to chapter 1 (landing 55), echoed at 54.96; next must reach 115, not re-cue 55.
        var chapters = Build(0, 60, 120);
        Assert.That(ChapterStep.ResolveTarget(chapters, 54.96, 1, 5), Is.EqualTo(115));
    }

    [Test]
    public void ResolveTargetToleranceDoesNotSkipFromMidChapter()
    {
        // A position clearly inside chapter 0 (well below the tolerance band) must still step to chapter 1, not skip it.
        var chapters = Build(0, 60, 120);
        Assert.That(ChapterStep.ResolveTarget(chapters, 50, 1, 0), Is.EqualTo(60));
    }
}
