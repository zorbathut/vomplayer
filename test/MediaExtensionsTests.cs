using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vomplayer.Util;

namespace Vomplayer.Tests;

[TestFixture]
public class MediaExtensionsTests
{
    [Test]
    public void IsVideoFileMatchesByExtensionCaseInsensitively()
    {
        Assert.That(MediaExtensions.IsVideoFile("/some/dir/video.mp4"), Is.True);
        Assert.That(MediaExtensions.IsVideoFile("/some/dir/video.MP4"), Is.True);
        Assert.That(MediaExtensions.IsVideoFile("/some/dir/Video.MkV"), Is.True);
        Assert.That(MediaExtensions.IsVideoFile("video.webm"), Is.True);
    }

    [Test]
    public void IsVideoFileRejectsExtensionlessAndUnknown()
    {
        Assert.That(MediaExtensions.IsVideoFile(""), Is.False);
        Assert.That(MediaExtensions.IsVideoFile("plainname"), Is.False);
        Assert.That(MediaExtensions.IsVideoFile("file."), Is.False);
        Assert.That(MediaExtensions.IsVideoFile("notes.txt"), Is.False);
        Assert.That(MediaExtensions.IsVideoFile("song.mp3"), Is.False);
        Assert.That(MediaExtensions.IsVideoFile("subs.srt"), Is.False);
    }

    [Test]
    public void IsVideoFileHandlesMultiDotAndHiddenPaths()
    {
        // Multi-dot filenames: only the trailing extension counts (Path.GetExtension contract).
        Assert.That(MediaExtensions.IsVideoFile("/dir/movie.part.1.mkv"), Is.True);
        Assert.That(MediaExtensions.IsVideoFile("/dir/movie.part.1.txt"), Is.False);
        // Dotfiles without an actual extension: Path.GetExtension treats ".bashrc" as having extension ".bashrc" — neither in nor out of the video list, but consistently rejected.
        Assert.That(MediaExtensions.IsVideoFile("/dir/.hidden"), Is.False);
        // Dotfile with a video extension after the dot: ".mkv" has extension ".mkv" — accepted by the same logic that handles "movie.mkv".
        Assert.That(MediaExtensions.IsVideoFile("/dir/.mkv"), Is.True);
    }

    [Test]
    public void ExpandPathsKeepsFilesAndUrisUnchanged()
    {
        // Direct file entries are NOT extension-filtered — the user's explicit drop is authoritative. URIs (any scheme://) skip the directory check entirely (Directory.Exists on a URI is always false anyway, but the test pins the intent).
        var errors = new List<string>();
        var result = MediaExtensions.ExpandPaths(new[] { "/tmp/movie.mkv", "/tmp/notes.txt", "https://example.com/stream.m3u8" }, errors.Add);
        Assert.That(result, Is.EqualTo(new[] { "/tmp/movie.mkv", "/tmp/notes.txt", "https://example.com/stream.m3u8" }));
        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void ExpandPathsRecurseDirectoryFiltersByExtension()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vompl-expand-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var sub = Path.Combine(dir, "sub");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(dir, "a.mkv"), "");
            File.WriteAllText(Path.Combine(dir, "b.txt"), "");
            File.WriteAllText(Path.Combine(sub, "c.mp4"), "");
            File.WriteAllText(Path.Combine(sub, "d.jpg"), "");

            var errors = new List<string>();
            var result = MediaExtensions.ExpandPaths(new[] { dir }, errors.Add);

            // Should contain a.mkv and sub/c.mp4 but NOT b.txt or d.jpg. Output is lexicographically sorted (case-insensitive, ordinal); "a.mkv" sorts before "sub/c.mp4" because '/' < ASCII letters.
            Assert.That(result, Is.EqualTo(new[] { Path.Combine(dir, "a.mkv"), Path.Combine(sub, "c.mp4") }));
            Assert.That(errors, Is.Empty);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void FindAdjacentByNameStepsThroughMiddle()
    {
        var c = new[] { "/d/a.mp4", "/d/b.mp4", "/d/c.mp4" };
        Assert.That(MediaExtensions.FindAdjacentByName(c, "/d/b.mp4", +1), Is.EqualTo("/d/c.mp4"));
        Assert.That(MediaExtensions.FindAdjacentByName(c, "/d/b.mp4", -1), Is.EqualTo("/d/a.mp4"));
    }

    [Test]
    public void FindAdjacentByNameReturnsNullAtEnds()
    {
        var c = new[] { "/d/a.mp4", "/d/b.mp4", "/d/c.mp4" };
        Assert.That(MediaExtensions.FindAdjacentByName(c, "/d/c.mp4", +1), Is.Null, "no next after last");
        Assert.That(MediaExtensions.FindAdjacentByName(c, "/d/a.mp4", -1), Is.Null, "no prev before first");
        Assert.That(MediaExtensions.FindAdjacentByName(Array.Empty<string>(), "/d/a.mp4", +1), Is.Null, "empty candidates");
    }

    [Test]
    public void FindAdjacentByNameResolvesWhenCurrentAbsent()
    {
        // Current file isn't in the candidate list (deleted, or a playable-but-non-video extension the walk skipped). Neighbor is still found by name position.
        var c = new[] { "/d/a.mp4", "/d/c.mp4", "/d/e.mp4" };
        Assert.That(MediaExtensions.FindAdjacentByName(c, "/d/b.mp4", +1), Is.EqualTo("/d/c.mp4"), "next after the gap");
        Assert.That(MediaExtensions.FindAdjacentByName(c, "/d/d.mp4", -1), Is.EqualTo("/d/c.mp4"), "prev before the gap");
    }

    [Test]
    public void FindAdjacentByNameComparesByFilenameNotFullPath()
    {
        // current is a bare relative name; candidates are absolute. Comparison is by filename, so the relative/absolute mismatch doesn't skew ordering.
        var c = new[] { "/some/dir/a.mp4", "/some/dir/b.mp4", "/some/dir/c.mp4" };
        Assert.That(MediaExtensions.FindAdjacentByName(c, "b.mp4", +1), Is.EqualTo("/some/dir/c.mp4"));
    }

    [Test]
    public void FindAdjacentByNameIsCaseInsensitiveButTieBreaksDeterministically()
    {
        // Ordering is case-insensitive (so "Apple.mp4" sits next to "banana.mp4", not after all lowercase).
        var mixed = new[] { "/d/Apple.mp4", "/d/banana.mp4", "/d/Cherry.mp4" };
        Assert.That(MediaExtensions.FindAdjacentByName(mixed, "/d/banana.mp4", +1), Is.EqualTo("/d/Cherry.mp4"));
        Assert.That(MediaExtensions.FindAdjacentByName(mixed, "/d/banana.mp4", -1), Is.EqualTo("/d/Apple.mp4"));

        // Case-only-differing siblings (possible on a case-sensitive FS): they are distinct and ordered by the Ordinal tie-break — uppercase ('M' = 0x4D) sorts before lowercase ('m' = 0x6D).
        var siblings = new[] { "/d/Movie.mp4", "/d/movie.mp4" };
        Assert.That(MediaExtensions.FindAdjacentByName(siblings, "/d/Movie.mp4", +1), Is.EqualTo("/d/movie.mp4"), "next from upper goes to lower");
        Assert.That(MediaExtensions.FindAdjacentByName(siblings, "/d/movie.mp4", -1), Is.EqualTo("/d/Movie.mp4"), "prev from lower goes to upper");
        Assert.That(MediaExtensions.FindAdjacentByName(siblings, "/d/Movie.mp4", -1), Is.Null, "nothing before the upper sibling");
    }

    [Test]
    public void FindDirectoryNeighborWalksDirectoryFilteringNonVideo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vompl-neighbor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.mp4"), "");
            File.WriteAllText(Path.Combine(dir, "b.mp4"), "");
            File.WriteAllText(Path.Combine(dir, "c.mp4"), "");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "");

            var errors = new List<string>();
            string b = Path.Combine(dir, "b.mp4");
            Assert.That(MediaExtensions.FindDirectoryNeighbor(dir, b, +1, errors.Add), Is.EqualTo(Path.Combine(dir, "c.mp4")));
            Assert.That(MediaExtensions.FindDirectoryNeighbor(dir, b, -1, errors.Add), Is.EqualTo(Path.Combine(dir, "a.mp4")));
            Assert.That(MediaExtensions.FindDirectoryNeighbor(dir, Path.Combine(dir, "c.mp4"), +1, errors.Add), Is.Null, "no next past last; notes.txt excluded");
            Assert.That(MediaExtensions.FindDirectoryNeighbor(dir, Path.Combine(dir, "a.mp4"), -1, errors.Add), Is.Null, "no prev before first");
            Assert.That(errors, Is.Empty);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void FindDirectoryNeighborReportsListingFailure()
    {
        var missing = Path.Combine(Path.GetTempPath(), "vompl-missing-" + Guid.NewGuid().ToString("N"));
        var errors = new List<string>();
        var result = MediaExtensions.FindDirectoryNeighbor(missing, Path.Combine(missing, "x.mp4"), +1, errors.Add);
        Assert.That(result, Is.Null);
        Assert.That(errors, Has.Count.EqualTo(1), "listing failure surfaced, not swallowed");
    }

    [Test]
    public void ExpandPathsEmptyDirectoryProducesEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vompl-expand-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var errors = new List<string>();
            var result = MediaExtensions.ExpandPaths(new[] { dir }, errors.Add);
            Assert.That(result, Is.Empty);
            Assert.That(errors, Is.Empty);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void ExpandPathsSortsLexicographicallyAcrossInputAndDirectory()
    {
        // Input order is non-monotonic — both at the input level (file/dir/file) and inside the directory's contents (z.mkv before a.mkv on disk). The output is one flat lex-sorted list across everything.
        var dir = Path.Combine(Path.GetTempPath(), "vompl-expand-sort-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Create files in reverse order to make it harder for the filesystem to coincidentally serve them sorted.
            File.WriteAllText(Path.Combine(dir, "z.mkv"), "");
            File.WriteAllText(Path.Combine(dir, "a.mkv"), "");

            var errors = new List<string>();
            var result = MediaExtensions.ExpandPaths(
                new[] { "/zzz/last.mkv", "/aaa/first.mkv", dir, "/mmm/middle.mkv" },
                errors.Add);

            // Expected sorted output — case-insensitive ordinal. Directory contents (a.mkv, z.mkv) interleave with the explicit file paths by their full-path ordering.
            var expected = new[]
            {
                "/aaa/first.mkv",
                Path.Combine(dir, "a.mkv"),
                Path.Combine(dir, "z.mkv"),
                "/mmm/middle.mkv",
                "/zzz/last.mkv",
            };
            Array.Sort(expected, StringComparer.OrdinalIgnoreCase);   // pin: result matches the documented sort order
            Assert.That(result, Is.EqualTo(expected));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void ExpandPathsSortsCaseInsensitively()
    {
        // Capitalization shouldn't dominate ordering — "Bravo" should sort between "alpha" and "charlie", not after both like a strict ordinal comparison would do.
        var errors = new List<string>();
        var result = MediaExtensions.ExpandPaths(new[] { "charlie.mkv", "Bravo.mkv", "alpha.mkv" }, errors.Add);
        Assert.That(result, Is.EqualTo(new[] { "alpha.mkv", "Bravo.mkv", "charlie.mkv" }));
    }

    [Test]
    public void ExpandPathsMixesFilesAndDirectories()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vompl-expand-mix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "from-dir.mp4"), "");
            File.WriteAllText(Path.Combine(dir, "non-video.txt"), "");

            var errors = new List<string>();
            var result = MediaExtensions.ExpandPaths(new[] { "/explicit/file.mkv", dir, "https://example.com/x" }, errors.Add);

            Assert.That(result, Does.Contain("/explicit/file.mkv"));
            Assert.That(result, Does.Contain(Path.Combine(dir, "from-dir.mp4")));
            Assert.That(result, Does.Contain("https://example.com/x"));
            Assert.That(result, Does.Not.Contain(Path.Combine(dir, "non-video.txt")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
