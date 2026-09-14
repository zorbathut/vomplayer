using System;
using System.IO;
using Vomplayer.Services;

namespace Vomplayer.Tests;

// The visibility ladder is the probe that answers the diagnostic question ("which prefix of the real path can the sandbox still reach"), so its rung-by-rung classification is pinned against a real temp tree. The probe list and the background dispatch are orchestration and are not unit-tested.
[TestFixture]
public class DiagnosticsEnvironmentTests
{
    private string tempDir = string.Empty;

    [SetUp]
    public void Setup()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "vompl-ladder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var d in Directory.EnumerateDirectories(tempDir))
        {
            File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Directory.Delete(tempDir, recursive: true);
    }

    [Test]
    public void LadderNamesEveryRungRootFirstAndStopsBeingListableWhereTheTreeEnds()
    {
        var present = Path.Combine(tempDir, "present");
        Directory.CreateDirectory(present);
        var hostPath = Path.Combine(present, "missing", "deeper", "file.mkv");
        var lines = DiagnosticsEnvironment.VisibilityLadder(hostPath).Split('\n');
        Assert.That(lines[0], Is.EqualTo("/: listable"));
        // A rung that is itself a symlink (a symlinked temp dir, say) carries a realpath note after the verdict, so match prefixes.
        Assert.That(lines, Has.Some.StartsWith($"{present}: listable (empty)"));
        // ENOENT is 2; a stale share root reports 116.
        Assert.That(lines, Has.Some.EqualTo($"{present}/missing: absent (errno 2)"));
        Assert.That(lines, Has.Some.EqualTo($"{present}/missing/deeper: absent (errno 2)"));
        Assert.That(lines[^1], Is.EqualTo($"{hostPath}: file absent"));
        // One rung per path component, plus the root and the file line.
        Assert.That(lines.Length, Is.EqualTo(Path.GetDirectoryName(hostPath)!.Split('/', StringSplitOptions.RemoveEmptyEntries).Length + 2));
    }

    [Test]
    public void LadderReportsAnUnlistableRungWithTheReason()
    {
        if (Vomplayer.LibC.GetUid() == 0)
        {
            Assert.Ignore("root can list a mode-000 directory");
        }
        var locked = Path.Combine(tempDir, "locked");
        Directory.CreateDirectory(locked);
        File.SetUnixFileMode(locked, UnixFileMode.None);
        var lines = DiagnosticsEnvironment.VisibilityLadder(Path.Combine(locked, "file.mkv")).Split('\n');
        Assert.That(lines, Has.Some.StartsWith($"{locked}: exists but not listable: UnauthorizedAccessException"));
        Assert.That(lines, Has.None.Contains("realpath"), "no symlinks in this tree, so no realpath notes");
    }

    // Relative and `..`-bearing spellings are normalized first, so the rungs are real prefixes of the real path rather than of the raw string.
    [Test]
    public void LadderNormalizesRelativeAndDotDotSpellings()
    {
        var present = Path.Combine(tempDir, "present");
        Directory.CreateDirectory(present);
        var dodgy = Path.Combine(tempDir, "elsewhere", "..", "present", "file.mkv");
        var lines = DiagnosticsEnvironment.VisibilityLadder(dodgy).Split('\n');
        Assert.That(lines, Has.None.Contains("/.."));
        Assert.That(lines, Has.None.Contains("elsewhere"));
        Assert.That(lines, Has.Some.StartsWith($"{present}: listable (empty)"));
        Assert.That(lines[^1], Is.EqualTo($"{Path.Combine(present, "file.mkv")}: file absent"));
        var relative = DiagnosticsEnvironment.VisibilityLadder("sub/x.mkv").Split('\n');
        Assert.That(relative, Has.None.StartsWith("/sub:"));
        Assert.That(relative[^1], Does.EndWith("/sub/x.mkv: file absent"));
    }

    [Test]
    public void LadderCallsAnExistingFileRungNotADirectory()
    {
        var file = Path.Combine(tempDir, "plain.mkv");
        File.WriteAllText(file, "");
        var lines = DiagnosticsEnvironment.VisibilityLadder(Path.Combine(file, "inner.mkv")).Split('\n');
        Assert.That(lines, Has.Some.EqualTo($"{file}: not a directory"));
    }

    [Test]
    public void LadderHandlesAFileDirectlyUnderRoot()
    {
        var lines = DiagnosticsEnvironment.VisibilityLadder("/file.mkv").Split('\n');
        Assert.That(lines, Is.EqualTo(new[] { "/: listable", "/file.mkv: file absent" }));
    }
}
