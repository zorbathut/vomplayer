using NUnit.Framework;
using Vomplayer.Services;

namespace Vomplayer.Tests;

// Line-parsing tests plus real-process tests against a bash stub standing in for yt-dlp (the ctor's command list is the injection seam — command[0] is the executable). The stub tests exercise the actual spawn/read/exit plumbing, which is the most failure-prone code in the class.
[TestFixture]
public class YtDlpDownloaderTests
{
    private string? tempDir;

    [SetUp]
    public void Setup()
    {
        tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vompl-ytdlp-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tempDir);
    }

    [TearDown]
    public void Teardown()
    {
        if (tempDir != null && System.IO.Directory.Exists(tempDir))
        {
            System.IO.Directory.Delete(tempDir, recursive: true);
        }
    }

    // Writes an executable bash script the downloader will spawn instead of yt-dlp.
    private string WriteStub(string body)
    {
        var path = System.IO.Path.Combine(tempDir!, "fake-ytdlp.sh");
        System.IO.File.WriteAllText(path, "#!/bin/bash\n" + body + "\n");
        System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private YtDlpDownloader NewWithStub(string body)
    {
        var cache = new UrlDownloadCache(System.IO.Path.Combine(tempDir!, "cache"));
        return new YtDlpDownloader(cache, new[] { WriteStub(body) });
    }

    [Test]
    public async Task IsAvailableAsyncTrueWhenVersionExitsZero()
    {
        var dl = NewWithStub("echo 2026.01.01; exit 0");
        Assert.That(await dl.IsAvailableAsync(CancellationToken.None), Is.True);
    }

    [Test]
    public async Task IsAvailableAsyncFalseWhenBinaryMissing()
    {
        var cache = new UrlDownloadCache(System.IO.Path.Combine(tempDir!, "cache"));
        var dl = new YtDlpDownloader(cache, new[] { System.IO.Path.Combine(tempDir!, "does-not-exist") });
        Assert.That(await dl.IsAvailableAsync(CancellationToken.None), Is.False);
    }

    [Test]
    public async Task ProbeAsyncReturnsOneEntryPerStdoutLine()
    {
        var dl = NewWithStub("echo https://example.com/a; echo https://example.com/b");
        var entries = await dl.ProbeAsync("https://example.com/playlist", CancellationToken.None);
        Assert.That(entries, Is.EqualTo(new[] { "https://example.com/a", "https://example.com/b" }));
    }

    [Test]
    public async Task ProbeAsyncEmptyOutputFallsBackToTheUrlItself()
    {
        var dl = NewWithStub("exit 0");
        var entries = await dl.ProbeAsync("https://example.com/v", CancellationToken.None);
        Assert.That(entries, Is.EqualTo(new[] { "https://example.com/v" }));
    }

    [Test]
    public void ProbeAsyncNonZeroExitThrowsWithStderr()
    {
        var dl = NewWithStub("echo 'ERROR: unsupported url' >&2; exit 1");
        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => dl.ProbeAsync("https://example.com/x", CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("unsupported url"));
    }

    [Test]
    public async Task ClassifyAsyncGenericExtractorRoutesMpvDirect()
    {
        var dl = NewWithStub("echo generic");
        Assert.That(await dl.ClassifyAsync("https://example.com/v.mp4", CancellationToken.None), Is.EqualTo(UrlLoadKind.MpvDirect));
    }

    [Test]
    public async Task ClassifyAsyncSpecificExtractorRoutesDownload()
    {
        var dl = NewWithStub("echo youtube");
        Assert.That(await dl.ClassifyAsync("https://youtu.be/x", CancellationToken.None), Is.EqualTo(UrlLoadKind.YtDlpDownload));
    }

    [Test]
    public async Task DownloadAsyncReportsProgressUsesAfterMovePathAndRecordsCacheHit()
    {
        // Full happy path against the stub: progress lines drive IProgress, the after_move print names the final file, RecordDownload makes the next DownloadAsync a cache hit that spawns nothing (the second stub body would fail the test loudly if executed).
        var cacheRoot = System.IO.Path.Combine(tempDir!, "cache");
        var cache = new UrlDownloadCache(cacheRoot);
        var stub = WriteStub(string.Join("\n", new[]
        {
            "while [[ $# -gt 0 ]]; do case \"$1\" in -P) dir=\"$2\"; shift 2;; --) url=\"$2\"; shift 2;; *) shift;; esac; done",
            "echo 'VOMPLPROG 100 1000 NA downloading'",
            "mkdir -p \"$dir\"",
            "printf 'fake video bytes' > \"$dir/Video Title.mp4\"",
            "echo \"VOMPLFILE $dir/Video Title.mp4\"",
            "echo 'VOMPLPROG 1000 1000 NA finished'",
        }));
        var dl = new YtDlpDownloader(cache, new[] { stub });
        var ticks = new List<UrlDownloadProgress>();
        var progress = new SynchronousProgress(ticks.Add);

        var path = await dl.DownloadAsync("https://example.com/v", progress, CancellationToken.None);

        Assert.That(System.IO.File.ReadAllText(path), Is.EqualTo("fake video bytes"));
        Assert.That(System.IO.Path.GetFileName(path), Is.EqualTo("Video Title.mp4"));
        Assert.That(ticks.Any(p => p.Status == "downloading" && p.DownloadedBytes == 100 && p.TotalBytes == 1000), Is.True);
        Assert.That(ticks.Any(p => p.Status == "finished"), Is.True);

        // Cache hit: swap in a stub that would poison the result if spawned.
        System.IO.File.WriteAllText(stub, "#!/bin/bash\necho 'MUST NOT RUN' >&2; exit 99\n");
        var again = await dl.DownloadAsync("https://example.com/v", null, CancellationToken.None);
        Assert.That(again, Is.EqualTo(path));
    }

    [Test]
    public void DownloadAsyncNonZeroExitThrowsWithStderr()
    {
        var dl = NewWithStub("echo 'ERROR: network down' >&2; exit 1");
        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => dl.DownloadAsync("https://example.com/v", null, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("network down"));
    }

    [Test]
    public async Task DownloadAsyncMissingAfterMovePrintFallsBackToDirectoryScan()
    {
        // yt-dlp succeeded but never printed the after_move filepath — FindMediaFileIn picks the largest non-sidecar file.
        var stubBody = string.Join("\n", new[]
        {
            "while [[ $# -gt 0 ]]; do case \"$1\" in -P) dir=\"$2\"; shift 2;; --) url=\"$2\"; shift 2;; *) shift;; esac; done",
            "mkdir -p \"$dir\"",
            "printf 'sidecar' > \"$dir/state.ytdl\"",
            "printf 'the real media file content' > \"$dir/clip.mp4\"",
        });
        var dl = NewWithStub(stubBody);
        var path = await dl.DownloadAsync("https://example.com/w", null, CancellationToken.None);
        Assert.That(System.IO.Path.GetFileName(path), Is.EqualTo("clip.mp4"));
    }

    // IProgress<T> whose callback runs inline — Progress<T> posts to a sync context the test doesn't pump.
    private sealed class SynchronousProgress : IProgress<UrlDownloadProgress>
    {
        private readonly Action<UrlDownloadProgress> handler;

        public SynchronousProgress(Action<UrlDownloadProgress> handler)
        {
            this.handler = handler;
        }

        public void Report(UrlDownloadProgress value)
        {
            handler(value);
        }
    }

    [Test]
    public void TryParseProgressParsesNumericFields()
    {
        bool ok = YtDlpDownloader.TryParseProgress("VOMPLPROG 1234 5678 NA downloading", out var p);
        Assert.That(ok, Is.True);
        Assert.That(p.DownloadedBytes, Is.EqualTo(1234));
        Assert.That(p.TotalBytes, Is.EqualTo(5678));
        Assert.That(p.Status, Is.EqualTo("downloading"));
    }

    [Test]
    public void TryParseProgressTreatsNAAsNullTotal()
    {
        bool ok = YtDlpDownloader.TryParseProgress("VOMPLPROG 4096 NA NA downloading", out var p);
        Assert.That(ok, Is.True);
        Assert.That(p.DownloadedBytes, Is.EqualTo(4096));
        Assert.That(p.TotalBytes, Is.Null);
    }

    [Test]
    public void TryParseProgressRejectsLineWithoutPrefix()
    {
        bool ok = YtDlpDownloader.TryParseProgress("[download] 12.3% of 100MiB", out _);
        Assert.That(ok, Is.False);
    }

    [Test]
    public void TryParseProgressRejectsTooFewFields()
    {
        bool ok = YtDlpDownloader.TryParseProgress("VOMPLPROG 1234", out _);
        Assert.That(ok, Is.False);
    }

    [Test]
    public void TryParseProgressTreatsUnparseableDownloadedAsZero()
    {
        // NA-as-downloaded happens early in the connection phase before yt-dlp knows the size. We map to 0 so the dialog renders as 0% rather than crashing.
        bool ok = YtDlpDownloader.TryParseProgress("VOMPLPROG NA 9999 NA downloading", out var p);
        Assert.That(ok, Is.True);
        Assert.That(p.DownloadedBytes, Is.EqualTo(0));
        Assert.That(p.TotalBytes, Is.EqualTo(9999));
    }

    [Test]
    public void TryParseProgressFallsBackToEstimateWhenTotalIsNA()
    {
        // HLS/DASH downloads: total_bytes is NA and the usable figure lands in total_bytes_estimate (a float). The bar should be determinate off the estimate.
        bool ok = YtDlpDownloader.TryParseProgress("VOMPLPROG 4096 NA 12345678.0 downloading", out var p);
        Assert.That(ok, Is.True);
        Assert.That(p.TotalBytes, Is.EqualTo(12345678L));
        Assert.That(p.DownloadedBytes, Is.EqualTo(4096));
    }

    [Test]
    public void TryParseProgressHandlesFinishedStatus()
    {
        bool ok = YtDlpDownloader.TryParseProgress("VOMPLPROG 50000 50000 NA finished", out var p);
        Assert.That(ok, Is.True);
        Assert.That(p.Status, Is.EqualTo("finished"));
    }

    [Test]
    public void BuildCommandOutsideFlatpakIsBareBinary()
    {
        // Outside a sandbox we invoke yt-dlp directly on PATH — no wrapping.
        Assert.That(YtDlpDownloader.BuildCommand(false), Is.EqualTo(new[] { "yt-dlp" }));
    }

    [Test]
    public void BuildCommandInsideFlatpakWrapsWithHostSpawn()
    {
        // Full ordered sequence: --host must precede the command, and --watch-bus must not silently drop out (it's what tears down the host yt-dlp on cancel/crash).
        Assert.That(
            YtDlpDownloader.BuildCommand(true),
            Is.EqualTo(new[] { "flatpak-spawn", "--host", "--watch-bus", "yt-dlp" }));
    }

    [Test]
    public void ConstructorRejectsEmptyCommand()
    {
        var cache = new UrlDownloadCache(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vompl-ctor-test"));
        Assert.Throws<System.ArgumentException>(() => new YtDlpDownloader(cache, System.Array.Empty<string>()));
    }
}
