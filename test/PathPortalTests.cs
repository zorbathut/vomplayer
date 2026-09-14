using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    // Builds an origin file plus a fake one-file "portal" directory holding a byte-identical copy with the same mtime; the caller decides whether to stamp the host-path xattr on the portal copy.
    private (string root, string host, string portal) MakePortalPair(string content = "video", string hostName = "movie.mkv")
    {
        var root = Path.Combine(tempDir, "doc");
        var hostDir = Path.Combine(tempDir, "videos");
        Directory.CreateDirectory(hostDir);
        var host = Path.Combine(hostDir, hostName);
        File.WriteAllText(host, content);
        var portalDir = Path.Combine(root, "abc123");
        Directory.CreateDirectory(portalDir);
        var portal = Path.Combine(portalDir, hostName);
        File.WriteAllText(portal, content);
        var stamp = new DateTime(2024, 5, 14, 21, 59, 23, DateTimeKind.Utc).AddTicks(430787);
        File.SetLastWriteTimeUtc(host, stamp);
        File.SetLastWriteTimeUtc(portal, stamp);
        return (root, host, portal);
    }

    [Test]
    public void SameFileRequiresBothToExistWithEqualSizeAndMtime()
    {
        var (_, host, portal) = MakePortalPair();
        Assert.That(PathPortal.SameFile(portal, host), Is.True);
        File.SetLastWriteTimeUtc(portal, File.GetLastWriteTimeUtc(host).AddTicks(1));
        Assert.That(File.GetLastWriteTimeUtc(portal), Is.Not.EqualTo(File.GetLastWriteTimeUtc(host)), "this filesystem must keep a one-tick difference for the exactness assertion to mean anything");
        Assert.That(PathPortal.SameFile(portal, host), Is.False, "mtime is compared exactly");
        File.SetLastWriteTimeUtc(portal, File.GetLastWriteTimeUtc(host));
        File.AppendAllText(host, "x");
        File.SetLastWriteTimeUtc(host, File.GetLastWriteTimeUtc(portal));
        Assert.That(PathPortal.SameFile(portal, host), Is.False, "size differs");
        var missing = Path.Combine(tempDir, "missing.mkv");
        Assert.That(PathPortal.SameFile(portal, missing), Is.False);
        Assert.That(PathPortal.SameFile(missing, missing), Is.False, "two absent files are not the same file");
    }

    [Test]
    public void TryResolveToHostResolvesAReachableIdenticalOrigin()
    {
        var (root, host, portal) = MakePortalPair();
        XattrSupport.SetUserXattrOrIgnore(portal, PathPortal.HostPathXattr, host);
        var resolution = PathPortal.TryResolveToHost(portal, root);
        Assert.That(resolution, Is.Not.Null);
        Assert.That(resolution!.HostPath, Is.EqualTo(host));
        Assert.That(resolution.PortalPath, Is.EqualTo(portal));
        Assert.That(resolution.Length, Is.EqualTo(new FileInfo(host).Length));
    }

    [Test]
    public void TryResolveToHostNormalizesTheXattrValue()
    {
        var (root, host, portal) = MakePortalPair();
        var dodgy = Path.Combine(Path.GetDirectoryName(host)!, "..", "videos", Path.GetFileName(host)) + "\0";
        XattrSupport.SetUserXattrOrIgnore(portal, PathPortal.HostPathXattr, dodgy);
        var resolution = PathPortal.TryResolveToHost(portal, root);
        Assert.That(resolution, Is.Not.Null);
        Assert.That(resolution!.HostPath, Is.EqualTo(host));
    }

    [Test]
    public void TryResolveToHostDeclinesWhenThereIsNothingToResolve()
    {
        var (root, host, portal) = MakePortalPair();
        Assert.That(PathPortal.TryResolveToHost(host, root), Is.Null, "not a portal path");
        Assert.That(PathPortal.TryResolveToHost("https://example.com/x.mkv", root), Is.Null);
        Assert.That(PathPortal.TryResolveToHost(portal, root), Is.Null, "no xattr");
    }

    [Test]
    public void TryResolveToHostDeclinesAMismatchedOrMissingOrDirectoryOrigin()
    {
        var (root, host, portal) = MakePortalPair();
        XattrSupport.SetUserXattrOrIgnore(portal, PathPortal.HostPathXattr, host);
        File.SetLastWriteTimeUtc(host, File.GetLastWriteTimeUtc(host).AddSeconds(1));
        Assert.That(PathPortal.TryResolveToHost(portal, root), Is.Null, "mtime differs");

        var gone = Path.Combine(tempDir, "videos", "gone.mkv");
        XattrSupport.SetUserXattrOrIgnore(portal, PathPortal.HostPathXattr, gone);
        Assert.That(PathPortal.TryResolveToHost(portal, root), Is.Null, "origin missing");

        XattrSupport.SetUserXattrOrIgnore(portal, PathPortal.HostPathXattr, Path.GetDirectoryName(host)!);
        Assert.That(PathPortal.TryResolveToHost(portal, root), Is.Null, "origin is a directory");
    }

    // Execute-only on the directory: the file inside can still be stat'ed (so TryResolveToHost accepts it) but the directory can't be enumerated, which is ResolveAll's own reason to decline.
    [Test]
    public void ResolveAllDeclinesWhenTheOriginDirectoryIsUnlistable()
    {
        if (Vomplayer.LibC.GetUid() == 0)
        {
            Assert.Ignore("root can list an execute-only directory");
        }
        var (root, host, portal) = MakePortalPair();
        XattrSupport.SetUserXattrOrIgnore(portal, PathPortal.HostPathXattr, host);
        File.SetUnixFileMode(Path.GetDirectoryName(host)!, UnixFileMode.UserExecute);
        Assert.That(PathPortal.TryResolveToHost(portal, root), Is.Not.Null, "the file itself is reachable");
        var resolutions = new List<PathResolution>();
        var result = PathPortal.ResolveAll(new[] { portal }, root, resolutions.Add);
        Assert.That(result, Is.EqualTo(new[] { portal }));
        Assert.That(resolutions, Is.Empty);
    }

    [Test]
    public void ResolveAllKeepsOrderAndLeavesNonPortalEntriesAlone()
    {
        var (root, host, portal) = MakePortalPair();
        XattrSupport.SetUserXattrOrIgnore(portal, PathPortal.HostPathXattr, host);
        var input = new[] { "/videos/first.mkv", portal, "https://example.com/v", "" };
        var resolutions = new List<PathResolution>();
        var result = PathPortal.ResolveAll(input, root, resolutions.Add);
        Assert.That(result, Is.EqualTo(new[] { "/videos/first.mkv", host, "https://example.com/v", "" }));
        Assert.That(resolutions.Select(r => r.HostPath), Is.EqualTo(new[] { host }));
        Assert.That(PathPortal.ResolveAll(new[] { "/videos/a.mkv" }, root, resolutions.Add), Is.EqualTo(new[] { "/videos/a.mkv" }));
    }

    // A portal path whose origin directory the sandbox can see, but whose file was rejected, is reported as its own kind: the "grant the folder" advice would be wrong there. The origin in the problem is the normalized spelling even when the xattr carries a NUL terminator.
    [Test]
    public void DiagnoseReportsOriginRejectedWhenTheOriginDirectoryIsReachable()
    {
        var (root, host, portal) = MakePortalPair();
        XattrSupport.SetUserXattrOrIgnore(portal, PathPortal.HostPathXattr, host);
        File.AppendAllText(host, "different");
        var problem = PathPortal.Diagnose(portal, root, directoryKey: null);
        Assert.That(problem, Is.Not.Null);
        Assert.That(problem!.Kind, Is.EqualTo(PathProblemKind.DocumentPortalOriginRejected));
        Assert.That(problem.Directory, Is.EqualTo(Path.GetDirectoryName(host)));
        Assert.That(problem.HostPath, Is.EqualTo(host));

        XattrSupport.SetUserXattrOrIgnore(portal, PathPortal.HostPathXattr, Path.Combine(tempDir, "nowhere", "movie.mkv"));
        var unreachable = PathPortal.Diagnose(portal, root, directoryKey: null);
        Assert.That(unreachable!.Kind, Is.EqualTo(PathProblemKind.DocumentPortal));

        XattrSupport.SetUserXattrOrIgnore(portal, PathPortal.HostPathXattr, host + "\0");
        var nulTerminated = PathPortal.Diagnose(portal, root, directoryKey: null);
        Assert.That(nulTerminated!.HostPath, Is.EqualTo(host));
    }
}
