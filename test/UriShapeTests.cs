using Vomplayer.Util;

namespace Vomplayer.Tests;

[TestFixture]
public class UriShapeTests
{
    [TestCase("http://example.com/v.mp4", true)]
    [TestCase("https://example.com/v.mp4", true)]
    [TestCase("file:///home/u/v.mp4", true)]
    [TestCase("smb://server/share/v.mp4", true)]
    [TestCase("rtsp://cam.local/stream", true)]
    [TestCase("magnet:?xt=urn:btih:abcdef", true)]
    [TestCase("MAGNET:?xt=urn:btih:abcdef", true)]
    [TestCase("/home/u/v.mp4", false)]
    [TestCase("relative/path.mp4", false)]
    [TestCase(@"C:\videos\v.mp4", false)]
    [TestCase("C:/videos/v.mp4", false)]
    // Relative filename containing a colon — deliberately NOT treated as a URI (see UriShape's rejected-general-parse note).
    [TestCase("foo:bar.mp4", false)]
    [TestCase("", false)]
    public void LooksLikeUriClassifies(string input, bool expected)
    {
        Assert.That(UriShape.LooksLikeUri(input), Is.EqualTo(expected));
    }
}
