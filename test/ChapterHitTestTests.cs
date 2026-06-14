using System;
using System.Collections.Generic;
using Vomplayer.Playback;
using Vomplayer.Util;

namespace Vomplayer.Tests;

[TestFixture]
public class ChapterHitTestTests
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

    // Standard fixture: trough spans pixels [10, 110], duration 100s. So time=t → x = 10 + t.
    private const double TroughLeft = 10;
    private const double TroughWidth = 100;
    private const double Duration = 100;
    private const double Tol = 6;

    [Test]
    public void DirectHitReturnsChapter()
    {
        var chapters = Build(25, 50, 75);
        // x = 60 maps to time 50.
        var ch = ChapterHitTest.NearestChapter(60, TroughLeft, TroughWidth, Duration, chapters, Tol);
        Assert.That(ch?.TimeSeconds, Is.EqualTo(50));
    }

    [Test]
    public void ReturnsFullChapterRecord()
    {
        var chapters = Build(25, 50, 75);
        // x = 60 maps to the second chapter (index 1, title "ch1", time 50) — the whole record comes back, which the tooltip relies on for Title/Index.
        var ch = ChapterHitTest.NearestChapter(60, TroughLeft, TroughWidth, Duration, chapters, Tol);
        Assert.That(ch, Is.EqualTo(new MediaChapter(1, "ch1", 50)));
    }

    [Test]
    public void WithinToleranceReturnsNearest()
    {
        var chapters = Build(25, 50, 75);
        // x = 56 → 4 px from time 50's marker (x=60). Within tol.
        var ch = ChapterHitTest.NearestChapter(56, TroughLeft, TroughWidth, Duration, chapters, Tol);
        Assert.That(ch?.TimeSeconds, Is.EqualTo(50));
    }

    [Test]
    public void OutsideToleranceReturnsNull()
    {
        var chapters = Build(25, 50, 75);
        // x = 50 → 10 px from time 50's marker (x=60). Outside tol.
        var ch = ChapterHitTest.NearestChapter(50, TroughLeft, TroughWidth, Duration, chapters, Tol);
        Assert.That(ch, Is.Null);
    }

    [Test]
    public void TwoChaptersBothInRangeReturnsCloser()
    {
        // Chapters at t=50 (x=60) and t=55 (x=65). Click at x=63 → 3 px from 60, 2 px from 65 → returns 55.
        var chapters = Build(50, 55);
        var ch = ChapterHitTest.NearestChapter(63, TroughLeft, TroughWidth, Duration, chapters, Tol);
        Assert.That(ch?.TimeSeconds, Is.EqualTo(55));
    }

    [Test]
    public void DurationZeroReturnsNull()
    {
        var chapters = Build(25, 50);
        var ch = ChapterHitTest.NearestChapter(60, TroughLeft, TroughWidth, 0, chapters, Tol);
        Assert.That(ch, Is.Null);
    }

    [Test]
    public void DurationNegativeReturnsNull()
    {
        var chapters = Build(25, 50);
        var ch = ChapterHitTest.NearestChapter(60, TroughLeft, TroughWidth, -1, chapters, Tol);
        Assert.That(ch, Is.Null);
    }

    [Test]
    public void TroughWidthZeroReturnsNull()
    {
        var chapters = Build(25, 50);
        var ch = ChapterHitTest.NearestChapter(60, TroughLeft, 0, Duration, chapters, Tol);
        Assert.That(ch, Is.Null);
    }

    [Test]
    public void EmptyChapterListReturnsNull()
    {
        var ch = ChapterHitTest.NearestChapter(60, TroughLeft, TroughWidth, Duration, Array.Empty<MediaChapter>(), Tol);
        Assert.That(ch, Is.Null);
    }

    [Test]
    public void ChapterAtZeroIgnored()
    {
        var chapters = Build(0, 50);
        // Click x=10 maps to time=0; that chapter must be ignored. No other chapter is in range.
        var ch = ChapterHitTest.NearestChapter(10, TroughLeft, TroughWidth, Duration, chapters, Tol);
        Assert.That(ch, Is.Null);
    }

    [Test]
    public void ChapterAtDurationIgnored()
    {
        var chapters = Build(50, 100);
        // Click x=110 maps to time=100; that chapter must be ignored. The other chapter (50/x=60) is far away.
        var ch = ChapterHitTest.NearestChapter(110, TroughLeft, TroughWidth, Duration, chapters, Tol);
        Assert.That(ch, Is.Null);
    }

    [Test]
    public void ChapterPastDurationIgnored()
    {
        var chapters = Build(50, 200);
        var ch = ChapterHitTest.NearestChapter(60, TroughLeft, TroughWidth, Duration, chapters, Tol);
        Assert.That(ch?.TimeSeconds, Is.EqualTo(50));
    }

    [Test]
    public void ToleranceBoundaryInclusive()
    {
        var chapters = Build(50);
        // x=66 is exactly Tol px from x=60. Should hit.
        var ch = ChapterHitTest.NearestChapter(66, TroughLeft, TroughWidth, Duration, chapters, Tol);
        Assert.That(ch?.TimeSeconds, Is.EqualTo(50));
    }
}
