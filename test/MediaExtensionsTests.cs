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
