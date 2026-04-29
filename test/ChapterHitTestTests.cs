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
    public void DirectHitReturnsTime()
    {
        var chapters = Build(25, 50, 75);
        // x = 60 maps to time 50.
        var t = ChapterHitTest.NearestTimeSeconds(60, TroughLeft, TroughWidth, Duration, chapters, Tol);
        Assert.That(t, Is.EqualTo(50));
    }

    [Test]
    public void WithinToleranceReturnsNearest()
    {
        var chapters = Build(25, 50, 75);
        // x = 56 → 4 px from time 50's marker (x=60). Within tol.
        var t = ChapterHitTest.NearestTimeSeconds(56, TroughLeft, TroughWidth, Duration, chapters, Tol);
        Assert.That(t, Is.EqualTo(50));
    }

    [Test]
    public void OutsideToleranceReturnsNull()
    {
        var chapters = Build(25, 50, 75);
        // x = 50 → 10 px from time 50's marker (x=60). Outside tol.
        var t = ChapterHitTest.NearestTimeSeconds(50, TroughLeft, TroughWidth, Duration, chapters, Tol);
        Assert.That(t, Is.Null);
    }

    [Test]
    public void TwoChaptersBothInRangeReturnsCloser()
    {
        // Chapters at t=50 (x=60) and t=55 (x=65). Click at x=63 → 3 px from 60, 2 px from 65 → returns 55.
        var chapters = Build(50, 55);
        var t = ChapterHitTest.NearestTimeSeconds(63, TroughLeft, TroughWidth, Duration, chapters, Tol);
        Assert.That(t, Is.EqualTo(55));
    }

    [Test]
    public void DurationZeroReturnsNull()
    {
        var chapters = Build(25, 50);
        var t = ChapterHitTest.NearestTimeSeconds(60, TroughLeft, TroughWidth, 0, chapters, Tol);
        Assert.That(t, Is.Null);
    }

    [Test]
    public void DurationNegativeReturnsNull()
    {
        var chapters = Build(25, 50);
        var t = ChapterHitTest.NearestTimeSeconds(60, TroughLeft, TroughWidth, -1, chapters, Tol);
        Assert.That(t, Is.Null);
    }

    [Test]
    public void TroughWidthZeroReturnsNull()
    {
        var chapters = Build(25, 50);
        var t = ChapterHitTest.NearestTimeSeconds(60, TroughLeft, 0, Duration, chapters, Tol);
        Assert.That(t, Is.Null);
    }

    [Test]
    public void EmptyChapterListReturnsNull()
    {
        var t = ChapterHitTest.NearestTimeSeconds(60, TroughLeft, TroughWidth, Duration, Array.Empty<MediaChapter>(), Tol);
        Assert.That(t, Is.Null);
    }

    [Test]
    public void ChapterAtZeroIgnored()
    {
        var chapters = Build(0, 50);
        // Click x=10 maps to time=0; that chapter must be ignored. No other chapter is in range.
        var t = ChapterHitTest.NearestTimeSeconds(10, TroughLeft, TroughWidth, Duration, chapters, Tol);
        Assert.That(t, Is.Null);
    }

    [Test]
    public void ChapterAtDurationIgnored()
    {
        var chapters = Build(50, 100);
        // Click x=110 maps to time=100; that chapter must be ignored. The other chapter (50/x=60) is far away.
        var t = ChapterHitTest.NearestTimeSeconds(110, TroughLeft, TroughWidth, Duration, chapters, Tol);
        Assert.That(t, Is.Null);
    }

    [Test]
    public void ChapterPastDurationIgnored()
    {
        var chapters = Build(50, 200);
        var t = ChapterHitTest.NearestTimeSeconds(60, TroughLeft, TroughWidth, Duration, chapters, Tol);
        Assert.That(t, Is.EqualTo(50));
    }

    [Test]
    public void ToleranceBoundaryInclusive()
    {
        var chapters = Build(50);
        // x=66 is exactly Tol px from x=60. Should hit.
        var t = ChapterHitTest.NearestTimeSeconds(66, TroughLeft, TroughWidth, Duration, chapters, Tol);
        Assert.That(t, Is.EqualTo(50));
    }
}
