using System.Collections.Generic;
using Vomplayer;

namespace Vomplayer.Tests;

[TestFixture]
public class StartupHelpersTests
{
    [Test]
    public void ComputeAppFlagsSingleInstanceHasHandlesCommandLineWithoutNonUnique()
    {
        var flags = StartupHelpers.ComputeAppFlags(singleInstance: true);
        Assert.That(flags & Gio.ApplicationFlags.HandlesCommandLine, Is.EqualTo(Gio.ApplicationFlags.HandlesCommandLine));
        Assert.That(flags & Gio.ApplicationFlags.NonUnique, Is.EqualTo(Gio.ApplicationFlags.FlagsNone));
    }

    [Test]
    public void ComputeAppFlagsMultiInstanceHasBothHandlesCommandLineAndNonUnique()
    {
        var flags = StartupHelpers.ComputeAppFlags(singleInstance: false);
        Assert.That(flags & Gio.ApplicationFlags.HandlesCommandLine, Is.EqualTo(Gio.ApplicationFlags.HandlesCommandLine));
        Assert.That(flags & Gio.ApplicationFlags.NonUnique, Is.EqualTo(Gio.ApplicationFlags.NonUnique));
    }

    [Test]
    public void ResolveCommandLineFilesSkipsProgramName()
    {
        var skipped = new List<string>();
        var result = StartupHelpers.ResolveCommandLineFiles(
            new[] { "vomplayer", "/tmp/foo.mp4" }, cwd: "/home/me", skipped.Add);
        Assert.That(result, Is.EqualTo(new[] { "/tmp/foo.mp4" }));
        Assert.That(skipped, Is.Empty);
    }

    [Test]
    public void ResolveCommandLineFilesRootsRelativePathsAgainstCwd()
    {
        var skipped = new List<string>();
        var result = StartupHelpers.ResolveCommandLineFiles(
            new[] { "vomplayer", "./foo.mp4", "../bar.mkv" }, cwd: "/home/me/videos", skipped.Add);
        Assert.That(result, Is.EqualTo(new[] { "/home/me/videos/foo.mp4", "/home/me/bar.mkv" }));
        Assert.That(skipped, Is.Empty);
    }

    [Test]
    public void ResolveCommandLineFilesLeavesAbsolutePathsAlone()
    {
        var skipped = new List<string>();
        var result = StartupHelpers.ResolveCommandLineFiles(
            new[] { "vomplayer", "/abs/path.mp4" }, cwd: "/totally/different/cwd", skipped.Add);
        Assert.That(result, Is.EqualTo(new[] { "/abs/path.mp4" }));
    }

    [Test]
    public void ResolveCommandLineFilesLeavesUriSchemesAlone()
    {
        var skipped = new List<string>();
        var result = StartupHelpers.ResolveCommandLineFiles(
            new[] { "vomplayer", "https://example.com/v.mp4", "smb://host/share/x.mkv" },
            cwd: "/home/me", skipped.Add);
        Assert.That(result, Is.EqualTo(new[] { "https://example.com/v.mp4", "smb://host/share/x.mkv" }));
    }

    [Test]
    public void ResolveCommandLineFilesLogsAndDropsUnknownFlags()
    {
        var skipped = new List<string>();
        var result = StartupHelpers.ResolveCommandLineFiles(
            new[] { "vomplayer", "--bogus", "/tmp/a.mp4", "-x" }, cwd: "/home/me", skipped.Add);
        Assert.That(result, Is.EqualTo(new[] { "/tmp/a.mp4" }));
        Assert.That(skipped, Has.Count.EqualTo(2));
        Assert.That(skipped[0], Does.Contain("--bogus"));
        Assert.That(skipped[1], Does.Contain("-x"));
    }

    [Test]
    public void ResolveCommandLineFilesTreatsNoArgsAsEmpty()
    {
        var skipped = new List<string>();
        var result = StartupHelpers.ResolveCommandLineFiles(
            new[] { "vomplayer" }, cwd: "/home/me", skipped.Add);
        Assert.That(result, Is.Empty);
        Assert.That(skipped, Is.Empty);
    }
}
