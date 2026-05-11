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
    // FakeUrlDownloader for coordinator tests. Each DownloadAsync call gets a fresh TaskCompletionSource that the test can drive (SetResult / TrySetCanceled / SetException) to simulate completion, cancellation, or failure. The fake also registers a TrySetCanceled callback on the supplied CancellationToken so the coordinator's CancelActive() propagates through to the download task naturally.
    private sealed class FakeUrlDownloader : IUrlDownloader
    {
        public bool Available { get; set; } = true;
        public IReadOnlyList<string> NextProbeResult { get; set; } = Array.Empty<string>();
        public Func<string, CancellationToken, Task<IReadOnlyList<string>>>? ProbeOverride { get; set; }
        public List<string> ProbeCalls { get; } = new();
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
    public void ShouldDownload_TrueForMembers_FalseForOthers()
    {
        var coord = new UrlLoadCoordinator(new FakeUrlDownloader(), new FakeUrlPrompt());
        coord.SetUrlsRequiringDownload(new[] { "https://a", "https://b" });
        Assert.That(coord.ShouldDownload("https://a"), Is.True);
        Assert.That(coord.ShouldDownload("https://b"), Is.True);
        Assert.That(coord.ShouldDownload("https://c"), Is.False);
        Assert.That(coord.ShouldDownload(""), Is.False);
    }

    [Test]
    public void SetUrlsRequiringDownload_Replaces()
    {
        var coord = new UrlLoadCoordinator(new FakeUrlDownloader(), new FakeUrlPrompt());
        coord.SetUrlsRequiringDownload(new[] { "https://a" });
        coord.SetUrlsRequiringDownload(new[] { "https://b" });
        // Old URL is no longer flagged — replace, not merge.
        Assert.That(coord.ShouldDownload("https://a"), Is.False);
        Assert.That(coord.ShouldDownload("https://b"), Is.True);
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
    public async Task StartDownload_Success_FiresOnCompleted()
    {
        var dl = new FakeUrlDownloader();
        var prompt = new FakeUrlPrompt();
        using var coord = new UrlLoadCoordinator(dl, prompt);

        string? completedUrl = null;
        string? completedLocal = null;
        var done = new TaskCompletionSource();
        coord.StartDownload("https://x", (url, local) =>
        {
            completedUrl = url;
            completedLocal = local;
            done.SetResult();
        });

        // Coordinator's RunAsync is now awaiting our TCS. Drive it to completion.
        Assert.That(dl.Downloads, Has.Count.EqualTo(1));
        dl.Downloads[0].SetResult("/cache/x");
        await done.Task;

        Assert.That(completedUrl, Is.EqualTo("https://x"));
        Assert.That(completedLocal, Is.EqualTo("/cache/x"));
        Assert.That(prompt.ProgressShown, Is.EqualTo(1));
        Assert.That(prompt.ProgressDisposed, Is.EqualTo(1));
        Assert.That(prompt.Errors, Is.Empty);
    }

    [Test]
    public async Task StartDownload_CancelActive_SuppressesOnCompleted()
    {
        var dl = new FakeUrlDownloader();
        var prompt = new FakeUrlPrompt();
        using var coord = new UrlLoadCoordinator(dl, prompt);

        bool fired = false;
        coord.StartDownload("https://x", (_, _) => fired = true);
        Assert.That(dl.Downloads, Has.Count.EqualTo(1));

        coord.CancelActive();
        // CancelActive propagates through the registered ct callback → TrySetCanceled on the TCS → RunAsync's await throws OperationCanceledException → caught silently. Yielding lets the cancellation continuation drain.
        await Task.Yield();

        Assert.That(fired, Is.False);
        // The progress dialog is still disposed by RunAsync's finally even on cancellation.
        Assert.That(prompt.ProgressDisposed, Is.EqualTo(1));
        Assert.That(prompt.Errors, Is.Empty, "user cancellation is not an error");
    }

    [Test]
    public async Task StartDownload_SecondCallSupersedesFirst_OldOnCompletedSuppressed()
    {
        var dl = new FakeUrlDownloader();
        var prompt = new FakeUrlPrompt();
        using var coord = new UrlLoadCoordinator(dl, prompt);

        bool firstFired = false;
        coord.StartDownload("https://first", (_, _) => firstFired = true);
        Assert.That(dl.Downloads, Has.Count.EqualTo(1));

        // Second StartDownload cancels the first internally before installing its own CTS.
        var secondDone = new TaskCompletionSource();
        coord.StartDownload("https://second", (_, _) => secondDone.SetResult());
        Assert.That(dl.Downloads, Has.Count.EqualTo(2));

        // Drive the second download to completion. The first's TCS was already cancelled by CancelActive — its RunAsync's await threw and exited the race-guard branch.
        dl.Downloads[1].SetResult("/cache/second");
        await secondDone.Task;

        Assert.That(firstFired, Is.False, "first onCompleted must not fire after being superseded");
        // Both downloads' progress dialogs are disposed (first by cancellation, second by completion).
        Assert.That(prompt.ProgressDisposed, Is.EqualTo(2));
    }

    [Test]
    public async Task StartDownload_NonCancelException_ShowsErrorSuppressesOnCompleted()
    {
        var dl = new FakeUrlDownloader();
        var prompt = new FakeUrlPrompt();
        using var coord = new UrlLoadCoordinator(dl, prompt);

        bool fired = false;
        coord.StartDownload("https://x", (_, _) => fired = true);
        dl.Downloads[0].SetException(new InvalidOperationException("download failed"));
        // Yield so the await continuation drains the exception path.
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
    public async Task StartDownload_SupersededDownloadCompletingLate_ShowsNoError()
    {
        // If an old download throws AFTER being superseded by a newer one, the old RunAsync's catch must skip ShowError (the user has moved on).
        var dl = new FakeUrlDownloader();
        var prompt = new FakeUrlPrompt();
        using var coord = new UrlLoadCoordinator(dl, prompt);

        coord.StartDownload("https://first", (_, _) => { });
        Assert.That(dl.Downloads, Has.Count.EqualTo(1));

        var firstTcs = dl.Downloads[0];
        // Supersede before firstTcs throws.
        coord.StartDownload("https://second", (_, _) => { });
        Assert.That(dl.Downloads, Has.Count.EqualTo(2));

        // First was already cancelled by Supersede; setting an exception is a no-op on a cancelled TCS, so use TrySetException to avoid throwing.
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
    public async Task Dispose_CancelsActiveDownload()
    {
        var dl = new FakeUrlDownloader();
        var prompt = new FakeUrlPrompt();
        var coord = new UrlLoadCoordinator(dl, prompt);

        bool fired = false;
        coord.StartDownload("https://x", (_, _) => fired = true);
        Assert.That(dl.Downloads, Has.Count.EqualTo(1));

        coord.Dispose();
        await Task.Yield();

        Assert.That(fired, Is.False);
        Assert.That(prompt.ProgressDisposed, Is.EqualTo(1));
    }
}
