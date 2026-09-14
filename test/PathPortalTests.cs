using System;
using System.IO;
using Vomplayer.Util;

namespace Vomplayer.Tests;

// Tests for the document-portal path rules. The portal root is a plain string parameter so no real portal is needed.
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
}
