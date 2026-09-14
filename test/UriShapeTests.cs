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

    [Test]
    public void TryLocalPathFromFileUriConvertsEmptyAndLocalhostAuthoritiesOnly()
    {
        Assert.That(UriShape.TryLocalPathFromFileUri("file:///run/user/1000/doc/abc/x%20y.mkv", out var p1), Is.True);
        Assert.That(p1, Is.EqualTo("/run/user/1000/doc/abc/x y.mkv"));
        Assert.That(UriShape.TryLocalPathFromFileUri("file://localhost/tmp/x.mkv", out var p2), Is.True);
        Assert.That(p2, Is.EqualTo("/tmp/x.mkv"));
        Assert.That(UriShape.TryLocalPathFromFileUri("file:///media/dir/", out var p3), Is.True);
        Assert.That(p3, Is.EqualTo("/media/dir/"));
        Assert.That(UriShape.TryLocalPathFromFileUri("file://nas/share/v.mp4", out _), Is.False);
        Assert.That(UriShape.TryLocalPathFromFileUri("https://example.com/v.mp4", out _), Is.False);
        // Degenerate but consistent: a bare file:// is the root directory, which a load then fails on loudly.
        Assert.That(UriShape.TryLocalPathFromFileUri("file://", out var p4), Is.True);
        Assert.That(p4, Is.EqualTo("/"));
    }
}
