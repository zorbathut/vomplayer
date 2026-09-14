using System;
using System.Collections.Generic;
using System.IO;
using Vomplayer.Util;

namespace Vomplayer.Tests;

// The file side of DiagnosticsLog (append, rotation, unopened no-op) and its two pure formatters. The formatter assertions treat the section layout — banner line, privacy note, one line per recent / directory — as a format contract, since the log is read by a human comparing launches.
[TestFixture]
public class DiagnosticsLogTests
{
    private string tempDir = string.Empty;
    private string logPath = string.Empty;

    [SetUp]
    public void Setup()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "vompl-diaglog-" + Guid.NewGuid().ToString("N"));
        logPath = Path.Combine(tempDir, "sub", "diagnostics.log");
    }

    [TearDown]
    public void TearDown()
    {
        DiagnosticsLog.Close();
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public void AppendCreatesTheDirectoryAndAccumulatesSections()
    {
        DiagnosticsLog.Open(logPath);
        DiagnosticsLog.Append("first");
        DiagnosticsLog.Append("second");
        var text = File.ReadAllText(logPath);
        Assert.That(text, Does.Contain("first\n"));
        Assert.That(text, Does.Contain("second\n"));
        Assert.That(text.IndexOf("first", StringComparison.Ordinal), Is.LessThan(text.IndexOf("second", StringComparison.Ordinal)));
    }

    [Test]
    public void AppendBeforeOpenWritesNothing()
    {
        DiagnosticsLog.Append("orphan");
        Assert.That(File.Exists(logPath), Is.False);
    }

    // Rotation happens at the launch banner and nowhere else, so an over-cap file keeps growing mid-launch and is cut only when the next process starts.
    [Test]
    public void LaunchBannerRotatesAnOverCapFileButAppendDoesNot()
    {
        DiagnosticsLog.Open(logPath);
        DiagnosticsLog.Append(new string('x', DiagnosticsLog.MaxBytes + 1));
        DiagnosticsLog.Append("still-same-file");
        Assert.That(File.Exists(logPath + ".1"), Is.False);
        DiagnosticsLog.WriteLaunch(Array.Empty<string>(), Array.Empty<string>());
        Assert.That(File.Exists(logPath + ".1"), Is.True);
        Assert.That(new FileInfo(logPath + ".1").Length, Is.GreaterThan(DiagnosticsLog.MaxBytes));
        Assert.That(File.ReadAllText(logPath), Does.StartWith("=== launch "));
        Assert.That(File.ReadAllText(logPath), Does.Not.Contain("still-same-file"));
    }

    [Test]
    public void FormatLaunchCarriesBannerPrivacyNoteAndEveryRow()
    {
        var text = DiagnosticsLog.FormatLaunch(
            new DateTimeOffset(2026, 9, 14, 1, 2, 3, TimeSpan.Zero),
            4242,
            "[Application]\nname=io.github.zorbathut.vomplayer\n",
            new List<KeyValuePair<string, string?>> { new("XDG_RUNTIME_DIR", "/run/user/1000"), new("FLATPAK_ID", null) },
            new[] { "/media/a.mkv", "https://example.com/v" },
            new[] { "/media", "/var/mnt/ext" });
        Assert.That(text, Does.StartWith("=== launch 2026-09-14T01:02:03"));
        Assert.That(text, Does.Contain("pid 4242"));
        Assert.That(text, Does.Contain("name=io.github.zorbathut.vomplayer"));
        Assert.That(text, Does.Contain("XDG_RUNTIME_DIR=/run/user/1000"));
        Assert.That(text, Does.Contain("FLATPAK_ID=<unset>"));
        Assert.That(text, Does.Contain("/media/a.mkv"));
        Assert.That(text, Does.Contain("/var/mnt/ext"));
        Assert.That(text.ToLowerInvariant(), Does.Contain("contain"), "privacy note telling the user what the file holds");
    }

    [Test]
    public void FormatLoadShowsBothDirectorySpellingsAndWhetherPreferencesKnowThem()
    {
        var text = DiagnosticsLog.FormatLoad("/media/link/a.mkv", "/media/link", "/media/real", keyKnown: false, realKnown: true, realPathErrno: 0);
        Assert.That(text, Does.Contain("/media/link/a.mkv"));
        Assert.That(text, Does.Contain("/media/real"));
        var noKey = DiagnosticsLog.FormatLoad("https://example.com/v", null, null, keyKnown: false, realKnown: false, realPathErrno: 0);
        Assert.That(noKey, Does.Contain("https://example.com/v"));
        Assert.That(noKey, Is.Not.EqualTo(text));
        var failed = DiagnosticsLog.FormatLoad("/gone/a.mkv", "/gone", null, keyKnown: false, realKnown: false, realPathErrno: 2);
        Assert.That(failed, Does.Contain("errno 2"));
    }

    [Test]
    public void LoadWritesTheDirectoryKeyAndWhetherPreferencesKnowIt()
    {
        DiagnosticsLog.Open(logPath);
        var dir = Path.Combine(tempDir, "videos");
        Directory.CreateDirectory(dir);
        DiagnosticsLog.Load(Path.Combine(dir, "a.mkv"), dir, new[] { dir });
        var text = File.ReadAllText(logPath);
        Assert.That(text, Does.Contain("--- load "));
        Assert.That(text, Does.Contain($"directory-key={dir}"));
        Assert.That(text, Does.Contain("key=True"));
    }
}
