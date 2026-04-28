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

        Assert.That(cfg.Placeholder.Example, Is.EqualTo("hello"));
        // Steady state for a fresh install: no file is written. The user creates one by hand if they want to override.
        Assert.That(File.Exists(path), Is.False);
    }

    [Test]
    public void LoadOrDefaultReadsExistingFile()
    {
        var path = Path.Combine(tempDir!, "config.toml");
        Directory.CreateDirectory(tempDir!);
        File.WriteAllText(path, "[placeholder]\nexample = \"world\"\n");

        var cfg = UserConfig.LoadOrDefault(path);
        Assert.That(cfg.Placeholder.Example, Is.EqualTo("world"));
    }

    [Test]
    public void MissingSectionFallsBackToDefaults()
    {
        var path = Path.Combine(tempDir!, "config.toml");
        Directory.CreateDirectory(tempDir!);
        // File exists but has no [placeholder] section.
        File.WriteAllText(path, "# user comment, no sections\n");

        var cfg = UserConfig.LoadOrDefault(path);
        Assert.That(cfg.Placeholder, Is.Not.Null);
        Assert.That(cfg.Placeholder.Example, Is.EqualTo("hello"));
    }

    [Test]
    public void SectionExistsWithMissingKeyFallsBackToDefault()
    {
        var path = Path.Combine(tempDir!, "config.toml");
        Directory.CreateDirectory(tempDir!);
        // [placeholder] declared but `example` not set — POCO default should win.
        File.WriteAllText(path, "[placeholder]\n");

        var cfg = UserConfig.LoadOrDefault(path);
        Assert.That(cfg.Placeholder.Example, Is.EqualTo("hello"));
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
        Assert.That(cfg.Placeholder.Example, Is.EqualTo("hello"));
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

                Assert.That(cfg.Placeholder.Example, Is.EqualTo("hello"));
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
}
