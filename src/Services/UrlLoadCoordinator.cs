using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Vomplayer.Services;

// Owns the full yt-dlp interaction surface for a video stream: interactive prompt + playlist-expansion probe (OpenUrlInteractiveAsync), URI-shape gating (ShouldProbe), and the race-guarded probe-then-route load lifecycle (StartUrlLoad). Created and held per-VideoContext.
//
// Contract: StartUrlLoad's onResolved fires only when the load is still the active one (cancellation token clear + the CTS hasn't been replaced by a newer StartUrlLoad). Callers must additionally verify their own still-current predicate inside the callback — e.g., the host's "is the playlist still on this URL?" — because the coordinator doesn't know about playlist state.
public sealed class UrlLoadCoordinator : IDisposable
{
    private readonly IUrlDownloader downloader;
    private readonly IUrlPrompt prompt;
    // CTS for the in-flight URL load (if any — covers both the classify probe and any subsequent download). StartUrlLoad cancels and replaces this synchronously, so a stale load A racing a new load B can't clobber B's playback. RunAsync re-checks this field is still its CTS after each await — defense in depth against the cancel-callback racing the onResolved invocation.
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

    // Interactive URL-open flow: gate on yt-dlp availability, prompt the user for a URL, run a 30s-timeout probe via yt-dlp's --flat-playlist, and surface any failure via ShowError. Returns the resolved entry list (one entry for a single video, many for a playlist URL) or null if the user cancelled, the probe failed, yt-dlp wasn't installed, or no entries came back. Caller drops the result into the playlist; each entry's load goes through ShouldProbe + StartUrlLoad in LoadCurrentItem, which then re-probes that entry's URL to decide between mpv-direct and yt-dlp download.
    public async Task<IReadOnlyList<string>?> OpenUrlInteractiveAsync()
    {
        // Gate before prompting. yt-dlp not available is a hard stop for this flow — there's no sensible fallback (mpv-direct doesn't handle YouTube), so the right UX is a clear "install yt-dlp" message rather than letting the user type a URL and *then* failing.
        if (!await downloader.IsAvailableAsync(CancellationToken.None))
        {
            ShowYtDlpMissingError();
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
            // Show the in-window busy overlay the instant the user submits the URL — the probe (and the classify that follows) can take several seconds, and the old flow left that whole window with no feedback. userCancelled distinguishes a Cancel-button click from the 30s timeout: both surface as an OperationCanceledException with probeCts.IsCancellationRequested, but only the timeout warrants an error dialog.
            bool userCancelled = false;
            var status = prompt.ShowUrlStatus("Fetching video info…", () => { userCancelled = true; TryCancel(probeCts); });
            try
            {
                entries = await downloader.ProbeAsync(url, probeCts.Token);
            }
            catch (OperationCanceledException) when (probeCts.IsCancellationRequested)
            {
                if (!userCancelled)
                {
                    prompt.ShowError("Failed to open URL", "Probe timed out after 30 seconds. The URL may be unreachable or the extractor may be hanging.");
                }
                return null;
            }
            catch (Exception ex)
            {
                prompt.ShowError("Failed to open URL", ex.Message);
                return null;
            }
            finally
            {
                status.Dispose();
            }
        }
        if (entries.Count == 0)
        {
            prompt.ShowError("Failed to open URL", "yt-dlp returned no entries for this URL.");
            return null;
        }
        return entries;
    }

    // Only http(s) URLs need yt-dlp's classification step. Local paths, mpv-native protocol URIs (smb://, sftp://, dvd://, bd://, rtsp://, rtmp://, ftp://, file://, magnet:, …) go straight to mpv: yt-dlp has no extractor for them, and mpv either handles them natively or surfaces its own "can't open" error. By classifying on URI shape rather than tracking "did this URL come from Open-URL", every entry-point — Open-URL prompt, drag-drop, command line, Recent-menu / autosave restore — sees the same routing decision.
    public bool ShouldProbe(string pathOrUri)
    {
        if (string.IsNullOrEmpty(pathOrUri))
        {
            return false;
        }
        return pathOrUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || pathOrUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
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

    // Kick off the full load for `url`: probe yt-dlp to classify (generic-extractor URLs route to mpv-direct, specific-extractor URLs route to a download), then either complete synchronously with the original URL or complete after a download with the local cache path. Fire-and-forget. onResolved fires with (url, loadablePath) — loadablePath is either the original URL (mpv-direct branch) or a /cache/... path (download branch). Cancellation is silent; non-cancel errors are surfaced via the prompt's ShowError.
    //
    // Threading: the coordinator's CTS-identity check and the caller's onResolved lambda must observe each other under the same synchronization context, since the host's residual predicate (e.g. "currentFilePath == url") can change between them. Today RunAsync's continuation lands on the GTK main thread (via the default sync context) and onResolved runs synchronously from there — no other code can interleave. A future refactor that pushes onResolved onto a different thread or queues it via Task.Run must revisit this contract.
    public void StartUrlLoad(string url, Action<string, string> onResolved)
    {
        if (url == null)
        {
            throw new ArgumentNullException(nameof(url));
        }
        if (onResolved == null)
        {
            throw new ArgumentNullException(nameof(onResolved));
        }
        // Defense-in-depth: the host's load-current path also calls CancelActive at its top to cover the local-file case (no StartUrlLoad follows). On the URL branch, both calls run; CancelActive is idempotent so the second one is a no-op. Removing this internal call would mean a caller that forgets to cancel first would leak the prior yt-dlp process until its natural completion.
        CancelActive();
        var cts = new CancellationTokenSource();
        activeCts = cts;
        // Fire-and-forget — exceptions are caught inside RunAsync and surfaced via the prompt's ShowError. Keeping the public entry-point synchronous matches the host's load-current path, which can't await.
        _ = RunAsync(url, cts, onResolved);
    }

    // The "yt-dlp absent" dialog, shared by the interactive prompt gate and the load-path gate so both surface byte-identical guidance.
    private void ShowYtDlpMissingError()
    {
        prompt.ShowError(
            "yt-dlp not found",
            "Install yt-dlp to play URLs (e.g. `pip install yt-dlp`, or your distribution's package manager). Once installed, retry without restarting Vomplayer.");
    }

    private async Task RunAsync(string url, CancellationTokenSource cts, Action<string, string> onResolved)
    {
        IUrlStatusHandle? status = null;
        try
        {
            // Same hard gate as the interactive OpenUrlInteractiveAsync flow. Without yt-dlp, ClassifyAsync would throw and we'd fall back to mpv-direct, which then can't resolve an extractor URL and fails silently — the user sees nothing happen. Gating here surfaces the identical "install yt-dlp" dialog for every non-interactive load (playlist row, drag-drop, command line, autosave restore). We can't tell a direct stream from an extractor URL without yt-dlp, so any http(s) URL that reaches here (ShouldProbe already filtered) is treated as needing it. Awaited before the status overlay (a missing binary shows the error dialog with no overlay flash) — and async at all so the probe's 2s worst case can't freeze the GTK main thread the way the old synchronous gate in StartUrlLoad did.
            if (!await downloader.IsAvailableAsync(cts.Token))
            {
                if (ReferenceEquals(activeCts, cts))
                {
                    ShowYtDlpMissingError();
                }
                return;
            }

            // Show the busy overlay before classification — the classify spawn is a second yt-dlp round-trip that was previously silent. Cancel is wired to this load's CTS so the user can abort through classify and download alike; on the interactive single-video path this replaces the "Fetching…" overlay OpenUrlInteractiveAsync just disposed. The hide/re-show land in separate main-loop turns (across the await in OpenUrlAsync), so there's usually no visible gap, but a one-frame flicker is possible — accepted in exchange for each method owning a self-contained, leak-proof overlay lifecycle.
            status = prompt.ShowUrlStatus("Preparing…", () => TryCancel(cts));

            // Classification step. StartUrlLoad already gated on IsAvailable(), so yt-dlp is present here — a failure is a classify glitch (network blip, extractor crash, an unusable --cookies-from-browser spec), not an absent binary. We still fall back to mpv-direct so a true direct stream keeps playing natively, but we report the failure first.
            //
            // Reporting is not optional, and what mpv does next doesn't change that. Whether mpv can rescue the URL depends on the libmpv it's linked against: a full build ships the ytdl_hook Lua script (the system libmpv on the primary dev platform does), our slim flatpak libmpv doesn't (see Playback.cs's `osc` note). But ytdl_hook shells out to the *same* yt-dlp binary we just failed on, so it only helps when the failure came from something we passed and it doesn't — i.e. the user's --cookies-from-browser spec. Every other cause (network blip, extractor breakage, an URL yt-dlp genuinely can't handle) fails there too, and nothing downstream turns that into a message: MpvClient documents that nothing consumes the end-file reason.
            //
            // So the two outcomes are "mpv fails too and the user would otherwise see nothing at all", or "mpv succeeds precisely because the cookie source is broken" — where the dialog is telling them something true and actionable that they'd otherwise never learn. Both want the report. The cost is a dialog in front of a video that then plays; that's the second case, and it's still the right call.
            //
            // Cancellation must propagate (the user clicked another row mid-probe).
            UrlLoadKind kind;
            try
            {
                kind = await downloader.ClassifyAsync(url, cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[vompl] yt-dlp classify failed for {url}: {ex.Message}; falling back to mpv-direct");
                prompt.ShowError("Failed to identify URL", ex.Message);
                kind = UrlLoadKind.MpvDirect;
            }

            // Race guard after the probe await — identical concern as the post-download guard.
            if (cts.Token.IsCancellationRequested || !ReferenceEquals(activeCts, cts))
            {
                return;
            }

            if (kind == UrlLoadKind.MpvDirect)
            {
                // Direct stream — mpv plays it natively, no download. The brief "Preparing…" busy flash is fine (and desirable feedback); the finally hides it.
                //
                // Known limitation: mpv fetches this itself and never sees the user's --cookies-from-browser setting, so an authenticated direct media link is classified *with* cookies and then played *without* them. Neither fix is worth it — forcing YtDlpDownload whenever cookies are configured would turn every direct 4 GB .mp4 from a stream into a full download, and exporting a cookie jar into mpv's HTTP headers is worse still. The preference's help text says cookies apply to videos yt-dlp downloads, not to direct stream links.
                onResolved(url, url);
                return;
            }

            // YtDlpDownload branch. The status handle's Progress drives the overlay's bar; ticks marshal to the GTK main thread via the Progress the overlay latched at Show time.
            string localPath = await downloader.DownloadAsync(url, status.Progress, cts.Token);

            // Race guard. The activeCts identity check catches the case where the user advanced to another URL during the download (a newer StartUrlLoad swapped in its own CTS). Token cancellation handles the user-clicked-Cancel path.
            if (cts.Token.IsCancellationRequested || !ReferenceEquals(activeCts, cts))
            {
                return;
            }
            onResolved(url, localPath);
        }
        catch (OperationCanceledException)
        {
            // User clicked Cancel, or a newer StartUrlLoad cancelled us. Either way, no playback to start.
        }
        catch (Exception ex)
        {
            // Show the dialog only if we're still the active load — a stale download's failure shouldn't pop up after the user has moved on. But never drop the record entirely: a superseded load's non-cancel failure still goes to stderr for diagnosability.
            if (ReferenceEquals(activeCts, cts))
            {
                prompt.ShowError("Download failed", ex.Message);
            }
            else
            {
                Console.Error.WriteLine($"[vompl] superseded URL load for {url} failed: {ex.Message}");
            }
        }
        finally
        {
            // Hide the status overlay on every exit (success, mpv-direct, cancel, supersede, error). Dispose is a no-op if a newer load already took the overlay over (generation guard in DownloadStatusOverlay), so a stale finish can't hide a live sibling's status.
            status?.Dispose();
            // Only clear the field if we're still its CTS — a newer StartUrlLoad may have already swapped in its own.
            if (ReferenceEquals(activeCts, cts))
            {
                activeCts = null;
            }
            cts.Dispose();
        }
    }

    // Cancel a CTS, tolerating a race where the VM already disposed it.
    private static void TryCancel(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        CancelActive();
    }
}
