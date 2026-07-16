using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Vomplayer.Services;

// IUrlDownloader backed by an external yt-dlp binary. Dedup via UrlDownloadCache. Progress is parsed from yt-dlp's stdout — we use --progress-template to emit a stable "VOMPLPROG <downloaded> <total> <status>" prefix that's trivial to split on whitespace, sidestepping yt-dlp's default human-readable lines that change format between versions. --newline forces one update per line (default is carriage-return-rewrite which won't survive line-buffered redirection).
//
// The binary is supplied as a command list (see BuildCommand): bare ["yt-dlp"] on a normal host, or ["flatpak-spawn", "--host", "--watch-bus", "yt-dlp"] inside a flatpak sandbox, where the host's yt-dlp is reached through the Flatpak portal because the sandbox PATH doesn't see it. Every invocation goes through NewStartInfo so the executable + arg prefix are applied in exactly one place.
//
// IsAvailableAsync() shells `<command> --version`. Cached after the first call because subsequent UI gates would otherwise spawn the process repeatedly per click. The cache stays valid for the process lifetime; if the user installs yt-dlp mid-session they'll need to restart, but that's an extreme edge case.
public sealed class YtDlpDownloader : IUrlDownloader
{
    private const string ProgressPrefix = "VOMPLPROG";
    private const string FilenamePrefix = "VOMPLFILE";

    private readonly UrlDownloadCache cache;
    private readonly string executable;
    private readonly IReadOnlyList<string> argPrefix;
    // We cache only the *positive* IsAvailable result. A negative result is re-probed on every call so a user who installs yt-dlp mid-session ("oh, it's not on PATH? hold on, brew install yt-dlp; ok, click Open URL again") gets through without restarting the app.
    private bool isAvailableCachedTrue;

    // command[0] is the executable; command[1..] is a fixed arg prefix prepended to every invocation. See BuildCommand.
    public YtDlpDownloader(UrlDownloadCache cache, IReadOnlyList<string> command)
    {
        if (cache == null)
        {
            throw new ArgumentNullException(nameof(cache));
        }
        if (command == null || command.Count == 0)
        {
            throw new ArgumentException("command must be non-empty", nameof(command));
        }
        this.cache = cache;
        this.executable = command[0];
        this.argPrefix = command.Skip(1).ToArray();
    }

    // Resolve the yt-dlp invocation command for the current environment. Pure so it's unit-testable; the environment probe lives in FlatpakDetect.IsSandboxed.
    public static IReadOnlyList<string> BuildCommand(bool inFlatpak)
    {
        // Inside a flatpak sandbox the host's yt-dlp is reached via flatpak-spawn --host.
        // --watch-bus binds the host process to flatpak-spawn's D-Bus connection: when we
        // Kill() the in-sandbox flatpak-spawn, the kernel closes its FDs (incl. the bus
        // socket) and the portal tears down the host yt-dlp. So a cancelled download leaves
        // no orphan, and neither does an app crash. (flatpak-spawn forwards no signals; the
        // bus-drop is what does the work — see flatpak/flatpak#4827.)
        if (inFlatpak)
        {
            return new[] { "flatpak-spawn", "--host", "--watch-bus", "yt-dlp" };
        }
        return new[] { "yt-dlp" };
    }

    // Build a ProcessStartInfo for a yt-dlp invocation: the resolved executable, the shared redirect/no-window flags every call needs, and the fixed arg prefix. Callers add only their own args afterward, so the prefix is applied in exactly one place.
    private ProcessStartInfo NewStartInfo()
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in argPrefix)
        {
            psi.ArgumentList.Add(arg);
        }
        return psi;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        if (isAvailableCachedTrue)
        {
            return true;
        }
        try
        {
            var psi = NewStartInfo();
            psi.ArgumentList.Add("--version");
            using var probe = Process.Start(psi) ?? throw new InvalidOperationException("yt-dlp failed to start");
            // 2s is generous — `yt-dlp --version` reads no network and just prints a string. Awaited (not WaitForExit-blocked) because this runs on the GTK main thread.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(2000);
            try
            {
                await probe.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!probe.HasExited)
                {
                    probe.Kill(entireProcessTree: true);
                }
                if (ct.IsCancellationRequested)
                {
                    throw;
                }
                return false;
            }
            if (probe.ExitCode == 0)
            {
                isAvailableCachedTrue = true;
                return true;
            }
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // ENOENT / permission / any other launch failure — yt-dlp isn't usable. We don't distinguish causes here; the UI gate just needs a yes/no, and the user will get the "not available" path which gives them install instructions. Log to stderr so a config issue (yt-dlp installed but unable to launch) is diagnosable instead of silently surfaced as "not installed".
            Console.Error.WriteLine($"[vompl] yt-dlp probe failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // Run yt-dlp with --flat-playlist to extract URLs without downloading. Returns one entry for a single video, or N entries for a playlist. yt-dlp's default behavior on a single video is to print the same URL back, so the caller doesn't have to special-case "is this a playlist" — they just check Count.
    //
    // --no-playlist makes yt-dlp itself disambiguate the "video URL with a playlist parameter" case: a `watch?v=X&list=Y` URL resolves to just video X (because there's an unambiguous single-video target on the page), while a pure `/playlist?list=Y` URL still expands to all entries (the flag is a no-op when there's no single-video to fall back to). This matches the user-intent of "URL that points at a specific video plays just that video; URL that points at a playlist plays the playlist", driven by yt-dlp's own URL classification rather than a regex on our side.
    public async Task<IReadOnlyList<string>> ProbeAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(url))
        {
            throw new ArgumentException("url must be non-empty", nameof(url));
        }
        var psi = NewStartInfo();
        psi.ArgumentList.Add("--flat-playlist");
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("%(webpage_url)s");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(url);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("yt-dlp failed to start");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (Exception killEx)
            {
                Console.Error.WriteLine($"[vompl] yt-dlp probe: Kill failed: {killEx.Message}");
            }
            // Drain the read tasks before throwing so they don't surface as unobserved task exceptions on the finalizer queue. ReadToEndAsync(ct) will have completed (faulted with OCE or finished cleanly when the killed process closed its streams) — either outcome is fine; we just need to await.
            try { await stdoutTask; } catch (OperationCanceledException) { } catch (Exception ex) { Console.Error.WriteLine($"[vompl] yt-dlp probe: stdout drain failed: {ex.Message}"); }
            try { await stderrTask; } catch (OperationCanceledException) { } catch (Exception ex) { Console.Error.WriteLine($"[vompl] yt-dlp probe: stderr drain failed: {ex.Message}"); }
            throw;
        }
        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"yt-dlp probe failed (exit {proc.ExitCode}): {stderr.Trim()}");
        }
        var urls = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
        if (urls.Count == 0)
        {
            // yt-dlp accepted the URL but produced no entries — treat as "play this URL directly" so the user isn't left with an empty playlist. The single entry path will then run the actual download.
            urls.Add(url);
        }
        return urls;
    }

    // Ask yt-dlp which extractor matches `url`. Used by the load path to distinguish "yt-dlp's generic extractor (the URL is a direct media link mpv can stream)" from "a specific extractor (YouTube, Twitch, …) where mpv-direct fails and we want to download instead." --flat-playlist keeps the call cheap — yt-dlp pattern-matches the URL without fetching video metadata. Exit-nonzero / process-start-fail throws; the caller treats that as "fall back to mpv-direct" rather than refusing to load.
    public async Task<UrlLoadKind> ClassifyAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(url))
        {
            throw new ArgumentException("url must be non-empty", nameof(url));
        }
        var psi = NewStartInfo();
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--flat-playlist");
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("%(extractor)s");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(url);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("yt-dlp failed to start");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (Exception killEx)
            {
                Console.Error.WriteLine($"[vompl] yt-dlp classify: Kill failed: {killEx.Message}");
            }
            try { await stdoutTask; } catch (OperationCanceledException) { } catch (Exception ex) { Console.Error.WriteLine($"[vompl] yt-dlp classify: stdout drain failed: {ex.Message}"); }
            try { await stderrTask; } catch (OperationCanceledException) { } catch (Exception ex) { Console.Error.WriteLine($"[vompl] yt-dlp classify: stderr drain failed: {ex.Message}"); }
            throw;
        }
        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"yt-dlp classify failed (exit {proc.ExitCode}): {stderr.Trim()}");
        }
        // yt-dlp prints one extractor line per entry; for --no-playlist + a single-video URL there's exactly one. Take the first non-empty line and compare to "generic" case-insensitively. Anything else means yt-dlp has a specific extractor for the URL (YouTube, Twitch, Vimeo, …), so we want to download.
        var firstLine = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0);
        if (firstLine != null && string.Equals(firstLine, "generic", StringComparison.OrdinalIgnoreCase))
        {
            return UrlLoadKind.MpvDirect;
        }
        return UrlLoadKind.YtDlpDownload;
    }

    // Download the URL, reporting progress through the IProgress callback. Cache hit short-circuits without spawning yt-dlp. On miss, yt-dlp runs to completion and we return the local path. Cancellation kills the process tree synchronously.
    public async Task<string> DownloadAsync(string url, IProgress<UrlDownloadProgress>? progress, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(url))
        {
            throw new ArgumentException("url must be non-empty", nameof(url));
        }
        // Sweep stale entries opportunistically. Cleanup is also called at app startup, but a long-running session never sees the startup pass; doing it on every download keeps disk usage bounded for users who keep the app open for days. Cheap (one stat per cached entry).
        cache.Cleanup(DateTimeOffset.UtcNow, msg => Console.Error.WriteLine($"[vompl] {msg}"));

        var existing = cache.TryGetExistingFile(url);
        if (existing != null)
        {
            // Synthesize a single 100%-complete progress event so the UI dialog can dismiss itself instead of staring at 0%. DownloadedBytes is unknown for a cache hit (we'd have to stat) — pass 0/0/finished and let the UI render "complete" rather than "loading".
            progress?.Report(new UrlDownloadProgress(0, 0, "finished"));
            return existing;
        }

        var targetDir = cache.DirectoryFor(url);
        // Wipe any prior debris (orphan .part files, half-merged .f137.mp4 / .f140.m4a streams from a yt-dlp crash, manifest-less .ytdl resume state). Without this, FindMediaFileIn could pick up a half-product if the after_move print is missed. The TryGetExistingFile call above already verified there's no completed download to preserve.
        if (Directory.Exists(targetDir))
        {
            try
            {
                Directory.Delete(targetDir, recursive: true);
            }
            catch (IOException ex)
            {
                // Don't bail — yt-dlp may overwrite individual files via its own logic. Log and continue; if that produces a bad merge, the resulting download will fail visibly rather than silently.
                Console.Error.WriteLine($"[vompl] yt-dlp: pre-download wipe of {targetDir} failed: {ex.Message}");
            }
        }
        cache.EnsureDirectoryExists(url);

        var psi = NewStartInfo();
        // --newline: emit each progress update on its own line (default rewrites in place via \r). Required for stdout line-by-line parsing.
        // --progress-template: stable machine-parseable format. We split on whitespace and read fields by index.
        // --print after_move:filepath: emit the final-file path AFTER all post-processing (merge of separate video+audio streams, format conversion, etc.) so we know exactly which file mpv should open.
        // --no-quiet: --print implicitly enables --quiet, which suppresses *all* other output including the progress lines we depend on. Restore the default verbosity so progress-template lines land on stdout.
        // -P: place all output (incl. .part files) inside the per-URL cache subdir. -o template ensures a single canonical file name; %(ext)s lets yt-dlp pick the actual extension. targetDir is absolute. In the flatpak case a host-spawned yt-dlp writes via this path; it lands where the sandbox reads only because the cache lives under $XDG_DATA_HOME (~/.var/app/<id>/data), a real host path visible at the same absolute location on both sides. Moving the cache to a path-virtualized location (e.g. /tmp, or a --filesystem-mapped dir the host sees elsewhere) would break that.
        // --no-warnings + --no-progress in stderr keeps stderr clean for actual error output.
        psi.ArgumentList.Add("--newline");
        psi.ArgumentList.Add("--progress-template");
        psi.ArgumentList.Add($"{ProgressPrefix} %(progress.downloaded_bytes)s %(progress.total_bytes)s %(progress.total_bytes_estimate)s %(progress.status)s");
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add($"after_move:{FilenamePrefix} %(filepath)s");
        psi.ArgumentList.Add("--no-quiet");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("-P");
        psi.ArgumentList.Add(targetDir);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("%(title)s.%(ext)s");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(url);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("yt-dlp failed to start");

        string? finalPath = null;
        var stderr = new System.Text.StringBuilder();

        // Read stdout line-by-line so progress events drive the UI in near-real-time. Reading both streams concurrently — stderr buffering is small enough that a hung reader can deadlock the child if its stderr fills.
        var stdoutTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(ct)) != null)
            {
                if (line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
                {
                    if (TryParseProgress(line, out var p))
                    {
                        progress?.Report(p);
                    }
                }
                else if (line.StartsWith(FilenamePrefix, StringComparison.Ordinal))
                {
                    finalPath = line.Substring(FilenamePrefix.Length).Trim();
                }
            }
        }, ct);
        var stderrTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync(ct)) != null)
            {
                stderr.AppendLine(line);
            }
        }, ct);

        try
        {
            await proc.WaitForExitAsync(ct);
            await stdoutTask;
            await stderrTask;
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (Exception killEx)
            {
                Console.Error.WriteLine($"[vompl] yt-dlp download: Kill failed: {killEx.Message}");
            }
            // Drain the reader tasks. They were started with `Task.Run(..., ct)` so a cancellation throws OCE inside; without these awaits, the OCE becomes an unobserved task exception. We don't care about exceptions here other than to silence the unobserved-task channel — Console.Error captures non-OCE failures so a real I/O bug surfaces in logs.
            try { await stdoutTask; } catch (OperationCanceledException) { } catch (Exception ex) { Console.Error.WriteLine($"[vompl] yt-dlp download: stdout drain failed: {ex.Message}"); }
            try { await stderrTask; } catch (OperationCanceledException) { } catch (Exception ex) { Console.Error.WriteLine($"[vompl] yt-dlp download: stderr drain failed: {ex.Message}"); }
            throw;
        }

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"yt-dlp failed (exit {proc.ExitCode}): {stderr.ToString().Trim()}");
        }
        if (string.IsNullOrEmpty(finalPath))
        {
            // yt-dlp succeeded but never printed an after_move filepath — fall back to scanning the target directory. Worst case, a user dropping in to inspect cache/<key>/ should still find their file.
            finalPath = FindMediaFileIn(targetDir);
            if (string.IsNullOrEmpty(finalPath))
            {
                throw new InvalidOperationException($"yt-dlp completed but produced no playable file in {targetDir}");
            }
        }
        if (!Path.IsPathRooted(finalPath))
        {
            // yt-dlp's filepath is normally absolute (since we passed -P). Defense-in-depth for a future yt-dlp behavior change.
            finalPath = Path.Combine(targetDir, finalPath);
        }
        var relative = Path.GetRelativePath(targetDir, finalPath);
        cache.RecordDownload(url, relative);
        return finalPath;
    }

    internal static bool TryParseProgress(string line, out UrlDownloadProgress progress)
    {
        progress = default!;
        // Format: "VOMPLPROG <downloaded> <total> <total_estimate> <status>". yt-dlp prints "NA" for unknown numeric fields; we map that to null/0 as appropriate. total_bytes is NA for HLS/DASH downloads, where yt-dlp puts the usable figure in total_bytes_estimate instead — prefer the exact value, fall back to the estimate so those downloads get a determinate bar. The estimate is emitted as a float ("12345.0"), hence the double parse.
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5 || parts[0] != ProgressPrefix)
        {
            return false;
        }
        if (!long.TryParse(parts[1], out long downloaded))
        {
            downloaded = 0;
        }
        long? total = null;
        if (long.TryParse(parts[2], out long totalParsed))
        {
            total = totalParsed;
        }
        else if (double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double estimate) && estimate > 0)
        {
            total = (long)estimate;
        }
        string status = parts[4];
        progress = new UrlDownloadProgress(downloaded, total, status);
        return true;
    }

    private static string? FindMediaFileIn(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return null;
        }
        // Pick the largest file that isn't a yt-dlp .part / .ytdl sidecar. The merged final file is reliably the largest result by a wide margin; sidecars are KB-scale.
        string? best = null;
        long bestLen = -1;
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            if (f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var len = new FileInfo(f).Length;
            if (len > bestLen)
            {
                bestLen = len;
                best = f;
            }
        }
        return best;
    }
}
