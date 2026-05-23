using System;
using System.Collections.Generic;
using System.IO;

namespace Vomplayer.Util;

// Shared extension lists for the file picker, drag-and-drop directory traversal, and any future "is this a video?" check. Originally lived in FilePickerGtk; promoted here so MainWindow's drop handler can filter directory contents to known video formats without reaching into the picker. KISS: extension match is "what counts as a video" for our purposes — libmpv plays much more than this list, so direct file drops are NOT filtered (the user explicitly chose them). The filter only fires when expanding a dropped directory's contents.
public static class MediaExtensions
{
    public static readonly string[] Video =
    {
        "mp4", "mkv", "webm", "mov", "avi", "m4v", "ts", "mpg", "mpeg", "wmv", "flv",
    };

    public static readonly string[] Audio =
    {
        "mp3", "flac", "wav", "ogg", "oga", "opus", "m4a", "aac", "ac3", "dts", "wma", "mka",
    };

    public static readonly string[] Subtitle =
    {
        "srt", "ass", "ssa", "vtt", "sub", "idx", "sup", "smi", "mks",
    };

    // Case-insensitive extension match against the Video list. Returns false for paths with no extension. Path.GetExtension returns the extension WITH the leading dot (".mp4") or the empty string; we strip the dot before comparing against the bare-extension list.
    public static bool IsVideoFile(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext) || ext.Length < 2)
        {
            return false;
        }
        var bare = ext[1..];
        foreach (var v in Video)
        {
            if (string.Equals(v, bare, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // Drop-handler helper. Walks a list of dropped paths, replacing any local-directory entries with their recursive video-file contents (extension-filtered via IsVideoFile). Direct file entries pass through unfiltered (the user's explicit drop is authoritative — we don't second-guess by extension); URIs (anything with a scheme://) skip the directory check entirely. Errors from individual subtree walks (permission denied, deleted mid-walk, etc.) are reported via onError and the offending entry is dropped, but other entries continue to expand — one unreadable subtree shouldn't reject the whole drop.
    //
    // Output is sorted lexicographically (case-insensitive, ordinal) across the whole result, NOT preserving the per-input grouping. Both folder expansion (where Directory.EnumerateFiles returns filesystem-order, which is unspecified) and multi-file drops (where the file manager's ordering may or may not survive the GTK drag pipeline) need this for predictable playback order — a user dropping a folder of "S01E01.mkv … S01E12.mkv" expects to play in episode order, not in inode order. Case-insensitive matches typical file-manager presentation; ordinal-not-cultural keeps the result independent of locale.
    public static List<string> ExpandPaths(IReadOnlyList<string> paths, Action<string> onError)
    {
        if (paths == null)
        {
            throw new ArgumentNullException(nameof(paths));
        }
        if (onError == null)
        {
            throw new ArgumentNullException(nameof(onError));
        }
        var expanded = new List<string>(paths.Count);
        foreach (var entry in paths)
        {
            if (Directory.Exists(entry))
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories))
                    {
                        if (IsVideoFile(f))
                        {
                            expanded.Add(f);
                        }
                    }
                }
                catch (Exception ex)
                {
                    onError($"directory traversal failed for '{entry}': {ex.Message}");
                }
            }
            else
            {
                expanded.Add(entry);
            }
        }
        expanded.Sort(StringComparer.OrdinalIgnoreCase);
        return expanded;
    }

    // Pure neighbor selection used by the previous/next-track buttons when they walk off the end of the playlist. Given the video files in a directory, returns the one immediately after (direction > 0) or before (direction < 0) `current` in name order — or null if `current` is already the last/first. Single-pass min/max scan (NOT sort-then-index) so it stays correct even when `current` isn't in `candidates` (deleted, or a playable-but-non-video-extension file the directory walk skipped). Comparison is by filename (Path.GetFileName) under a total order — OrdinalIgnoreCase with an Ordinal tie-break — so case-only-differing siblings (Movie.mp4 vs movie.mp4 on a case-sensitive FS) are ordered deterministically. Filename (not full path) comparison also means a relative `current` (e.g. an argv-passed "clip.mp4") compares correctly against the enumerator's absolute candidate paths; within one directory the two orderings coincide anyway.
    public static string? FindAdjacentByName(IReadOnlyList<string> candidates, string current, int direction)
    {
        if (candidates == null)
        {
            throw new ArgumentNullException(nameof(candidates));
        }
        if (current == null)
        {
            throw new ArgumentNullException(nameof(current));
        }
        string currentName = Path.GetFileName(current);
        string? best = null;
        string? bestName = null;
        foreach (var c in candidates)
        {
            string name = Path.GetFileName(c);
            int cmp = CompareName(name, currentName);
            bool onRequestedSide = direction > 0 ? cmp > 0 : cmp < 0;
            if (!onRequestedSide)
            {
                continue;
            }
            // Track the running best: for "next" the smallest of the after-set; for "previous" the largest of the before-set.
            if (best == null || (direction > 0 ? CompareName(name, bestName!) < 0 : CompareName(name, bestName!) > 0))
            {
                best = c;
                bestName = name;
            }
        }
        return best;
    }

    // Total order over filenames: case-insensitive first (matches ExpandPaths' sort), case-sensitive tie-break so siblings differing only in case stay distinct and deterministically ordered.
    private static int CompareName(string a, string b)
    {
        int cmp = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        if (cmp != 0)
        {
            return cmp;
        }
        return string.Compare(a, b, StringComparison.Ordinal);
    }

    // Filesystem glue over FindAdjacentByName: list the directory (top level only — "same directory", non-recursive), keep video files, return the neighbor of currentPath in the requested direction. Returns null when there's no neighbor on that side or the listing fails (reported via onError, never swallowed — mirrors ExpandPaths). Caller resolves `directory` from currentPath (e.g. via TrackPreferences.TryGetDirectoryKey) so the URI / no-parent cases are filtered before we get here.
    public static string? FindDirectoryNeighbor(string directory, string currentPath, int direction, Action<string> onError)
    {
        if (directory == null)
        {
            throw new ArgumentNullException(nameof(directory));
        }
        if (currentPath == null)
        {
            throw new ArgumentNullException(nameof(currentPath));
        }
        if (onError == null)
        {
            throw new ArgumentNullException(nameof(onError));
        }
        var candidates = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if (IsVideoFile(f))
                {
                    candidates.Add(f);
                }
            }
        }
        catch (Exception ex)
        {
            onError($"directory listing failed for '{directory}': {ex.Message}");
            return null;
        }
        return FindAdjacentByName(candidates, currentPath, direction);
    }
}
