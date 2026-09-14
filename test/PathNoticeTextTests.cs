using Vomplayer.Util;

namespace Vomplayer.Tests;

// Relational tests for the warning-row copy (no exact strings — the wording is free to change) plus the per-session dedupe.
[TestFixture]
public class PathNoticeTextTests
{
    private const string Log = "/state/diagnostics.log";

    [Test]
    public void ComposeNamesTheHostDirectoryAndTheLogWhenKnown()
    {
        var text = PathNoticeText.Compose(new PathProblem("/run/user/1000/doc/abc/x.mkv", PathProblemKind.DocumentPortal, "/var/mnt/ext/Show/x.mkv", 0, "/var/mnt/ext/Show"), Log);
        Assert.That(text, Is.Not.Empty);
        Assert.That(text, Does.Contain("/var/mnt/ext/Show"));
        Assert.That(text, Does.Contain("--filesystem=/var/mnt/ext/Show"));
        Assert.That(text, Does.Contain(Log));
    }

    [Test]
    public void ComposeDiffersAcrossKindsAndAcrossKnownVersusUnknownOrigin()
    {
        var portalKnown = PathNoticeText.Compose(new PathProblem("/run/user/1000/doc/abc/x.mkv", PathProblemKind.DocumentPortal, "/var/mnt/ext/x.mkv", 0, "/var/mnt/ext"), Log);
        var portalUnknown = PathNoticeText.Compose(new PathProblem("/run/user/1000/doc/abc/x.mkv", PathProblemKind.DocumentPortal, null, 61, null), Log);
        var unlistable = PathNoticeText.Compose(new PathProblem("/media/x.mkv", PathProblemKind.DirectoryUnlistable, null, 0, "/media"), Log);
        Assert.That(portalUnknown, Is.Not.Empty);
        Assert.That(unlistable, Is.Not.Empty);
        Assert.That(portalKnown, Is.Not.EqualTo(portalUnknown));
        Assert.That(portalKnown, Is.Not.EqualTo(unlistable));
        Assert.That(portalUnknown, Is.Not.EqualTo(unlistable));
        Assert.That(portalUnknown, Does.Not.Contain("--filesystem="), "no directory to suggest granting");
        Assert.That(unlistable, Does.Contain("/media"));
        var mismatched = PathNoticeText.Compose(new PathProblem("/run/user/1000/doc/abc/x.mkv", PathProblemKind.DocumentPortalOriginRejected, "/var/mnt/ext/x.mkv", 0, "/var/mnt/ext"), Log);
        Assert.That(mismatched, Is.Not.Empty);
        Assert.That(mismatched, Is.Not.EqualTo(portalKnown));
        Assert.That(mismatched, Is.Not.EqualTo(portalUnknown));
        Assert.That(mismatched, Is.Not.EqualTo(unlistable));
        Assert.That(mismatched, Does.Not.Contain("--filesystem="), "the folder is visible, so granting it is not the fix");
        Assert.That(mismatched, Does.Contain("/var/mnt/ext"));
        Assert.That(mismatched, Does.Contain(Log));
    }

    [Test]
    public void DedupeShowsEachDirectoryOnceAndFallsBackToThePath()
    {
        var dedupe = new PathProblemDedupe();
        var a1 = new PathProblem("/run/user/1000/doc/a/x.mkv", PathProblemKind.DocumentPortal, "/var/mnt/Show/x.mkv", 0, "/var/mnt/Show");
        var a2 = new PathProblem("/run/user/1000/doc/b/y.mkv", PathProblemKind.DocumentPortal, "/var/mnt/Show/y.mkv", 0, "/var/mnt/Show");
        var other = new PathProblem("/run/user/1000/doc/c/z.mkv", PathProblemKind.DocumentPortal, "/var/mnt/Other/z.mkv", 0, "/var/mnt/Other");
        var noOrigin = new PathProblem("/run/user/1000/doc/d/w.mkv", PathProblemKind.DocumentPortal, null, 61, null);
        Assert.That(dedupe.IsFirstFor(a1), Is.True);
        Assert.That(dedupe.IsFirstFor(a2), Is.False);
        Assert.That(dedupe.IsFirstFor(other), Is.True);
        Assert.That(dedupe.IsFirstFor(noOrigin), Is.True);
        Assert.That(dedupe.IsFirstFor(noOrigin), Is.False);
    }

    // The override command must survive a paste into a shell: the quoted path abuts the unquoted --filesystem= and :ro, which the shell joins into one word.
    [Test]
    public void ComposeShellQuotesDirectoriesTheShellWouldOtherwiseSplit()
    {
        var spaced = PathNoticeText.Compose(new PathProblem("/run/user/1000/doc/a/x.mkv", PathProblemKind.DocumentPortal, "/media/My Videos/x.mkv", 0, "/media/My Videos"), Log);
        Assert.That(spaced, Does.Contain("--filesystem='/media/My Videos':ro"));
        var apostrophe = PathNoticeText.Compose(new PathProblem("/run/user/1000/doc/a/x.mkv", PathProblemKind.DocumentPortal, "/media/Bob's/x.mkv", 0, "/media/Bob's"), Log);
        Assert.That(apostrophe, Does.Contain("--filesystem='/media/Bob'\\''s':ro"));
        var plain = PathNoticeText.Compose(new PathProblem("/run/user/1000/doc/a/x.mkv", PathProblemKind.DocumentPortal, "/media/plain-1.0/x.mkv", 0, "/media/plain-1.0"), Log);
        Assert.That(plain, Does.Contain("--filesystem=/media/plain-1.0:ro"));
    }
}
