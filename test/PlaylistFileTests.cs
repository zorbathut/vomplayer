using System.Collections.Generic;
using System.IO;
using Vomplayer.Util;

namespace Vomplayer.Tests;

[TestFixture]
public class PlaylistFileTests
{
    [Test]
    public void SerializeJoinsItemsWithLfAndTrailingNewline()
    {
        var text = PlaylistFile.Serialize(new[] { "/a/b.mkv", "/c/d.mp4", "https://x/y.m3u8" });
        Assert.That(text, Is.EqualTo("/a/b.mkv\n/c/d.mp4\nhttps://x/y.m3u8\n"));
    }

    [Test]
    public void SerializeEmptyListIsEmptyString()
    {
        Assert.That(PlaylistFile.Serialize(new string[0]), Is.EqualTo(""));
    }

    [Test]
    public void SerializeRejectsNull()
    {
        Assert.That(() => PlaylistFile.Serialize(null!), Throws.ArgumentNullException);
    }

    [Test]
    public void ParseRoundTripsSerializedAbsolutePaths()
    {
        var items = new[] { "/a/b.mkv", "/c/d e.mp4", "https://x/y.m3u8" };
        var roundTripped = PlaylistFile.Parse(PlaylistFile.Serialize(items), null);
        Assert.That(roundTripped, Is.EqualTo(items));
    }

    [Test]
    public void ParseKeepsAbsolutePathsVerbatim()
    {
        var items = PlaylistFile.Parse("/movies/one.mkv\n/movies/two.mkv\n", "/some/playlist/dir");
        Assert.That(items, Is.EqualTo(new[] { "/movies/one.mkv", "/movies/two.mkv" }));
    }

    [Test]
    public void ParseTrimsWhitespaceAndSkipsBlankAndCommentLines()
    {
        var text = "#EXTM3U\n\n  /a/b.mkv  \n#EXTINF:123,Title\n/c/d.mkv\n   \n";
        var items = PlaylistFile.Parse(text, null);
        Assert.That(items, Is.EqualTo(new[] { "/a/b.mkv", "/c/d.mkv" }));
    }

    [Test]
    public void ParseKeepsUriVerbatimEvenWithBaseDirectory()
    {
        // Pins the "://"-before-IsPathRooted order: a URI is not Path.IsPathRooted on Linux, so
        // without the URI check first it would be (wrongly) joined onto baseDirectory.
        var items = PlaylistFile.Parse("https://example.com/video.mp4\nfile:///abs/clip.mkv\n", "/base/dir");
        Assert.That(items, Is.EqualTo(new[] { "https://example.com/video.mp4", "file:///abs/clip.mkv" }));
    }

    [Test]
    public void ParseResolvesRelativePathsAgainstBaseDirectory()
    {
        var items = PlaylistFile.Parse("sub/clip.mkv\n../up.mkv\n", "/home/user/pl");
        Assert.That(items, Is.EqualTo(new[]
        {
            Path.GetFullPath("/home/user/pl/sub/clip.mkv"),
            Path.GetFullPath("/home/user/pl/../up.mkv"),
        }));
        // The "../" entry normalizes away the parent segment.
        Assert.That(items[1], Is.EqualTo("/home/user/up.mkv"));
    }

    [Test]
    public void ParseLeavesRelativePathsUnchangedWhenNoBaseDirectory()
    {
        var items = PlaylistFile.Parse("sub/clip.mkv\nrelative.mp4\n", null);
        Assert.That(items, Is.EqualTo(new[] { "sub/clip.mkv", "relative.mp4" }));
    }

    [Test]
    public void ParseHandlesCrlfLineEndings()
    {
        var items = PlaylistFile.Parse("/a/b.mkv\r\n/c/d.mkv\r\n", null);
        Assert.That(items, Is.EqualTo(new[] { "/a/b.mkv", "/c/d.mkv" }));
    }

    [Test]
    public void ParseWhitespaceOnlyOrEmptyReturnsEmpty()
    {
        Assert.That(PlaylistFile.Parse("", null), Is.Empty);
        Assert.That(PlaylistFile.Parse("   \n\r\n  \n", null), Is.Empty);
        Assert.That(PlaylistFile.Parse("#EXTM3U\n# only comments\n", null), Is.Empty);
    }
}
