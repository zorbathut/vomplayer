using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Vomplayer.Services;

// Owns the full yt-dlp interaction surface for a video stream: interactive prompt + probe (OpenUrlInteractiveAsync), the URL-routing set, and the race-guarded download lifecycle. Created and held per-VideoContext.
//
// Contract: StartDownload's onCompleted fires only when the load is still the active download (cancellation token clear + the CTS hasn't been replaced by a newer StartDownload). Callers must additionally verify their own still-current predicate inside the callback — e.g., the host's "is the playlist still on this URL?" — because the coordinator doesn't know about playlist state.
public sealed class UrlLoadCoordinator : IDisposable
{
    private readonly IUrlDownloader downloader;
    private readonly IUrlPrompt prompt;
    // URLs flagged for download routing (vs. handed straight to mpv). Populated from a successful OpenUrlInteractiveAsync probe; consulted by the host's load-current path via ShouldDownload.
    private readonly HashSet<string> urlsRequiringDownload = new();
    // CTS for the in-flight URL download (if any). StartDownload cancels and replaces this synchronously, so a stale download A racing a new load B can't clobber B's playback. RunAsync re-checks this field is still its CTS at completion time — defense in depth against the cancel-callback racing the onCompleted invocation.
    private CancellationTokenSource? activeCts;

    public UrlLoadCoordinator(IUrlDownloader downloader, IUrlPrompt prompt)
    {
        if (downloader == null)
        {
            throw new ArgumentNullException(nameof(downloader));
        }
        if (prompt == null)
        {
            throw new ArgumentNullException(nameof(prompt));
        }
        this.downloader = downloader;
        this.prompt = prompt;
    }

    // Interactive URL-open flow: gate on yt-dlp availability, prompt the user for a URL, run a 30s-timeout probe via yt-dlp's --flat-playlist, and surface any failure via ShowError. Returns the resolved entry list (one entry for a single video, many for a playlist URL) or null if the user cancelled, the probe failed, yt-dlp wasn't installed, or no entries came back. Caller routes the result via SetUrlsRequiringDownload + the playlist.
    public async Task<IReadOnlyList<string>?> OpenUrlInteractiveAsync()
    {
        // Gate before prompting. yt-dlp not available is a hard stop for this flow — there's no sensible fallback (mpv-direct doesn't handle YouTube), so the right UX is a clear "install yt-dlp" message rather than letting the user type a URL and *then* failing.
        if (!downloader.IsAvailable())
        {
            prompt.ShowError(
                "yt-dlp not found",
                "Install yt-dlp to play URLs (e.g. `pip install yt-dlp`, or your distribution's package manager). Once installed, retry without restarting Vomplayer.");
            return null;
        }
        var url = await prompt.PromptForUrlAsync("Open URL");
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }
        IReadOnlyList<string> entries;
        // Cap the probe at 30 seconds. yt-dlp's --flat-playlist usually returns in well under a second; a hung probe (network outage, extractor regression, mid-download server stall) shouldn't leave the UI stuck with no recovery. Cancellation tears down the spawned yt-dlp via YtDlpDownloader.ProbeAsync's existing kill-on-cancel path.
        using (var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            try
            {
                entries = await downloader.ProbeAsync(url, probeCts.Token);
            }
            catch (OperationCanceledException) when (probeCts.IsCancellationRequested)
            {
                prompt.ShowError("Failed to open URL", "Probe timed out after 30 seconds. The URL may be unreachable or the extractor may be hanging.");
                return null;
            }
            catch (Exception ex)
            {
                prompt.ShowError("Failed to open URL", ex.Message);
                return null;
            }
        }
        if (entries.Count == 0)
        {
            prompt.ShowError("Failed to open URL", "yt-dlp returned no entries for this URL.");
            return null;
        }
        return entries;
    }

    // Replace the URL-routing set with exactly the URLs from a fresh Open URL invocation. Bounds the set's size to the current playlist (post-probe) so it doesn't grow over the session, and prevents a previously-OpenUrl'd URL from re-entering the download path if the user later types it as a local file string.
    public void SetUrlsRequiringDownload(IEnumerable<string> urls)
    {
        if (urls == null)
        {
            throw new ArgumentNullException(nameof(urls));
        }
        urlsRequiringDownload.Clear();
        foreach (var u in urls)
        {
            urlsRequiringDownload.Add(u);
        }
    }

    public bool ShouldDownload(string pathOrUri)
    {
        return urlsRequiringDownload.Contains(pathOrUri);
    }

    // Cancel-and-clear the in-flight CTS, if any. Idempotent — RunAsync's `finally` also disposes its CTS, but a second Dispose is a no-op. Called from the host's load-current path (before swapping in the new file's slot) and from the host's playlist-replace branch in OpenUrl (where we don't go through load-current but still want to stop a stale yt-dlp).
    public void CancelActive()
    {
        var previousCts = activeCts;
        activeCts = null;
        if (previousCts != null)
        {
            try
            {
                previousCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            previousCts.Dispose();
        }
    }

    // Kick off a download for `url`. Fire-and-forget. onCompleted runs (with the original url + the resolved local path) only if the download succeeded AND the coordinator-internal race guards pass — the host should still verify its own "still current" predicate inside the callback before acting on the local path. Cancellation is silent; non-cancel errors are surfaced via the prompt's ShowError.
    //
    // Threading: the coordinator's CTS-identity check and the caller's onCompleted lambda must observe each other under the same synchronization context, since the host's residual predicate (e.g. "currentFilePath == url") can change between them. Today RunAsync's continuation lands on the GTK main thread (via the default sync context) and onCompleted runs synchronously from there — no other code can interleave. A future refactor that pushes onCompleted onto a different thread or queues it via Task.Run must revisit this contract.
    public void StartDownload(string url, Action<string, string> onCompleted)
    {
        if (url == null)
        {
            throw new ArgumentNullException(nameof(url));
        }
        if (onCompleted == null)
        {
            throw new ArgumentNullException(nameof(onCompleted));
        }
        // Defense-in-depth: the host's load-current path also calls CancelActive at its top to cover the local-file case (no StartDownload follows). On the URL branch, both calls run; CancelActive is idempotent so the second one is a no-op. Removing this internal call would mean a caller that forgets to cancel first would leak the prior yt-dlp process until its natural completion.
        CancelActive();
        var cts = new CancellationTokenSource();
        activeCts = cts;
        // Fire-and-forget — exceptions are caught inside RunAsync and surfaced via the prompt's ShowError. Keeping the public entry-point synchronous matches the host's load-current path, which can't await.
        _ = RunAsync(url, cts, onCompleted);
    }

    private async Task RunAsync(string url, CancellationTokenSource cts, Action<string, string> onCompleted)
    {
        UrlProgressHandle? progressHandle = null;
        try
        {
            progressHandle = prompt.ShowDownloadProgress("Downloading", cts);
            string localPath = await downloader.DownloadAsync(url, progressHandle.Progress, cts.Token);

            // Race guard. The activeCts identity check catches the case where the user advanced to another URL during the download (a newer StartDownload swapped in its own CTS). Token cancellation handles the user-clicked-Cancel path.
            if (cts.Token.IsCancellationRequested || !ReferenceEquals(activeCts, cts))
            {
                return;
            }
            onCompleted(url, localPath);
        }
        catch (OperationCanceledException)
        {
            // User clicked Cancel, or a newer StartDownload cancelled us. Either way, no playback to start.
        }
        catch (Exception ex)
        {
            // Show only if we're still the active load — a stale download's failure shouldn't pop up after the user has moved on.
            if (ReferenceEquals(activeCts, cts))
            {
                prompt.ShowError("Download failed", ex.Message);
            }
        }
        finally
        {
            progressHandle?.Closer.Dispose();
            // Only clear the field if we're still its CTS — a newer StartDownload may have already swapped in its own.
            if (ReferenceEquals(activeCts, cts))
            {
                activeCts = null;
            }
            cts.Dispose();
        }
    }

    public void Dispose()
    {
        CancelActive();
    }
}
