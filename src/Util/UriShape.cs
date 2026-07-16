using System;

namespace Vomplayer.Util;

// The single URI-vs-local-path sniffer, shared by every layer that asks the question (CLI arg rooting, playlist-file parsing, track-preference/resume persistability). Previously three hand-synced copies whose comments cross-referenced each other "so they stay consistent".
//
// Rule: `scheme://` with the scheme starting at position > 0 and the "://" within the first 10 chars (http, https, file, smb, sftp, dvd, bd, rtsp, …), plus the explicit no-authority scheme `magnet:` (mpv-native, and the one no-slash scheme a video player plausibly receives). Bare Windows drive paths (`C:\foo`, `C:/foo`) survive because their colon isn't followed by `//`. A general RFC 3986 scheme parse (`alpha *(alnum|+|-|.) ":"`) was considered and rejected: it would misclassify relative filenames containing a colon (`foo:bar.mp4`) as URIs, breaking a working case to generalize a rare one — add further no-slash schemes here explicitly if they come up.
public static class UriShape
{
    public static bool LooksLikeUri(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        if (text.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        int colonSlashIdx = text.IndexOf("://", StringComparison.Ordinal);
        return colonSlashIdx > 0 && colonSlashIdx <= 10;
    }
}
