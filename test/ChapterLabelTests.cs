using Vomplayer.Playback;
using Vomplayer.Util;

namespace Vomplayer.Tests;

[TestFixture]
public class ChapterLabelTests
{
    [Test]
    public void TitlePresentReturnsTitle()
    {
        var label = ChapterLabel.For(new MediaChapter(0, "Opening Credits", 0.0));
        Assert.That(label, Is.EqualTo("Opening Credits"));
    }

    [Test]
    public void TitleNullFallsBackToOneBasedChapterNumber()
    {
        // Index is mpv's 0-based chapter-list position; the human label is 1-based.
        Assert.That(ChapterLabel.For(new MediaChapter(0, null, 0.0)), Is.EqualTo("Chapter 1"));
        Assert.That(ChapterLabel.For(new MediaChapter(4, null, 300.0)), Is.EqualTo("Chapter 5"));
    }

    [Test]
    public void TitleEmptyFallsBackToChapterNumber()
    {
        var label = ChapterLabel.For(new MediaChapter(2, "", 120.0));
        Assert.That(label, Is.EqualTo("Chapter 3"));
    }
}
