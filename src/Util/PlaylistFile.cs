using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Vomplayer.Util;

// Pure serializer/parser for the explicit "Open/Save Playlist" feature's newline-delimited textfile
// format ("nothing fancy" — one path-or-URI per line, no EXTINF metadata). Lives here as a GTK/mpv-
// free leaf so the format logic is unit-testable; the GTK file-dialog / clipboard / disk-I/O glue
// lives in MainWindow (mirroring the drag-and-drop handlers).
//
// The line tokenizer (split '\n' → TrimEnd('\r').Trim() → skip blank / '#') parallels
// UriListDropTarget.ParseAndConvert, but the two diverge in interpretation: the drop path converts
// file:// URIs via Uri.LocalPath and recursively expands dropped directories with no relative
// resolution, whereas this path resolves relative entries against the playlist file's directory and
// does neither. Kept as a separate self-contained leaf rather than coupling the Wayland-sensitive
// drop path to the playlist format for a few lines of shared tokenization.
public static class PlaylistFile
{
    // One item per line, '\n'-separated, with a trailing newline. Items are written verbatim —
    // Playlist.Items are already absolute local paths or full URIs. LF only (not CRLF); Parse
    // tolerates either on the way back in.
    public static string Serialize(IReadOnlyList<string> items)
    {
        if (items == null)
        {
            throw new ArgumentNullException(nameof(items));
        }
        var sb = new StringBuilder();
        foreach (var item in items)
        {
            sb.Append(item);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // Parse newline-delimited content into playlist entries. Blank lines and '#'-prefixed lines are
    // skipped (so a real .m3u's #EXTM3U/#EXTINF directives are ignored rather than imported as bogus
    // paths). A line carrying a "scheme://" prefix is treated as a URI and kept verbatim; an absolute
    // local path is kept verbatim; a relative local path is resolved against baseDirectory when one is
    // supplied (the File-open case) and left untouched when it's null (the Clipboard case, which has
    // no anchoring directory). A pathological relative line can make Path.GetFullPath throw (invalid
    // chars — effectively only NUL on Linux); we let that propagate so the caller surfaces it rather
    // than silently dropping the entry (CLAUDE.md bans silent error handling).
    public static List<string> Parse(string contents, string? baseDirectory)
    {
        var items = new List<string>();
        if (string.IsNullOrEmpty(contents))
        {
            return items;
        }
        foreach (var rawLine in contents.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            if (baseDirectory != null && !LooksLikeUri(line) && !Path.IsPathRooted(line))
            {
                items.Add(Path.GetFullPath(Path.Combine(baseDirectory, line)));
            }
            else
            {
                items.Add(line);
            }
        }
        return items;
    }

    // "scheme://" sniff, mirroring TrackPreferences.IsLocalFilesystemPath's rule: a "://" starting
    // after position 0 and within the first 10 chars marks a URI scheme (http, https, file, smb, …).
    // Checked BEFORE Path.IsPathRooted because a "file:///x" URI is not a rooted path and would
    // otherwise be (wrongly) joined onto baseDirectory.
    private static bool LooksLikeUri(string line)
    {
        int idx = line.IndexOf("://", StringComparison.Ordinal);
        return idx > 0 && idx <= 10;
    }
}
