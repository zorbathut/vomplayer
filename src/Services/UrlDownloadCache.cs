using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Vomplayer.Services;

// Per-URL on-disk cache for yt-dlp downloads. Each URL maps to its own subdirectory keyed by SHA-256(url) — stable across runs, so the same URL re-opened later re-uses the cached file (deduplication). A `manifest.txt` written inside the subdir on successful download names the actual media file; presence of manifest = "this download completed". Without it (interrupted download, fresh dir), TryGetExistingFile returns null and the caller re-downloads.
//
// Cleanup: every directory whose mtime is older than MaxAgeHours is removed wholesale on Cleanup(). mtime not atime because atime is unreliable (noatime is common on Linux roots, and Windows updates atime lazily). Touch() bumps the directory mtime whenever a cache hit is satisfied, so "accessed" semantics hold up to filesystem-mtime granularity (~1s, fine for a 24h window).
public sealed class UrlDownloadCache
{
    public const int MaxAgeHours = 24;
    private const string ManifestFileName = "manifest.txt";

    private readonly string root;

    public UrlDownloadCache(string root)
    {
        if (string.IsNullOrEmpty(root))
        {
            throw new ArgumentException("root must be non-empty", nameof(root));
        }
        this.root = root;
    }

    public string Root
    {
        get { return root; }
    }

    // Directory for a given URL. Idempotent — repeated calls return the same path. Doesn't create on disk; that's DirectoryFor or the caller's responsibility.
    public string KeyFor(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            throw new ArgumentException("url must be non-empty", nameof(url));
        }
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(url), hash);
        // 16 hex chars (8 bytes) is plenty — collision probability for ~thousands of URLs is negligible. Lowercase hex matches conventional file-system style. Full 64-char hex would push us toward filesystem name-length limits when combined with /tmp prefixes.
        var sb = new StringBuilder(16);
        for (int i = 0; i < 8; i++)
        {
            sb.Append(hash[i].ToString("x2"));
        }
        return sb.ToString();
    }

    public string DirectoryFor(string url)
    {
        return Path.Combine(root, KeyFor(url));
    }

    // Returns the local file path for `url` if a completed download exists, else null. On a cache hit, also bumps mtime so the cleanup pass treats this entry as freshly-used. The manifest contains the file name relative to the per-URL directory; we resolve it against DirectoryFor and verify the file actually exists (defends against a manual rm of just the media file).
    public string? TryGetExistingFile(string url)
    {
        var dir = DirectoryFor(url);
        var manifest = Path.Combine(dir, ManifestFileName);
        if (!File.Exists(manifest))
        {
            return null;
        }
        string relative;
        try
        {
            relative = File.ReadAllText(manifest).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[vompl] url-cache: read manifest {manifest} failed: {ex.Message}; treating as cache miss");
            return null;
        }
        if (string.IsNullOrEmpty(relative))
        {
            return null;
        }
        var fullPath = Path.Combine(dir, relative);
        if (!File.Exists(fullPath))
        {
            return null;
        }
        Touch(dir);
        return fullPath;
    }

    // Caller invokes after a successful yt-dlp run to record which file in the per-URL directory is the playable result. relativeFileName is just the file name (no leading directory) — yt-dlp outputs into DirectoryFor(url) so all paths are relative to it.
    public void RecordDownload(string url, string relativeFileName)
    {
        if (string.IsNullOrEmpty(relativeFileName))
        {
            throw new ArgumentException("relativeFileName must be non-empty", nameof(relativeFileName));
        }
        var dir = DirectoryFor(url);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ManifestFileName), relativeFileName);
        Touch(dir);
    }

    public void EnsureDirectoryExists(string url)
    {
        Directory.CreateDirectory(DirectoryFor(url));
    }

    // Walks the cache root and deletes every per-URL subdirectory whose last-write time is older than the cutoff. now is injected so tests can drive the clock; production callers pass DateTimeOffset.UtcNow. Errors deleting an individual subdir (file in use, permission denied) are reported through onError but don't abort the sweep — the next Cleanup will retry.
    public void Cleanup(DateTimeOffset now, Action<string>? onError)
    {
        if (!Directory.Exists(root))
        {
            return;
        }
        var cutoff = now.UtcDateTime.AddHours(-MaxAgeHours);
        IEnumerable<string> subdirs;
        try
        {
            subdirs = Directory.EnumerateDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onError?.Invoke($"cache cleanup: enumerate failed for {root}: {ex.Message}");
            return;
        }
        foreach (var dir in subdirs)
        {
            DateTime mtime;
            try
            {
                mtime = Directory.GetLastWriteTimeUtc(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                onError?.Invoke($"cache cleanup: stat failed for {dir}: {ex.Message}");
                continue;
            }
            if (mtime < cutoff)
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (Exception ex)
                {
                    onError?.Invoke($"cache cleanup: delete failed for {dir}: {ex.Message}");
                }
            }
        }
    }

    // Bump mtime so the cleanup pass treats this directory as freshly used. Touching the directory itself is enough — Cleanup looks at directory mtime, not the contained files.
    private static void Touch(string dir)
    {
        try
        {
            Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow);
        }
        catch (IOException)
        {
            // Touching is best-effort — if it fails, the worst case is the entry gets cleaned up earlier than expected. Don't disrupt the play path for it. Intentionally silent (not "swallowed"): a read-only cache directory would otherwise log on every URL play, which is pure noise — the failure is by design tolerable. If a real bug ever needs to be diagnosed here, replace this with throw + handle at the caller, not blanket logging.
        }
    }
}
