using System;
using System.IO;
using NUnit.Framework;
using Vomplayer.Services;

namespace Vomplayer.Tests;

[TestFixture]
public class UrlDownloadCacheTests
{
    private string tempRoot = string.Empty;

    [SetUp]
    public void SetUp()
    {
        tempRoot = Path.Combine(Path.GetTempPath(), $"vompl-cache-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(tempRoot))
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch (Exception ex)
            {
                // Best-effort cleanup. Log rather than swallow per CLAUDE.md; the OS will reclaim the temp dir eventually anyway.
                TestContext.Progress.WriteLine($"[TearDown] tempRoot cleanup failed for {tempRoot}: {ex.Message}");
            }
        }
    }

    [Test]
    public void KeyForIsStableAcrossInstances()
    {
        var a = new UrlDownloadCache(tempRoot);
        var b = new UrlDownloadCache(tempRoot);
        Assert.That(a.KeyFor("https://example.com/a"), Is.EqualTo(b.KeyFor("https://example.com/a")));
    }

    [Test]
    public void KeyForDiffersForDifferentUrls()
    {
        var c = new UrlDownloadCache(tempRoot);
        Assert.That(c.KeyFor("https://example.com/a"), Is.Not.EqualTo(c.KeyFor("https://example.com/b")));
    }

    [Test]
    public void KeyForIsHexAndCorrectLength()
    {
        var c = new UrlDownloadCache(tempRoot);
        var key = c.KeyFor("https://example.com/anything");
        Assert.That(key.Length, Is.EqualTo(16));
        foreach (var ch in key)
        {
            Assert.That("0123456789abcdef".IndexOf(ch), Is.GreaterThanOrEqualTo(0), $"key contained non-hex char '{ch}'");
        }
    }

    [Test]
    public void TryGetExistingFileReturnsNullWithoutManifest()
    {
        var c = new UrlDownloadCache(tempRoot);
        Assert.That(c.TryGetExistingFile("https://example.com/x"), Is.Null);
    }

    [Test]
    public void TryGetExistingFileReturnsPathWithValidManifest()
    {
        var c = new UrlDownloadCache(tempRoot);
        var url = "https://example.com/video";
        var dir = c.DirectoryFor(url);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "video.mp4"), "fake content");
        c.RecordDownload(url, "video.mp4");
        var hit = c.TryGetExistingFile(url);
        Assert.That(hit, Is.EqualTo(Path.Combine(dir, "video.mp4")));
    }

    [Test]
    public void TryGetExistingFileReturnsNullIfReferencedFileMissing()
    {
        var c = new UrlDownloadCache(tempRoot);
        var url = "https://example.com/lost";
        var dir = c.DirectoryFor(url);
        Directory.CreateDirectory(dir);
        c.RecordDownload(url, "video.mp4");
        // Manifest exists but the actual media file does not — manual rm or mid-cleanup race. TryGetExistingFile must NOT report a hit.
        Assert.That(c.TryGetExistingFile(url), Is.Null);
    }

    [Test]
    public void TryGetExistingFileBumpsDirectoryMtime()
    {
        var c = new UrlDownloadCache(tempRoot);
        var url = "https://example.com/touched";
        var dir = c.DirectoryFor(url);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "video.mp4"), "x");
        c.RecordDownload(url, "video.mp4");
        // Backdate the directory to simulate it being old.
        var twoDaysAgo = DateTime.UtcNow.AddHours(-48);
        Directory.SetLastWriteTimeUtc(dir, twoDaysAgo);

        c.TryGetExistingFile(url);

        var mtime = Directory.GetLastWriteTimeUtc(dir);
        Assert.That(mtime, Is.GreaterThan(twoDaysAgo.AddHours(1)), "TryGetExistingFile should have refreshed the directory mtime");
    }

    [Test]
    public void RecordDownloadCreatesManifest()
    {
        var c = new UrlDownloadCache(tempRoot);
        var url = "https://example.com/recorded";
        c.RecordDownload(url, "out.mp4");
        var manifestPath = Path.Combine(c.DirectoryFor(url), "manifest.txt");
        Assert.That(File.Exists(manifestPath), Is.True);
        Assert.That(File.ReadAllText(manifestPath).Trim(), Is.EqualTo("out.mp4"));
    }

    [Test]
    public void CleanupDeletesOldDirs()
    {
        var c = new UrlDownloadCache(tempRoot);
        var url = "https://example.com/old";
        var dir = c.DirectoryFor(url);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "f"), "x");
        // Backdate well past the 24h cutoff.
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddHours(-48));

        c.Cleanup(DateTimeOffset.UtcNow, msg => Assert.Fail($"unexpected error: {msg}"));

        Assert.That(Directory.Exists(dir), Is.False);
    }

    [Test]
    public void CleanupKeepsRecentDirs()
    {
        var c = new UrlDownloadCache(tempRoot);
        var url = "https://example.com/fresh";
        var dir = c.DirectoryFor(url);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "f"), "x");

        c.Cleanup(DateTimeOffset.UtcNow, msg => Assert.Fail($"unexpected error: {msg}"));

        Assert.That(Directory.Exists(dir), Is.True);
    }

    [Test]
    public void CleanupCutoffIsExactly24Hours()
    {
        var c = new UrlDownloadCache(tempRoot);
        var freshDir = c.DirectoryFor("https://example.com/fresh");
        var oldDir = c.DirectoryFor("https://example.com/old");
        Directory.CreateDirectory(freshDir);
        Directory.CreateDirectory(oldDir);
        var now = DateTimeOffset.UtcNow;
        Directory.SetLastWriteTimeUtc(freshDir, now.UtcDateTime.AddHours(-23));
        Directory.SetLastWriteTimeUtc(oldDir, now.UtcDateTime.AddHours(-25));

        c.Cleanup(now, msg => Assert.Fail($"unexpected error: {msg}"));

        Assert.That(Directory.Exists(freshDir), Is.True, "23h-old dir should survive");
        Assert.That(Directory.Exists(oldDir), Is.False, "25h-old dir should be deleted");
    }

    [Test]
    public void CleanupOnNonexistentRootIsNoOp()
    {
        var c = new UrlDownloadCache(Path.Combine(tempRoot, "does-not-exist"));
        c.Cleanup(DateTimeOffset.UtcNow, msg => Assert.Fail($"unexpected error: {msg}"));
        // Just shouldn't throw.
    }
}
