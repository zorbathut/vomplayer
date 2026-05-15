using System;
using System.Collections.Generic;
using System.Linq;
using Vomplayer.UserData;
using Vomplayer.Util;

namespace Vomplayer.Tests;

[TestFixture]
public class PlaylistMenuSelectorTests
{
    // A single-stream entry built from one path, with a unique GUID and the supplied tick value as last_used_at.
    private static SavedPlaylistEntry E(string path, long ticks)
    {
        var stream = new SavedPlaylistStream(0, 0, new[] { path });
        return new SavedPlaylistEntry(
            Guid.NewGuid(),
            path,
            new DateTimeOffset(ticks, TimeSpan.Zero),
            1,
            new[] { stream });
    }

    // Build a list of entries from path strings. Treats the supplied order as "most-recent-first" (which is also SavedPlaylists.GetMostRecent's contract), so the timestamps are derived as a strictly-decreasing series — the absolute values don't matter for the selection logic, only the order does.
    private static IReadOnlyList<SavedPlaylistEntry> Order(params string[] paths)
    {
        long t = paths.Length;
        var list = new List<SavedPlaylistEntry>(paths.Length);
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
        var result = PlaylistMenuSelector.Select(Array.Empty<SavedPlaylistEntry>(), 10, 5);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void FewerThanTotalSlotsReturnsAllSortedByLastUsedDesc()
    {
        var input = Order("/dirA/a.mp4", "/dirB/b.mp4", "/dirC/c.mp4");
        var result = PlaylistMenuSelector.Select(input, 10, 5);
        Assert.That(result.Select(r => r.Streams[0].Items[0]), Is.EqualTo(new[] { "/dirA/a.mp4", "/dirB/b.mp4", "/dirC/c.mp4" }));
    }

    [Test]
    public void DistinctDirectoriesBehaveLikeTopN()
    {
        var input = Order(
            "/d01/f.mp4", "/d02/f.mp4", "/d03/f.mp4", "/d04/f.mp4", "/d05/f.mp4",
            "/d06/f.mp4", "/d07/f.mp4", "/d08/f.mp4", "/d09/f.mp4", "/d10/f.mp4",
            "/d11/f.mp4", "/d12/f.mp4");
        var result = PlaylistMenuSelector.Select(input, 10, 5);
        Assert.That(result.Count, Is.EqualTo(10));
        var paths = result.Select(r => r.Streams[0].Items[0]).ToArray();
        Assert.That(paths, Is.EqualTo(new[] {
            "/d01/f.mp4", "/d02/f.mp4", "/d03/f.mp4", "/d04/f.mp4", "/d05/f.mp4",
            "/d06/f.mp4", "/d07/f.mp4", "/d08/f.mp4", "/d09/f.mp4", "/d10/f.mp4",
        }));
    }

    [Test]
    public void DirectoryCoverageReachesIntoOlderEntries()
    {
        var input = Order(
            "/dirA/a1.mp4", "/dirA/a2.mp4", "/dirA/a3.mp4", "/dirA/a4.mp4", "/dirA/a5.mp4",
            "/dirA/a6.mp4", "/dirA/a7.mp4", "/dirA/a8.mp4", "/dirA/a9.mp4",
            "/dirB/b1.mp4");
        var result = PlaylistMenuSelector.Select(input, 10, 5);
        var paths = result.Select(r => r.Streams[0].Items[0]).ToArray();
        Assert.That(paths.Length, Is.EqualTo(10));
        Assert.That(paths[0], Is.EqualTo("/dirA/a1.mp4"));
        Assert.That(paths[9], Is.EqualTo("/dirB/b1.mp4"));
    }

    [Test]
    public void DirectoryCoverageReachesPastFillBoundary()
    {
        var paths = new List<string>();
        for (int i = 1; i <= 11; i++)
        {
            paths.Add($"/dirHot/h{i:00}.mp4");
        }
        paths.Add("/dirOld/o.mp4");
        var input = Order(paths.ToArray());

        var result = PlaylistMenuSelector.Select(input, 10, 5);
        var resultPaths = result.Select(r => r.Streams[0].Items[0]).ToArray();
        Assert.That(resultPaths.Length, Is.EqualTo(10));
        Assert.That(resultPaths.Contains("/dirOld/o.mp4"), Is.True, "directory coverage must surface the only playlist from /dirOld");
        Assert.That(resultPaths.Contains("/dirHot/h01.mp4"), Is.True, "most-recent /dirHot playlist must also be present");
        Assert.That(resultPaths.Contains("/dirHot/h11.mp4"), Is.False, "least-recent /dirHot playlist is bumped by coverage rule");
    }

    [Test]
    public void NoDuplicatesEvenWhenInputRepeatsAGuid()
    {
        // Defensive: GetMostRecent dedupes by GUID PK, but the selector is contract-robust.
        var sharedGuid = Guid.NewGuid();
        var stream = new SavedPlaylistStream(0, 0, new[] { "/dirA/a.mp4" });
        var input = new[]
        {
            new SavedPlaylistEntry(sharedGuid, "a", new DateTimeOffset(100, TimeSpan.Zero), 1, new[] { stream }),
            new SavedPlaylistEntry(sharedGuid, "a", new DateTimeOffset(99, TimeSpan.Zero), 1, new[] { stream }),
            E("/dirB/b.mp4", 98),
        };
        var result = PlaylistMenuSelector.Select(input, 10, 5);
        Assert.That(result.Count, Is.EqualTo(2));
        Assert.That(result.Select(r => r.Guid).Distinct().Count(), Is.EqualTo(2));
    }

    [Test]
    public void SameFileWithDistinctGuidsDedupesToOneSlot()
    {
        // PlaylistAutosave mints a fresh GUID on every Open-replace, so opening /dirA/a.mp4 ten times yields ten rows with the same path. The selector must collapse them so a single hot file doesn't bury other directories.
        var input = Order(
            "/dirA/a.mp4", "/dirA/a.mp4", "/dirA/a.mp4", "/dirA/a.mp4", "/dirA/a.mp4",
            "/dirA/a.mp4", "/dirA/a.mp4", "/dirA/a.mp4", "/dirA/a.mp4", "/dirA/a.mp4",
            "/dirB/b.mp4");
        var result = PlaylistMenuSelector.Select(input, 10, 5);
        var paths = result.Select(r => r.Streams[0].Items[0]).ToArray();
        Assert.That(paths, Is.EqualTo(new[] { "/dirA/a.mp4", "/dirB/b.mp4" }));
    }

    [Test]
    public void FiveDirectoriesAreSurfacedDespiteRepeatOpensOfSameFile()
    {
        // Stress case for the directory-coverage guarantee: /dirA's single file is opened 20 times (20 fresh GUIDs, identical path) and four other directories each have one entry, all interleaved at the front of the history. Without file-level dedup, Phase 2's fill flood every remaining slot with /dirA repeats and only three of the four other directories make the cut.
        var paths = new List<string>();
        for (int i = 0; i < 20; i++)
        {
            paths.Add("/dirA/a.mp4");
        }
        paths.Add("/dirB/b.mp4");
        paths.Add("/dirC/c.mp4");
        paths.Add("/dirD/d.mp4");
        paths.Add("/dirE/e.mp4");
        var input = Order(paths.ToArray());

        var result = PlaylistMenuSelector.Select(input, 10, 5);
        var distinctDirs = result
            .Select(r => System.IO.Path.GetDirectoryName(r.Streams[0].Items[0]))
            .Distinct()
            .ToArray();
        Assert.That(distinctDirs.Length, Is.EqualTo(5));
    }

    [Test]
    public void OtherDirectoriesSurfaceBehindTwentyDistinctFilesInOneDirectory()
    {
        // The directory-coverage guarantee under a binge-watch shape: twenty *different* files (a01..a20) all in /dirA were watched most recently, then one file each in /dirB through /dirE. Phase 1 must reach past the dirA wall to surface all four other directories; with directoryCoverageSlots=5 the result is "first dirA entry + one each from B/C/D/E + fill from older dirA".
        var paths = new List<string>();
        for (int i = 1; i <= 20; i++)
        {
            paths.Add($"/dirA/a{i:00}.mp4");
        }
        paths.Add("/dirB/b.mp4");
        paths.Add("/dirC/c.mp4");
        paths.Add("/dirD/d.mp4");
        paths.Add("/dirE/e.mp4");
        var input = Order(paths.ToArray());

        var result = PlaylistMenuSelector.Select(input, 10, 5);
        var resultPaths = result.Select(r => r.Streams[0].Items[0]).ToArray();
        Assert.That(resultPaths.Length, Is.EqualTo(10));
        var distinctDirs = resultPaths.Select(p => System.IO.Path.GetDirectoryName(p)).Distinct().ToArray();
        Assert.That(distinctDirs.Length, Is.EqualTo(5), "all five directories must be represented");
        Assert.That(resultPaths.Contains("/dirB/b.mp4"), Is.True);
        Assert.That(resultPaths.Contains("/dirC/c.mp4"), Is.True);
        Assert.That(resultPaths.Contains("/dirD/d.mp4"), Is.True);
        Assert.That(resultPaths.Contains("/dirE/e.mp4"), Is.True);
        Assert.That(resultPaths.Contains("/dirA/a01.mp4"), Is.True, "most-recent /dirA entry must appear");
    }

    [Test]
    public void RepeatedUriCollapsesInFill()
    {
        // Same dedup rule applies to URI-only entries — five replays of the same YouTube URL take one slot, not five.
        const string url = "https://www.youtube.com/watch?v=abcdef";
        var input = Order(url, url, url, url, url, "/dirA/a.mp4");
        var result = PlaylistMenuSelector.Select(input, 10, 5);
        var paths = result.Select(r => r.Streams[0].Items[0]).ToArray();
        Assert.That(paths.Count(p => p == url), Is.EqualTo(1));
        Assert.That(paths.Contains("/dirA/a.mp4"), Is.True);
    }

    [Test]
    public void UrisDoNotConsumeDirectorySlotsButAppearInFill()
    {
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

        var result = PlaylistMenuSelector.Select(input, 10, 5);
        var paths = result.Select(r => r.Streams[0].Items[0]).ToArray();
        Assert.That(paths.Length, Is.EqualTo(10));
        Assert.That(paths.Contains("https://www.youtube.com/watch?v=abcdef"), Is.True);
        Assert.That(paths.Contains("/dirA/a.mp4"), Is.True);
        Assert.That(paths.Contains("/dirB/b.mp4"), Is.True);
        Assert.That(paths.Contains("/dirC/c.mp4"), Is.True);
        Assert.That(paths.Contains("/dirD/d.mp4"), Is.True);
    }

    [Test]
    public void ResultIsSortedChronologicallyDescending()
    {
        var input = new[]
        {
            E("/dirA/newest.mp4", 100),
            E("/dirA/middle.mp4", 50),
            E("/dirB/oldest.mp4", 10),
        };
        var result = PlaylistMenuSelector.Select(input, 10, 5);
        var paths = result.Select(r => r.Streams[0].Items[0]).ToArray();
        Assert.That(paths, Is.EqualTo(new[] { "/dirA/newest.mp4", "/dirA/middle.mp4", "/dirB/oldest.mp4" }));
    }

    [Test]
    public void ZeroTotalSlotsReturnsEmpty()
    {
        var input = Order("/a/x.mp4", "/b/y.mp4");
        Assert.That(PlaylistMenuSelector.Select(input, 0, 0), Is.Empty);
    }

    [Test]
    public void DirectoryCoverageZeroFallsBackToPureMostRecent()
    {
        var input = Order("/dirA/a1.mp4", "/dirA/a2.mp4", "/dirB/b.mp4");
        var result = PlaylistMenuSelector.Select(input, 2, 0);
        Assert.That(result.Select(r => r.Streams[0].Items[0]), Is.EqualTo(new[] { "/dirA/a1.mp4", "/dirA/a2.mp4" }));
    }

    [Test]
    public void DirectoryCoverageEqualToTotalSlotsStillFillsRemainingFromAlreadyChosen()
    {
        var input = Order("/dirA/a.mp4", "/dirB/b.mp4");
        var result = PlaylistMenuSelector.Select(input, 5, 5);
        Assert.That(result.Select(r => r.Streams[0].Items[0]), Is.EqualTo(new[] { "/dirA/a.mp4", "/dirB/b.mp4" }));
    }

    [Test]
    public void DirectoryCoverageGreaterThanTotalIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PlaylistMenuSelector.Select(Array.Empty<SavedPlaylistEntry>(), 5, 6));
    }

    [Test]
    public void NegativeArgsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PlaylistMenuSelector.Select(Array.Empty<SavedPlaylistEntry>(), -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlaylistMenuSelector.Select(Array.Empty<SavedPlaylistEntry>(), 5, -1));
    }

    [Test]
    public void DirectoryKeyUsesCurrentItemForMultiFilePlaylist()
    {
        // Multi-file playlist: the selector's directory-coverage key uses slot 0's currently-selected item, not the first item. So a 3-item playlist with current=1 belonging to /dirB groups under dirB even though the first item is in dirA.
        var stream = new SavedPlaylistStream(0, 1, new[] { "/dirA/a.mp4", "/dirB/b.mp4", "/dirC/c.mp4" });
        var entry = new SavedPlaylistEntry(
            Guid.NewGuid(),
            "multi",
            new DateTimeOffset(100, TimeSpan.Zero),
            1,
            new[] { stream });
        var key = PlaylistMenuSelector.DirectoryKeyOf(entry);
        Assert.That(key, Is.EqualTo("/dirB"));
    }

    [Test]
    public void DirectoryKeyForMultiStreamUsesSlotZero()
    {
        // Multi-stream entry: directory key still derives from slot 0's current item, ignoring slot 1. Keeps the heuristic predictable.
        var slot0 = new SavedPlaylistStream(0, 0, new[] { "/dirA/a.mp4" });
        var slot1 = new SavedPlaylistStream(1, 0, new[] { "/dirB/b.mp4" });
        var entry = new SavedPlaylistEntry(
            Guid.NewGuid(),
            "pip",
            new DateTimeOffset(100, TimeSpan.Zero),
            2,
            new[] { slot0, slot1 });
        var key = PlaylistMenuSelector.DirectoryKeyOf(entry);
        Assert.That(key, Is.EqualTo("/dirA"));
    }
}
