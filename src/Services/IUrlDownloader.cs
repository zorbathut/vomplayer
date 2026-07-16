using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Vomplayer.Services;

// One progress tick from yt-dlp. DownloadedBytes is always known once the download starts; TotalBytes can be null for live / unknown-size streams (yt-dlp emits "NA" in those cases). Status is the raw status field from yt-dlp ("downloading", "finished", "error") so the UI can flip between indeterminate and determinate states.
public sealed record UrlDownloadProgress(long DownloadedBytes, long? TotalBytes, string Status);

// How a URL should be loaded after classification. MpvDirect = hand the URL straight to mpv (direct stream — .mp4/.m3u8/.flac/etc., no extractor needed). YtDlpDownload = run yt-dlp to download to a local cache file, then hand the file to mpv (extractor-required sites — YouTube, Twitch, Vimeo, etc.).
public enum UrlLoadKind
{
    MpvDirect,
    YtDlpDownload,
}

// Interface so the VM can be unit-tested without a real yt-dlp binary on PATH. IsAvailableAsync gates every URL flow before the user is asked for anything (no point prompting for a URL we can't fulfill); it's async because the probe spawns a process, and its worst case — yt-dlp missing, re-probed on every call — used to block the GTK main thread for the full 2s timeout. ProbeAsync returns the list of contained URLs — for a single video, one entry; for a playlist, all entries. ClassifyAsync asks yt-dlp which extractor matches the URL so we can decide whether to download or stream-via-mpv. DownloadAsync downloads one URL and returns the local file path.
public interface IUrlDownloader
{
    Task<bool> IsAvailableAsync(CancellationToken ct);

    Task<IReadOnlyList<string>> ProbeAsync(string url, CancellationToken ct);

    Task<UrlLoadKind> ClassifyAsync(string url, CancellationToken ct);

    Task<string> DownloadAsync(string url, IProgress<UrlDownloadProgress>? progress, CancellationToken ct);
}
