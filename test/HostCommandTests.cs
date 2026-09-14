using System;
using Vomplayer.Services;

namespace Vomplayer.Tests;

[TestFixture]
public class HostCommandTests
{
    // The sandbox/host argv seam, shared with the yt-dlp launcher: pure, so it's pinned here rather than trusted.
    [Test]
    public void BuildPrefixesFlatpakSpawnOnlyInsideASandbox()
    {
        Assert.That(HostCommand.Build(inFlatpak: false, new[] { "flatpak", "--version" }), Is.EqualTo(new[] { "flatpak", "--version" }));
        Assert.That(HostCommand.Build(inFlatpak: true, new[] { "flatpak", "--version" }), Is.EqualTo(new[] { "flatpak-spawn", "--host", "--watch-bus", "flatpak", "--version" }));
        Assert.That(() => HostCommand.Build(inFlatpak: true, Array.Empty<string>()), Throws.ArgumentException);
    }
}
