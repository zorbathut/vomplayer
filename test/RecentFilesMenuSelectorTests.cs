using System;
using System.Collections.Generic;
using System.Linq;
using Vomplayer.UserData;
using Vomplayer.Util;

namespace Vomplayer.Tests;

[TestFixture]
public class RecentFilesMenuSelectorTests
{
    private static RecentFileEntry E(string path, long ticks)
    {
        return new RecentFileEntry(path, new DateTimeOffset(ticks, TimeSpan.Zero), 1);
    }

    // Build a list of entries from (path, t) tuples. Treats the supplied order as "most-recent-first" (which is also RecentFiles.GetMostRecent's contract), so the timestamps are derived as a strictly-decreasing series — the absolute values don't matter for the selection logic, only the order does.
    private static IReadOnlyList<RecentFileEntry> Order(params string[] paths)
    {
        long t = paths.Length;
        var list = new List<RecentFileEntry>(paths.Length);
        foreach (var p in paths)
        {
            list.Add(E(p, t));
            t--;
        }
        return list;
    }

    [Test]
    public void EmptyInputReturnsEmpty()
    {
        var result = RecentFilesMenuSelector.Select(Array.Empty<RecentFileEntry>(), 10, 5);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void FewerThanTotalSlotsReturnsAllSortedByLastOpenedDesc()
    {
        var input = Order("/dirA/a.mp4", "/dirB/b.mp4", "/dirC/c.mp4");
        var result = RecentFilesMenuSelector.Select(input, 10, 5);
        Assert.That(result.Select(r => r.PathOrUri), Is.EqualTo(new[] { "/dirA/a.mp4", "/dirB/b.mp4", "/dirC/c.mp4" }));
    }

    [Test]
    public void DistinctDirectoriesBehaveLikeTopN()
    {
        // 12 files each in their own directory. Top-10 by recency = the first 10. Phase 1 picks 5 from distinct dirs, Phase 2 fills with the next 5; the merged result sorted is just the most-recent 10.
        var input = Order(
            "/d01/f.mp4", "/d02/f.mp4", "/d03/f.mp4", "/d04/f.mp4", "/d05/f.mp4",
            "/d06/f.mp4", "/d07/f.mp4", "/d08/f.mp4", "/d09/f.mp4", "/d10/f.mp4",
            "/d11/f.mp4", "/d12/f.mp4");
        var result = RecentFilesMenuSelector.Select(input, 10, 5);
        Assert.That(result.Count, Is.EqualTo(10));
        var paths = result.Select(r => r.PathOrUri).ToArray();
        Assert.That(paths, Is.EqualTo(new[] {
            "/d01/f.mp4", "/d02/f.mp4", "/d03/f.mp4", "/d04/f.mp4", "/d05/f.mp4",
            "/d06/f.mp4", "/d07/f.mp4", "/d08/f.mp4", "/d09/f.mp4", "/d10/f.mp4",
        }));
    }

    [Test]
    public void DirectoryCoverageReachesIntoOlderEntries()
    {
        // 9 files in /dirA, 1 file in /dirB (very old). Top-N by pure recency would be all 9 of dirA + 1 of dirB. Coverage rule says: the most recent of /dirA is the first, the most recent of /dirB is the only B file. Phase 1 picks (a1, b1) — wait, dirB is older than all dirA, so on input order /a1, /a2, /a3, ... /a9, /b1, the very first new-dir entry is /a1; the second new-dir is /b1. Phase 1 stops at 2 (no more new dirs) even though directoryCoverageSlots=5. Phase 2 fills with the 8 remaining /a2../a9 in order.
        var input = Order(
            "/dirA/a1.mp4", "/dirA/a2.mp4", "/dirA/a3.mp4", "/dirA/a4.mp4", "/dirA/a5.mp4",
            "/dirA/a6.mp4", "/dirA/a7.mp4", "/dirA/a8.mp4", "/dirA/a9.mp4",
            "/dirB/b1.mp4");
        var result = RecentFilesMenuSelector.Select(input, 10, 5);
        var paths = result.Select(r => r.PathOrUri).ToArray();
        // All 10 files survived; sort is by last_opened desc which matches input order here.
        Assert.That(paths.Length, Is.EqualTo(10));
        Assert.That(paths[0], Is.EqualTo("/dirA/a1.mp4"));
        Assert.That(paths[9], Is.EqualTo("/dirB/b1.mp4"));
    }

    [Test]
    public void DirectoryCoverageReachesPastFillBoundary()
    {
        // The whole point of the feature: an old directory has its most-recent file pulled in even if pure-recency wouldn't. /dirOld's only file is buried under 11 newer /dirHot entries; without the coverage rule it'd never appear. With directoryCoverageSlots=5, dirHot consumes one slot, dirOld consumes another. Phase 2 then fills with the next 8 dirHot entries.
        var paths = new List<string>();
        for (int i = 1; i <= 11; i++)
        {
            paths.Add($"/dirHot/h{i:00}.mp4");
        }
        paths.Add("/dirOld/o.mp4");
        var input = Order(paths.ToArray());

        var result = RecentFilesMenuSelector.Select(input, 10, 5);
        var resultPaths = result.Select(r => r.PathOrUri).ToArray();
        Assert.That(resultPaths.Length, Is.EqualTo(10));
        Assert.That(resultPaths.Contains("/dirOld/o.mp4"), Is.True, "directory coverage must surface the only file from /dirOld");
        Assert.That(resultPaths.Contains("/dirHot/h01.mp4"), Is.True, "most-recent /dirHot file must also be present");
        // /dirHot/h11 is the 11th-most-recent dirHot file; it loses its slot to dirOld.
        Assert.That(resultPaths.Contains("/dirHot/h11.mp4"), Is.False, "least-recent /dirHot file is bumped by coverage rule");
    }

    [Test]
    public void NoDuplicatesEvenWhenInputRepeatsAFile()
    {
        // Defensive: RecentFiles.GetMostRecent dedupes by upsert, but the selector is contract-robust.
        var input = new[]
        {
            E("/dirA/a.mp4", 100),
            E("/dirA/a.mp4", 99),
            E("/dirB/b.mp4", 98),
        };
        var result = RecentFilesMenuSelector.Select(input, 10, 5);
        Assert.That(result.Count, Is.EqualTo(2));
        Assert.That(result.Select(r => r.PathOrUri).Distinct().Count(), Is.EqualTo(2));
    }

    [Test]
    public void UrisDoNotConsumeDirectorySlotsButAppearInFill()
    {
        // 4 distinct local directories + 1 URI + several more files in the same hot directory. Phase 1 must NOT bind the URI to a coverage slot — it has no directory — so coverage uses the 4 local dirs only. Phase 2 fills with the URI and the rest of the hot files.
        var input = Order(
            "/dirA/a.mp4",
            "https://www.youtube.com/watch?v=abcdef",
            "/dirB/b.mp4",
            "/dirC/c.mp4",
            "/dirD/d.mp4",
            "/dirA/a2.mp4",
            "/dirA/a3.mp4",
            "/dirA/a4.mp4",
            "/dirA/a5.mp4",
            "/dirA/a6.mp4",
            "/dirA/a7.mp4");

        var result = RecentFilesMenuSelector.Select(input, 10, 5);
        var paths = result.Select(r => r.PathOrUri).ToArray();
        Assert.That(paths.Length, Is.EqualTo(10));
        Assert.That(paths.Contains("https://www.youtube.com/watch?v=abcdef"), Is.True);
        // All 4 local dirs covered.
        Assert.That(paths.Contains("/dirA/a.mp4"), Is.True);
        Assert.That(paths.Contains("/dirB/b.mp4"), Is.True);
        Assert.That(paths.Contains("/dirC/c.mp4"), Is.True);
        Assert.That(paths.Contains("/dirD/d.mp4"), Is.True);
    }

    [Test]
    public void ResultIsSortedChronologicallyDescending()
    {
        // Phase 1 may pick entries whose timestamps interleave with Phase 2's picks. The final sort must put them in last_opened desc order regardless.
        var input = new[]
        {
            E("/dirA/newest.mp4", 100),
            E("/dirA/middle.mp4", 50),
            E("/dirB/oldest.mp4", 10),
        };
        var result = RecentFilesMenuSelector.Select(input, 10, 5);
        var paths = result.Select(r => r.PathOrUri).ToArray();
        Assert.That(paths, Is.EqualTo(new[] { "/dirA/newest.mp4", "/dirA/middle.mp4", "/dirB/oldest.mp4" }));
    }

    [Test]
    public void ZeroTotalSlotsReturnsEmpty()
    {
        var input = Order("/a/x.mp4", "/b/y.mp4");
        Assert.That(RecentFilesMenuSelector.Select(input, 0, 0), Is.Empty);
    }

    [Test]
    public void DirectoryCoverageZeroFallsBackToPureMostRecent()
    {
        var input = Order("/dirA/a1.mp4", "/dirA/a2.mp4", "/dirB/b.mp4");
        var result = RecentFilesMenuSelector.Select(input, 2, 0);
        // Without coverage: top-2 by recency = a1, a2 (both from dirA).
        Assert.That(result.Select(r => r.PathOrUri), Is.EqualTo(new[] { "/dirA/a1.mp4", "/dirA/a2.mp4" }));
    }

    [Test]
    public void DirectoryCoverageEqualToTotalSlotsStillFillsRemainingFromAlreadyChosen()
    {
        // Edge case: coverage=total. If fewer distinct directories exist than coverage slots, the chosen set is short of total, but Phase 2 won't add anything (all candidate files are already chosen as their dir's most-recent).
        var input = Order("/dirA/a.mp4", "/dirB/b.mp4");
        var result = RecentFilesMenuSelector.Select(input, 5, 5);
        Assert.That(result.Select(r => r.PathOrUri), Is.EqualTo(new[] { "/dirA/a.mp4", "/dirB/b.mp4" }));
    }

    [Test]
    public void DirectoryCoverageGreaterThanTotalIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RecentFilesMenuSelector.Select(Array.Empty<RecentFileEntry>(), 5, 6));
    }

    [Test]
    public void NegativeArgsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RecentFilesMenuSelector.Select(Array.Empty<RecentFileEntry>(), -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecentFilesMenuSelector.Select(Array.Empty<RecentFileEntry>(), 5, -1));
    }
}
