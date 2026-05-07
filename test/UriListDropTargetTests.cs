using System.IO;
using System.Linq;
using Vomplayer.Util;

namespace Vomplayer.Tests;

// Tests for UriListDropTarget.ParseAndConvert. The DropTargetAsync construction and gdk_drop_read_async machinery aren't testable without GTK, but the URI-list-text → path-list parsing is pure and exercises the format quirks (RFC 2483 # comments, blank lines, CRLF tolerance, file:// percent-decoding).
[TestFixture]
public class UriListDropTargetTests
{
    private string sandboxDir = string.Empty;

    [SetUp]
    public void Setup()
    {
        sandboxDir = Path.Combine(Path.GetTempPath(), $"uri-list-target-test-{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(sandboxDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(sandboxDir))
        {
            Directory.Delete(sandboxDir, recursive: true);
        }
    }

    [Test]
    public void NullOrEmptyReturnsEmptyList()
    {
        Assert.That(UriListDropTarget.ParseAndConvert(null), Is.Empty);
        Assert.That(UriListDropTarget.ParseAndConvert(""), Is.Empty);
    }

    [Test]
    public void FileUriDecodesToLocalPath()
    {
        var file = Path.Combine(sandboxDir, "foo.mp4");
        File.WriteAllText(file, "");
        Assert.That(UriListDropTarget.ParseAndConvert($"file://{file}"), Is.EqualTo(new[] { file }));
    }

    [Test]
    public void FileUriPercentDecodesSpacesAndSpecialChars()
    {
        var file = Path.Combine(sandboxDir, "My Video # 1.mp4");
        File.WriteAllText(file, "");
        var encoded = file.Replace(" ", "%20").Replace("#", "%23");
        Assert.That(UriListDropTarget.ParseAndConvert($"file://{encoded}"), Is.EqualTo(new[] { file }));
    }

    [Test]
    public void NonFileUriPassesThroughUnchanged()
    {
        // Non-file:// schemes (http, https, smb, …) reach playback as URIs for mpv / yt-dlp to handle.
        var input = "https://example.com/video.mp4\nsmb://server/share/clip.mkv";
        var result = UriListDropTarget.ParseAndConvert(input);
        Assert.That(result, Does.Contain("https://example.com/video.mp4"));
        Assert.That(result, Does.Contain("smb://server/share/clip.mkv"));
    }

    [Test]
    public void CommentsAndBlankLinesAreSkipped()
    {
        var file = Path.Combine(sandboxDir, "keep.mp4");
        File.WriteAllText(file, "");
        var input = $"# this is a comment\n\nfile://{file}\n#another comment\n   \n";
        Assert.That(UriListDropTarget.ParseAndConvert(input), Is.EqualTo(new[] { file }));
    }

    [Test]
    public void CrlfLineSeparatorsTolerated()
    {
        var a = Path.Combine(sandboxDir, "a.mp4");
        var b = Path.Combine(sandboxDir, "b.mp4");
        File.WriteAllText(a, "");
        File.WriteAllText(b, "");
        var result = UriListDropTarget.ParseAndConvert($"file://{a}\r\nfile://{b}\r\n");
        Assert.That(result.OrderBy(s => s), Is.EqualTo(new[] { a, b }.OrderBy(s => s)));
    }

    [Test]
    public void DirectoryDropExpandsRecursively()
    {
        File.WriteAllText(Path.Combine(sandboxDir, "a.mp4"), "");
        File.WriteAllText(Path.Combine(sandboxDir, "b.txt"), "");
        var sub = Path.Combine(sandboxDir, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "c.mkv"), "");

        var result = UriListDropTarget.ParseAndConvert($"file://{sandboxDir}");
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result, Does.Contain(Path.Combine(sandboxDir, "a.mp4")));
        Assert.That(result, Does.Contain(Path.Combine(sub, "c.mkv")));
    }
}
