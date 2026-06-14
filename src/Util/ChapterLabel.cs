using Vomplayer.Playback;

namespace Vomplayer.Util;

// Human-facing label for a chapter marker's hover tooltip: the container-provided Title when it has one, else a generated "Chapter N". N is 1-based — MediaChapter.Index is mpv's 0-based chapter-list position, so the displayed number is Index + 1.
internal static class ChapterLabel
{
    internal static string For(MediaChapter chapter)
    {
        if (!string.IsNullOrEmpty(chapter.Title))
        {
            return chapter.Title;
        }
        return $"Chapter {chapter.Index + 1}";
    }
}
