using System;

namespace Vomplayer.Util;

// The single URI-vs-local-path sniffer, shared by every layer that asks the question (CLI arg rooting, playlist-file parsing, path normalization, saved-playlist canonicalization, playlist-row display). Every one of those layers calls this — don't answer the question with a local `Contains("://")`, which is how the copies this replaced drifted apart.
//
// Rule: an RFC 3986 scheme of two or more characters followed by `://` (http, https, file, smb, sftp, dvd, bd, rtsp, …), plus the explicit no-authority scheme `magnet:` (mpv-native, and the one no-slash scheme a video player plausibly receives). The two-character minimum is what keeps a Windows drive letter from reading as a scheme in the doubled-separator spelling (`C://Users`, which Windows accepts); no real scheme is one character. A local path that merely contains the separator (`/tmp/x://y.mkv`) is a path because its leading segment isn't a scheme.
//
// The `://` is required. A general RFC 3986 parse of the no-slash form (`alpha *(alnum|+|-|.) ":"`) was considered and rejected: it would misclassify relative filenames containing a colon (`foo:bar.mp4`) as URIs, breaking a working case to generalize a rare one — add further no-slash schemes here explicitly if they come up.
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
        return colonSlashIdx > 1 && IsScheme(text.AsSpan(0, colonSlashIdx));
    }

    // RFC 3986: ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ).
    private static bool IsScheme(ReadOnlySpan<char> candidate)
    {
        if (!char.IsAsciiLetter(candidate[0]))
        {
            return false;
        }
        foreach (char c in candidate[1..])
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '+' && c != '-' && c != '.')
            {
                return false;
            }
        }
        return true;
    }

    // A file: URI whose authority is empty or "localhost" (RFC 8089 treats them alike, as does g_filename_from_uri) converts to the percent-decoded local path. A URI naming any other authority (file://nas/share/x) stays a URI — it names a file that isn't here.
    public static bool TryLocalPathFromFileUri(string text, out string path)
    {
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.IsFile && (string.IsNullOrEmpty(uri.Host) || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)))
        {
            path = Uri.UnescapeDataString(uri.AbsolutePath);
            return true;
        }
        path = string.Empty;
        return false;
    }
}
