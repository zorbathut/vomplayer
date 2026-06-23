using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Vomplayer.Services;

namespace Vomplayer.Tests;

[TestFixture]
public class UrlLoadCoordinatorTests
{
    // FakeUrlDownloader for coordinator tests. ClassifyAsync resolves synchronously by default (configurable result) so most tests don't have to drive it; PendingClassify lets tests that need to interleave probe and download race events drive it asynchronously. Each DownloadAsync call gets a fresh TaskCompletionSource that the test can drive (SetResult / TrySetCanceled / SetException) to simulate completion, cancellation, or failure. The fake also registers a TrySetCanceled callback on the supplied CancellationToken so the coordinator's CancelActive() propagates through to the download task naturally.
    private sealed class FakeUrlDownloader : IUrlDownloader
    {
        public bool Available { get; set; } = true;
        public IReadOnlyList<string> NextProbeResult { get; set; } = Array.Empty<string>();
        public Func<string, CancellationToken, Task<IReadOnlyList<string>>>? ProbeOverride { get; set; }
        // Default: every URL is a yt-dlp-extractor URL (the common case — Open URL prompts feed YouTube etc.). Tests for the mpv-direct branch override to MpvDirect.
        public UrlLoadKind ClassifyResult { get; set; } = UrlLoadKind.YtDlpDownload;
        public Exception? ClassifyException { get; set; }
        public TaskCompletionSource<UrlLoadKind>? PendingClassify { get; set; }
        public List<string> ProbeCalls { get; } = new();
        public List<string> ClassifyCalls { get; } = new();
        public List<TaskCompletionSource<string>> Downloads { get; } = new();
        public List<string> DownloadUrls { get; } = new();

        public bool IsAvailable()
        {
            return Available;
        }

        public Task<IReadOnlyList<string>> ProbeAsync(string url, CancellationToken ct)
        {
            ProbeCalls.Add(url);
            if (ProbeOverride != null)
            {
                return ProbeOverride(url, ct);
            }
            return Task.FromResult(NextProbeResult);
        }

        public Task<UrlLoadKind> ClassifyAsync(string url, CancellationToken ct)
        {
            ClassifyCalls.Add(url);
            if (PendingClassify != null)
            {
                var tcs = PendingClassify;
                ct.Register(() => tcs.TrySetCanceled(ct));
                return tcs.Task;
            }
            if (ClassifyException != null)
            {
                throw ClassifyException;
            }
            return Task.FromResult(ClassifyResult);
        }

        public Task<string> DownloadAsync(string url, IProgress<UrlDownloadProgress>? progress, CancellationToken ct)
        {
            DownloadUrls.Add(url);
            var tcs = new TaskCompletionSource<string>();
            Downloads.Add(tcs);
            // Mirror YtDlpDownloader's contract: a cancelled token cancels the in-flight download.
            ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        }
    }

    private sealed class FakeUrlPrompt : IUrlPrompt
    {
        public string? NextUrl { get; set; }
        public List<(string Title, string Message)> Errors { get; } = new();
        public int PromptCalls { get; private set; }
        public int ProgressShown { get; private set; }
        public int ProgressDisposed { get; private set; }

        public Task<string?> PromptForUrlAsync(string title)
        {
            PromptCalls++;
            return Task.FromResult(NextUrl);
        }

        public void ShowError(string title, string message)
        {
            Errors.Add((title, message));
        }

        public UrlProgressHandle ShowDownloadProgress(string title, CancellationTokenSource cts)
        {
            ProgressShown++;
            return new UrlProgressHandle(new TrackingDisposable(this), new Progress<UrlDownloadProgress>(_ => { }));
        }

        private sealed class TrackingDisposable : IDisposable
        {
            private readonly FakeUrlPrompt owner;
            private bool disposed;
            public TrackingDisposable(FakeUrlPrompt owner) { this.owner = owner; }
            public void Dispose()
            {
                if (disposed) { return; }
                disposed = true;
                owner.ProgressDisposed++;
            }
        }
    }

    [Test]
    public void NullDownloaderThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new UrlLoadCoordinator(null!, new FakeUrlPrompt()));
    }

    [Test]
    public void NullPromptThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new UrlLoadCoordinator(new FakeUrlDownloader(), null!));
    }

    [Test]
    public void ShouldProbe_TrueForHttpUrls_FalseForOtherSchemesAndLocalPaths()
    {
        // Only http/https URLs are sent through yt-dlp's classification step. Local paths, mpv-native protocols (smb, sftp, dvd, bd, rtsp, …) and unrelated schemes (magnet:) bypass — yt-dlp has no extractor for them, and forcing them through the probe would just produce noise.
        var coord = new UrlLoadCoordinator(new FakeUrlDownloader(), new FakeUrlPrompt());
        Assert.That(coord.ShouldProbe("https://youtu.be/X"), Is.True);
        Assert.That(coord.ShouldProbe("http://example.com/stream.mp4"), Is.True);
        Assert.That(coord.ShouldProbe("HTTP://EXAMPLE.COM"), Is.True);
        Assert.That(coord.ShouldProbe("ftp://host/file"), Is.False);
        Assert.That(coord.ShouldProbe("smb://server/share/file.mkv"), Is.False);
        Assert.That(coord.ShouldProbe("sftp://user@host/file"), Is.False);
        Assert.That(coord.ShouldProbe("dvd://1"), Is.False);
        Assert.That(coord.ShouldProbe("bd://"), Is.False);
        Assert.That(coord.ShouldProbe("rtsp://host/stream"), Is.False);
        Assert.That(coord.ShouldProbe("file:///home/zorba/video.mp4"), Is.False);
        Assert.That(coord.ShouldProbe("magnet:?xt=urn:btih:..."), Is.False);
        Assert.That(coord.ShouldProbe("/home/zorba/video.mp4"), Is.False);
        Assert.That(coord.ShouldProbe("C:\\Users\\zorba\\video.mp4"), Is.False);
        Assert.That(coord.ShouldProbe("relative/path.mp4"), Is.False);
        Assert.That(coord.ShouldProbe(""), Is.False);
    }

    [Test]
    public async Task OpenUrl_YtDlpUnavailable_ShowsErrorReturnsNull()
    {
        var dl = new FakeUrlDownloader { Available = false };
        var prompt = new FakeUrlPrompt { NextUrl = "https://x" };
        var coord = new UrlLoadCoordinator(dl, prompt);

        var result = await coord.OpenUrlInteractiveAsync();

        Assert.That(result, Is.Null);
        Assert.That(prompt.PromptCalls, Is.EqualTo(0), "prompt should not be shown when yt-dlp is unavailable");
        Assert.That(prompt.Errors, Has.Count.EqualTo(1));
        Assert.That(prompt.Errors[0].Title, Does.Contain("yt-dlp"));
        Assert.That(dl.ProbeCalls, Is.Empty);
    }

    [Test]
    public async Task OpenUrl_PromptCancelled_NoErrorNoProbe()
    {
        var dl = new FakeUrlDownloader();
        var prompt = new FakeUrlPrompt { NextUrl = null };
        var coord = new UrlLoadCoordinator(dl, prompt);

        var result = await coord.OpenUrlInteractiveAsync();

        Assert.That(result, Is.Null);
        Assert.That(prompt.PromptCalls, Is.EqualTo(1));
        Assert.That(prompt.Errors, Is.Empty, "cancelling the prompt is not an error");
        Assert.That(dl.ProbeCalls, Is.Empty);
    }

    [Test]
    public async Task OpenUrl_EmptyUrl_TreatedAsCancelled()
    {
        var dl = new FakeUrlDownloader();
        var prompt = new FakeUrlPrompt { NextUrl = "" };
        var coord = new UrlLoadCoordinator(dl, prompt);

        var result = await coord.OpenUrlInteractiveAsync();

        Assert.That(result, Is.Null);
        Assert.That(prompt.Errors, Is.Empty);
        Assert.That(dl.ProbeCalls, Is.Empty);
    }

    [Test]
    public async Task OpenUrl_ProbeReturnsEntries_HappyPath()
    {
        var dl = new FakeUrlDownloader
        {
            NextProbeResult = new[] { "https://a", "https://b" },
        };
        var prompt = new FakeUrlPrompt { NextUrl = "https://playlist" };
        var coord = new UrlLoadCoordinator(dl, prompt);

        var result = await coord.OpenUrlInteractiveAsync();

        Assert.That(result, Is.Not.Null);
        Assert.That(result!, Is.EquivalentTo(new[] { "https://a", "https://b" }));
        Assert.That(dl.ProbeCalls, Is.EqualTo(new[] { "https://playlist" }));
        Assert.That(prompt.Errors, Is.Empty);
    }

    [Test]
    public async Task OpenUrl_ProbeReturnsEmpty_ShowsErrorReturnsNull()
    {
        var dl = new FakeUrlDownloader { NextProbeResult = Array.Empty<string>() };
        var prompt = new FakeUrlPrompt { NextUrl = "https://playlist" };
        var coord = new UrlLoadCoordinator(dl, prompt);

        var result = await coord.OpenUrlInteractiveAsync();

        Assert.That(result, Is.Null);
        Assert.That(prompt.Errors, Has.Count.EqualTo(1));
        Assert.That(prompt.Errors[0].Message, Does.Contain("no entries"));
    }

    [Test]
    public async Task OpenUrl_ProbeThrows_ShowsErrorReturnsNull()
    {
        var dl = new FakeUrlDownloader
        {
            ProbeOverride = (url, ct) => throw new InvalidOperationException("yt-dlp blew up"),
        };
        var prompt = new FakeUrlPrompt { NextUrl = "https://x" };
        var coord = new UrlLoadCoordinator(dl, prompt);

        var result = await coord.OpenUrlInteractiveAsync();

        Assert.That(result, Is.Null);
        Assert.That(prompt.Errors, Has.Count.EqualTo(1));
        Assert.That(prompt.Errors[0].Message, Does.Contain("yt-dlp blew up"));
    }

    [Test]
    public void StartUrlLoad_YtDlpUnavailable_ShowsSameErrorAsOpenUrlAndSkipsLoad()
    {
        // The non-interactive load path (playlist row click, drag-drop, command line, autosave restore) must gate on yt-dlp availability exactly like the interactive Open-URL path — otherwise a URL load with yt-dlp absent silently falls back to mpv-direct and "nothing happens". Same dialog, no classify, no onResolved.
        var dl = new FakeUrlDownloader { Available = false };
        var prompt = new FakeUrlPrompt();
        using var coord = new UrlLoadCoordinator(dl, prompt);

        bool fired = false;
        coord.StartUrlLoad("https://youtu.be/X", (_, _) => fired = true);

        Assert.That(fired, Is.False, "no load should fire when yt-dlp is unavailable");
        Assert.That(dl.ClassifyCalls, Is.Empty, "classification must not run without yt-dlp");
        Assert.That(dl.Downloads, Is.Empty);
        Assert.That(prompt.ProgressShown, Is.EqualTo(0));
        Assert.That(prompt.Errors, Has.Count.EqualTo(1));
        Assert.That(prompt.Errors[0].Title, Does.Contain("yt-dlp"));
        Assert.That(prompt.Errors[0].Message, Does.Contain("Install yt-dlp"));
    }

    [Test]
    public async Task StartUrlLoad_YtDlpUnavailable_ErrorMatchesOpenUrlInteractive()
    {
        // Lock in "the same error dialog" — both gates must surface byte-identical title+message.
        var unavailable = new FakeUrlDownloader { Available = false };
        var interactivePrompt = new FakeUrlPrompt { NextUrl = "https://x" };
        var interactiveCoord = new UrlLoadCoordinator(unavailable, interactivePrompt);
        await interactiveCoord.OpenUrlInteractiveAsync();

        var loadPrompt = new FakeUrlPrompt();
        using var loadCoord = new UrlLoadCoordinator(unavailable, loadPrompt);
        loadCoord.StartUrlLoad("https://x", (_, _) => { });

        Assert.That(interactivePrompt.Errors, Has.Count.EqualTo(1));
        Assert.That(loadPrompt.Errors, Has.Count.EqualTo(1));
        Assert.That(loadPrompt.Errors[0], Is.EqualTo(interactivePrompt.Errors[0]));
    }

    [Test]
    public async Task StartUrlLoad_ClassifyExtractor_DownloadsAndFiresOnResolved()
    {
        var dl = new FakeUrlDownloader();  // ClassifyResult defaults to YtDlpDownload
        var prompt = new FakeUrlPrompt();
        using var coord = new UrlLoadCoordinator(dl, prompt);

        string? completedUrl = null;
        string? completedPath = null;
        var done = new TaskCompletionSource();
        coord.StartUrlLoad("https://x", (url, path) =>
        {
            completedUrl = url;
            completedPath = path;
            done.SetResult();
        });

        // Probe completed synchronously → YtDlpDownload → DownloadAsync was called and is awaiting its TCS.
        Assert.That(dl.ClassifyCalls, Is.EqualTo(new[] { "https://x" }));
        Assert.That(dl.Downloads, Has.Count.EqualTo(1));
        dl.Downloads[0].SetResult("/cache/x");
        await done.Task;

        Assert.That(completedUrl, Is.EqualTo("https://x"));
        Assert.That(completedPath, Is.EqualTo("/cache/x"));
        Assert.That(prompt.ProgressShown, Is.EqualTo(1));
        Assert.That(prompt.ProgressDisposed, Is.EqualTo(1));
        Assert.That(prompt.Errors, Is.Empty);
    }

    [Test]
    public async Task StartUrlLoad_ClassifyGeneric_BypassesDownloadAndFiresWithOriginalUrl()
    {
        // yt-dlp's generic extractor matched — that means the URL is a direct media link (an .mp4 / .m3u8 / etc.) that mpv can stream natively. No download needed, no progress dialog.
        var dl = new FakeUrlDownloader { ClassifyResult = UrlLoadKind.MpvDirect };
        var prompt = new FakeUrlPrompt();
        using var coord = new UrlLoadCoordinator(dl, prompt);

        string? completedUrl = null;
        string? completedPath = null;
        var done = new TaskCompletionSource();
        coord.StartUrlLoad("https://example.com/stream.m3u8", (url, path) =>
        {
            completedUrl = url;
            completedPath = path;
            done.SetResult();
        });

        await done.Task;
        Assert.That(completedUrl, Is.EqualTo("https://example.com/stream.m3u8"));
        Assert.That(completedPath, Is.EqualTo("https://example.com/stream.m3u8"), "mpv-direct branch hands back the URL unchanged");
        Assert.That(dl.Downloads, Is.Empty, "no download spawned for generic-extractor URLs");
        Assert.That(prompt.ProgressShown, Is.EqualTo(0), "no progress dialog for mpv-direct");
        Assert.That(prompt.Errors, Is.Empty);
    }

    [Test]
    public async Task StartUrlLoad_ClassifyThrows_FallsBackToMpvDirect()
    {
        // yt-dlp not installed / network error / extractor crash → we don't refuse the load. Treat probe failure as "fall back to mpv-direct" and let mpv's own ytdl-hook surface a useful error if it actually needed yt-dlp.
        var dl = new FakeUrlDownloader { ClassifyException = new InvalidOperationException("yt-dlp not on PATH") };
        var prompt = new FakeUrlPrompt();
        using var coord = new UrlLoadCoordinator(dl, prompt);

        string? completedPath = null;
        var done = new TaskCompletionSource();
        coord.StartUrlLoad("https://youtu.be/X", (_, path) =>
        {
            completedPath = path;
            done.SetResult();
        });

        await done.Task;
        Assert.That(completedPath, Is.EqualTo("https://youtu.be/X"));
        Assert.That(dl.Downloads, Is.Empty);
        Assert.That(prompt.Errors, Is.Empty, "probe failure is silent — mpv surfaces its own error if needed");
    }

    [Test]
    public async Task StartUrlLoad_CancelActive_SuppressesOnResolved()
    {
        // PendingClassify lets us suspend the probe step so CancelActive can fire during it.
        var dl = new FakeUrlDownloader { PendingClassify = new TaskCompletionSource<UrlLoadKind>() };
        var prompt = new FakeUrlPrompt();
        using var coord = new UrlLoadCoordinator(dl, prompt);

        bool fired = false;
        coord.StartUrlLoad("https://x", (_, _) => fired = true);
        Assert.That(dl.ClassifyCalls, Has.Count.EqualTo(1));

        coord.CancelActive();
        await Task.Yield();

        Assert.That(fired, Is.False);
        // No progress dialog ever opened — cancellation happened before the YtDlpDownload branch.
        Assert.That(prompt.ProgressShown, Is.EqualTo(0));
        Assert.That(prompt.Errors, Is.Empty);
    }

    [Test]
    public async Task StartUrlLoad_CancelActive_DuringDownload_SuppressesOnResolved()
    {
        var dl = new FakeUrlDownloader();
        var prompt = new FakeUrlPrompt();
        using var coord = new UrlLoadCoordinator(dl, prompt);

        bool fired = false;
        coord.StartUrlLoad("https://x", (_, _) => fired = true);
        // Classify resolves immediately, download is awaiting its TCS.
        Assert.That(dl.Downloads, Has.Count.EqualTo(1));

        coord.CancelActive();
        await Task.Yield();

        Assert.That(fired, Is.False);
        Assert.That(prompt.ProgressDisposed, Is.EqualTo(1));
        Assert.That(prompt.Errors, Is.Empty, "user cancellation is not an error");
    }

    [Test]
    public async Task StartUrlLoad_SecondCallSupersedesFirst_OldOnResolvedSuppressed()
    {
        var dl = new FakeUrlDownloader();
        var prompt = new FakeUrlPrompt();
        using var coord = new UrlLoadCoordinator(dl, prompt);

        bool firstFired = false;
        coord.StartUrlLoad("https://first", (_, _) => firstFired = true);
        Assert.That(dl.Downloads, Has.Count.EqualTo(1));

        var secondDone = new TaskCompletionSource();
        coord.StartUrlLoad("https://second", (_, _) => secondDone.SetResult());
        Assert.That(dl.Downloads, Has.Count.EqualTo(2));

        dl.Downloads[1].SetResult("/cache/second");
        await secondDone.Task;

        Assert.That(firstFired, Is.False, "first onResolved must not fire after being superseded");
        Assert.That(prompt.ProgressDisposed, Is.EqualTo(2));
    }

    [Test]
    public async Task StartUrlLoad_DownloadException_ShowsErrorSuppressesOnResolved()
    {
        var dl = new FakeUrlDownloader();
        var prompt = new FakeUrlPrompt();
        using var coord = new UrlLoadCoordinator(dl, prompt);

        bool fired = false;
        coord.StartUrlLoad("https://x", (_, _) => fired = true);
        dl.Downloads[0].SetException(new InvalidOperationException("download failed"));
        for (int i = 0; i < 10 && prompt.Errors.Count == 0; i++)
        {
            await Task.Yield();
        }

        Assert.That(fired, Is.False);
        Assert.That(prompt.Errors, Has.Count.EqualTo(1));
        Assert.That(prompt.Errors[0].Title, Is.EqualTo("Download failed"));
        Assert.That(prompt.Errors[0].Message, Does.Contain("download failed"));
        Assert.That(prompt.ProgressDisposed, Is.EqualTo(1));
    }

    [Test]
    public async Task StartUrlLoad_SupersededDownloadCompletingLate_ShowsNoError()
    {
        var dl = new FakeUrlDownloader();
        var prompt = new FakeUrlPrompt();
        using var coord = new UrlLoadCoordinator(dl, prompt);

        coord.StartUrlLoad("https://first", (_, _) => { });
        Assert.That(dl.Downloads, Has.Count.EqualTo(1));

        var firstTcs = dl.Downloads[0];
        coord.StartUrlLoad("https://second", (_, _) => { });
        Assert.That(dl.Downloads, Has.Count.EqualTo(2));

        firstTcs.TrySetException(new InvalidOperationException("late failure"));
        await Task.Yield();

        Assert.That(prompt.Errors, Is.Empty, "stale download's late exception must not surface to the user");
    }

    [Test]
    public async Task CancelActive_NoActiveDownload_IsNoOp()
    {
        var coord = new UrlLoadCoordinator(new FakeUrlDownloader(), new FakeUrlPrompt());
        coord.CancelActive();
        coord.CancelActive();
        // Idempotent — nothing throws, no Tasks to await.
        await Task.CompletedTask;
    }

    [Test]
    public async Task Dispose_CancelsActiveLoad()
    {
        var dl = new FakeUrlDownloader();
        var prompt = new FakeUrlPrompt();
        var coord = new UrlLoadCoordinator(dl, prompt);

        bool fired = false;
        coord.StartUrlLoad("https://x", (_, _) => fired = true);
        Assert.That(dl.Downloads, Has.Count.EqualTo(1));

        coord.Dispose();
        await Task.Yield();

        Assert.That(fired, Is.False);
        Assert.That(prompt.ProgressDisposed, Is.EqualTo(1));
    }
}
