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
}
