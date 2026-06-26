using System;
using System.IO;
using Vomplayer.UserData;

namespace Vomplayer.Tests;

[TestFixture]
public class UserConfigTests
{
    private string? tempDir;

    [SetUp]
    public void Setup()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "vompl-cfg-" + Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void Teardown()
    {
        if (tempDir != null && Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public void LoadOrDefaultReturnsDefaultsWhenFileMissing()
    {
        var path = Path.Combine(tempDir!, "config.toml");
        var cfg = UserConfig.LoadOrDefault(path);

        // Hotkey defaults reproduce the pre-customization built-in keymap.
        Assert.That(cfg.Hotkeys.Open, Is.EqualTo(new[] { "<Primary>O" }));
        Assert.That(cfg.Hotkeys.Quit, Is.EqualTo(new[] { "<Primary>Q" }));
        Assert.That(cfg.Hotkeys.PlayPause, Is.EqualTo(new[] { "space" }));
        Assert.That(cfg.Hotkeys.ToggleFullscreen, Is.EqualTo(new[] { "f", "<Shift>F", "F11", "MouseDoubleClick1" }));
        Assert.That(cfg.Hotkeys.ExitFullscreen, Is.EqualTo(new[] { "Escape" }));
        Assert.That(cfg.Hotkeys.ToggleDiagnosticOverlay, Is.Empty);
        Assert.That(cfg.Hotkeys.ShowPreferences, Is.Empty);
        // Steady state for a fresh install: no file is written. The user creates one by hand or the preferences dialog writes one explicitly.
        Assert.That(File.Exists(path), Is.False);
    }

    [Test]
    public void LoadOrDefaultReadsExistingFile()
    {
        var path = Path.Combine(tempDir!, "config.toml");
        Directory.CreateDirectory(tempDir!);
        File.WriteAllText(path,
            "[hotkeys]\n" +
            "play_pause = [\"p\"]\n" +
            "toggle_fullscreen = [\"<Primary>F\"]\n");

        var cfg = UserConfig.LoadOrDefault(path);
        Assert.That(cfg.Hotkeys.PlayPause, Is.EqualTo(new[] { "p" }));
        Assert.That(cfg.Hotkeys.ToggleFullscreen, Is.EqualTo(new[] { "<Primary>F" }));
        // Keys not mentioned in the file fall back to the POCO default — touching one binding shouldn't reset the rest.
        Assert.That(cfg.Hotkeys.Open, Is.EqualTo(new[] { "<Primary>O" }));
    }

    [Test]
    public void MissingSectionFallsBackToDefaults()
    {
        var path = Path.Combine(tempDir!, "config.toml");
        Directory.CreateDirectory(tempDir!);
        // File exists but has no [hotkeys] section.
        File.WriteAllText(path, "# user comment, no sections\n");

        var cfg = UserConfig.LoadOrDefault(path);
        Assert.That(cfg.Hotkeys, Is.Not.Null);
        Assert.That(cfg.Hotkeys.PlayPause, Is.EqualTo(new[] { "space" }));
    }

    [Test]
    public void ExplicitlyEmptyListOverridesDefault()
    {
        // Distinguishes "user wants no binding" from "user didn't touch this key".
        var path = Path.Combine(tempDir!, "config.toml");
        Directory.CreateDirectory(tempDir!);
        File.WriteAllText(path, "[hotkeys]\nplay_pause = []\n");

        var cfg = UserConfig.LoadOrDefault(path);
        Assert.That(cfg.Hotkeys.PlayPause, Is.Empty);
    }

    [Test]
    public void MalformedTomlIsRotatedToBakAndDefaultsAreReturned()
    {
        var path = Path.Combine(tempDir!, "config.toml");
        Directory.CreateDirectory(tempDir!);
        const string broken = "[broken\n";
        File.WriteAllText(path, broken);

        var cfg = UserConfig.LoadOrDefault(path);

        // Defaults apply, app doesn't crash.
        Assert.That(cfg.Hotkeys.PlayPause, Is.EqualTo(new[] { "space" }));
        // Original path is gone — the broken file was rotated.
        Assert.That(File.Exists(path), Is.False);
        // Exactly one .bak was produced, and its contents are the original broken text (preserved verbatim for the user to recover).
        var bakFiles = Directory.GetFiles(tempDir!, "config.toml.malformed-*.bak");
        Assert.That(bakFiles, Has.Length.EqualTo(1));
        Assert.That(File.ReadAllText(bakFiles[0]), Is.EqualTo(broken));
    }

    [Test]
    public void MalformedTomlSurvivesARotationFailure()
    {
        // Approximate the rotation-fail path by chmod-ing the parent directory to read+execute (no write), so File.Move can't create the .bak. UserConfig should log to stderr (we don't capture it here) and still return defaults — the player must keep starting even if rotation fails.
        // Skipped on platforms where File.SetUnixFileMode on directories isn't honored (Windows). The if-block is what the analyzer needs to narrow the platform-gated APIs; an early-return pattern with Assert.Ignore would still trip CA1416.
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var path = Path.Combine(tempDir!, "config.toml");
            Directory.CreateDirectory(tempDir!);
            File.WriteAllText(path, "[broken\n");

            var dirInfo = new DirectoryInfo(tempDir!);
            var originalMode = File.GetUnixFileMode(dirInfo.FullName);
            try
            {
                File.SetUnixFileMode(dirInfo.FullName, UnixFileMode.UserRead | UnixFileMode.UserExecute);

                var cfg = UserConfig.LoadOrDefault(path);

                Assert.That(cfg.Hotkeys.PlayPause, Is.EqualTo(new[] { "space" }));
            }
            finally
            {
                // Restore so [TearDown]'s recursive delete can remove the directory.
                File.SetUnixFileMode(dirInfo.FullName, originalMode);
            }
        }
        else
        {
            Assert.Ignore("rotation-failure simulation requires POSIX directory permissions");
        }
    }

    [Test]
    public void ApplicationSectionDefaultsToReuseWindow()
    {
        // Missing [application] section in TOML — every pre-existing user config falls in here on upgrade. The Tomlyn POCO-default behavior is the contract we rely on for open-in-new-window-off-by-default to apply silently.
        var path = Path.Combine(tempDir!, "config.toml");
        Directory.CreateDirectory(tempDir!);
        File.WriteAllText(path, "[hotkeys]\nplay_pause = [\"p\"]\n");

        var cfg = UserConfig.LoadOrDefault(path);
        Assert.That(cfg.Application, Is.Not.Null);
        Assert.That(cfg.Application.OpenInNewWindow, Is.False);
    }

    [Test]
    public void ApplicationOpenInNewWindowHandEditedTomlReads()
    {
        // A user hand-writes the TOML file (not via Save). Exercises the snake_case naming policy on the read path directly, independent of Save's serialization shape. Catches the failure mode where Tomlyn's naming policy changes shape and Save→Load round-trips silently while hand-edited configs revert to default.
        var path = Path.Combine(tempDir!, "config.toml");
        Directory.CreateDirectory(tempDir!);
        File.WriteAllText(path, "[application]\nopen_in_new_window = true\n");

        var cfg = UserConfig.LoadOrDefault(path);
        Assert.That(cfg.Application.OpenInNewWindow, Is.True);
    }

    [Test]
    public void ApplicationOpenInNewWindowTrueRoundTrips()
    {
        var path = Path.Combine(tempDir!, "config.toml");
        Directory.CreateDirectory(tempDir!);

        var cfg = new UserConfig();
        cfg.Application.OpenInNewWindow = true;
        cfg.Save(path);

        var reloaded = UserConfig.LoadOrDefault(path);
        Assert.That(reloaded.Application.OpenInNewWindow, Is.True);
        // Hotkey defaults survive a setting-only save.
        Assert.That(reloaded.Hotkeys.Open, Is.EqualTo(new[] { "<Primary>O" }));
    }

    [Test]
    public void ApplicationThemeDefaultsToAuto()
    {
        // Missing [application] section — pre-existing configs land here on upgrade and must follow the system by default.
        var path = Path.Combine(tempDir!, "config.toml");
        Directory.CreateDirectory(tempDir!);
        File.WriteAllText(path, "[hotkeys]\nplay_pause = [\"p\"]\n");

        var cfg = UserConfig.LoadOrDefault(path);
        Assert.That(cfg.Application.Theme, Is.EqualTo("auto"));
    }

    [Test]
    public void ApplicationThemeHandEditedTomlReads()
    {
        var path = Path.Combine(tempDir!, "config.toml");
        Directory.CreateDirectory(tempDir!);
        File.WriteAllText(path, "[application]\ntheme = \"dark\"\n");

        var cfg = UserConfig.LoadOrDefault(path);
        Assert.That(cfg.Application.Theme, Is.EqualTo("dark"));
    }

    [Test]
    public void ApplicationThemeRoundTrips()
    {
        var path = Path.Combine(tempDir!, "config.toml");
        Directory.CreateDirectory(tempDir!);

        var cfg = new UserConfig();
        cfg.Application.Theme = "light";
        cfg.Save(path);

        var reloaded = UserConfig.LoadOrDefault(path);
        Assert.That(reloaded.Application.Theme, Is.EqualTo("light"));
        // Sibling application setting keeps its default through a theme-only save.
        Assert.That(reloaded.Application.OpenInNewWindow, Is.False);
    }

    [Test]
    public void ApplicationChapterSeekPrerollDefaultsToZero()
    {
        // Missing [application] section — pre-existing configs must default to "exactly at the cue".
        var path = Path.Combine(tempDir!, "config.toml");
        Directory.CreateDirectory(tempDir!);
        File.WriteAllText(path, "[hotkeys]\nplay_pause = [\"p\"]\n");

        var cfg = UserConfig.LoadOrDefault(path);
        Assert.That(cfg.Application.ChapterSeekPrerollSeconds, Is.EqualTo(0.0));
    }

    [Test]
    public void ApplicationChapterSeekPrerollHandEditedTomlReads()
    {
        var path = Path.Combine(tempDir!, "config.toml");
        Directory.CreateDirectory(tempDir!);
        File.WriteAllText(path, "[application]\nchapter_seek_preroll_seconds = 2.5\n");

        var cfg = UserConfig.LoadOrDefault(path);
        Assert.That(cfg.Application.ChapterSeekPrerollSeconds, Is.EqualTo(2.5));
    }

    [Test]
    public void ApplicationChapterSeekPrerollRoundTrips()
    {
        var path = Path.Combine(tempDir!, "config.toml");
        Directory.CreateDirectory(tempDir!);

        var cfg = new UserConfig();
        cfg.Application.ChapterSeekPrerollSeconds = 3.5;
        cfg.Save(path);

        var reloaded = UserConfig.LoadOrDefault(path);
        Assert.That(reloaded.Application.ChapterSeekPrerollSeconds, Is.EqualTo(3.5));
    }

    [Test]
    public void SaveRoundTripsThroughLoad()
    {
        var path = Path.Combine(tempDir!, "config.toml");
        Directory.CreateDirectory(tempDir!);

        var cfg = new UserConfig();
        cfg.Hotkeys.PlayPause = new() { "p", "<Primary>space" };
        cfg.Hotkeys.ToggleDiagnosticOverlay = new() { "<Primary>D" };
        cfg.Save(path);

        Assert.That(File.Exists(path), Is.True);

        var reloaded = UserConfig.LoadOrDefault(path);
        Assert.That(reloaded.Hotkeys.PlayPause, Is.EqualTo(new[] { "p", "<Primary>space" }));
        Assert.That(reloaded.Hotkeys.ToggleDiagnosticOverlay, Is.EqualTo(new[] { "<Primary>D" }));
        // Untouched keys round-trip from their defaults.
        Assert.That(reloaded.Hotkeys.Open, Is.EqualTo(new[] { "<Primary>O" }));
    }
}
