using System;
using System.IO;
using Vomplayer.Util;

namespace Vomplayer.Tests;

// Tests for the document-portal path rules. The portal root is a plain string parameter so no real portal is needed; the filesystem checks run against real temp directories (a symlink-free one-file "portal" directory stands in for the FUSE mount).
[TestFixture]
public class PathPortalTests
{
    private string tempDir = string.Empty;

    [SetUp]
    public void Setup()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "vompl-portal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(tempDir))
        {
            // Unlistable-directory tests chmod a top-level directory to 000; restore so the recursive delete succeeds.
            foreach (var d in Directory.EnumerateDirectories(tempDir))
            {
                File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public void IsPortalPathMatchesOnlyStrictlyBelowTheRoot()
    {
        Assert.That(PathPortal.IsPortalPath("/run/user/1000/doc/abc/x.mkv", "/run/user/1000/doc"), Is.True);
        Assert.That(PathPortal.IsPortalPath("/run/user/1000/doc", "/run/user/1000/doc"), Is.False);
        Assert.That(PathPortal.IsPortalPath("/run/user/1000/documents/x.mkv", "/run/user/1000/doc"), Is.False);
        Assert.That(PathPortal.IsPortalPath("/home/deck/x.mkv", "/run/user/1000/doc"), Is.False);
        Assert.That(PathPortal.IsPortalPath("https://example.com/doc/x.mkv", "/run/user/1000/doc"), Is.False);
    }

    [Test]
    public void RootHonoursXdgRuntimeDirAndFallsBackToRunUser()
    {
        var saved = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        try
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", "/tmp/rt");
            Assert.That(PathPortal.Root, Is.EqualTo("/tmp/rt/doc"));
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", null);
            Assert.That(PathPortal.Root, Does.StartWith("/run/user/").And.EndWith("/doc"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", saved);
        }
    }

    [Test]
    public void DiagnoseFlagsAPortalPathEvenWithoutAHostPathXattr()
    {
        var root = Path.Combine(tempDir, "doc");
        var file = Path.Combine(root, "abc123", "movie.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "");
        var problem = PathPortal.Diagnose(file, root, directoryKey: null);
        Assert.That(problem, Is.Not.Null);
        Assert.That(problem!.Kind, Is.EqualTo(PathProblemKind.DocumentPortal));
        Assert.That(problem.Path, Is.EqualTo(file));
        Assert.That(problem.HostPath, Is.Null);
        Assert.That(problem.HostPathErrno, Is.Not.Zero, "why the host path is missing is part of the finding");
        Assert.That(problem.Directory, Is.Null);
    }

    // The portal rule must see the same spelling TryGetDirectoryKey saw: a non-canonical path into the portal is still a portal path.
    [Test]
    public void DiagnoseNormalizesBeforeApplyingThePortalRule()
    {
        var root = Path.Combine(tempDir, "doc");
        var file = Path.Combine(root, "abc123", "movie.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "");
        var dodgy = Path.Combine(root, "abc123", "..", "abc123", "movie.mkv");
        var problem = PathPortal.Diagnose(dodgy, root, directoryKey: null);
        Assert.That(problem, Is.Not.Null);
        Assert.That(problem!.Kind, Is.EqualTo(PathProblemKind.DocumentPortal));
        Assert.That(problem.Path, Is.EqualTo(file), "the reported path is the normalized spelling");
    }

    // Junk that a raw drop payload can carry must not reach Path.GetFullPath unguarded: the answer is "no problem to report", never an exception.
    [Test]
    public void DiagnoseAndNormalizeTolerateJunkInput()
    {
        var root = Path.Combine(tempDir, "doc");
        Assert.That(() => PathPortal.Diagnose("\0", root, null), Throws.Nothing);
        Assert.That(PathPortal.Diagnose("\0", root, null), Is.Null);
        Assert.That(PathPortal.Diagnose("", root, null), Is.Null);
        Assert.That(PathPortal.TryNormalize("\0"), Is.Null);
        Assert.That(PathPortal.TryNormalize(""), Is.Null);
        Assert.That(PathPortal.TryNormalize("https://example.com/x.mkv"), Is.Null);
        Assert.That(PathPortal.TryNormalize("/a/b/../c.mkv"), Is.EqualTo("/a/c.mkv"));
    }

    [Test]
    public void DiagnoseIsQuietForOrdinaryListableDirectoriesAndRemoteUris()
    {
        var dir = Path.Combine(tempDir, "videos");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "a.mkv");
        File.WriteAllText(file, "");
        Assert.That(PathPortal.Diagnose(file, Path.Combine(tempDir, "doc"), dir), Is.Null);
        Assert.That(PathPortal.Diagnose("https://example.com/a.mkv", Path.Combine(tempDir, "doc"), null), Is.Null);
        // A directory that doesn't exist at all isn't a listing problem — the load itself will fail loudly.
        Assert.That(PathPortal.Diagnose(Path.Combine(tempDir, "nope", "a.mkv"), Path.Combine(tempDir, "doc"), Path.Combine(tempDir, "nope")), Is.Null);
    }

    [Test]
    public void DiagnoseFlagsAnExistingDirectoryThatCannotBeListed()
    {
        if (Vomplayer.LibC.GetUid() == 0)
        {
            Assert.Ignore("root can list a mode-000 directory");
        }
        var dir = Path.Combine(tempDir, "locked");
        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(dir, UnixFileMode.None);
        var problem = PathPortal.Diagnose(Path.Combine(dir, "a.mkv"), Path.Combine(tempDir, "doc"), dir);
        Assert.That(problem, Is.Not.Null);
        Assert.That(problem!.Kind, Is.EqualTo(PathProblemKind.DirectoryUnlistable));
        Assert.That(problem.Directory, Is.EqualTo(dir));
    }
}
