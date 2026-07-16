using System;
using System.IO;
using Vomplayer.UserData;

namespace Vomplayer.Tests;

[TestFixture]
public class UserDataPathsTests
{
    private string? savedConfigDir;
    private string? savedStateDir;

    [SetUp]
    public void Save()
    {
        savedConfigDir = Environment.GetEnvironmentVariable("VOMPL_CONFIG_DIR");
        savedStateDir = Environment.GetEnvironmentVariable("VOMPL_STATE_DIR");
        savedCacheDir = Environment.GetEnvironmentVariable("VOMPL_CACHE_DIR");
    }

    [TearDown]
    public void Restore()
    {
        Environment.SetEnvironmentVariable("VOMPL_CONFIG_DIR", savedConfigDir);
        Environment.SetEnvironmentVariable("VOMPL_STATE_DIR", savedStateDir);
        Environment.SetEnvironmentVariable("VOMPL_CACHE_DIR", savedCacheDir);
    }

    private string? savedCacheDir;

    [Test]
    public void CacheDirOverrideHonoursEnvVarAndRootsTheUrlCache()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vompl-test-cache-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("VOMPL_CACHE_DIR", dir);
        Assert.That(UserDataPaths.CacheDir, Is.EqualTo(dir));
        Assert.That(UserDataPaths.UrlDownloadCacheRoot, Is.EqualTo(Path.Combine(dir, "url-downloads")));
    }

    [Test]
    public void ConfigDirOverrideHonoursEnvVar()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vompl-test-cfg-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("VOMPL_CONFIG_DIR", dir);
        Assert.That(UserDataPaths.ConfigDir, Is.EqualTo(dir));
        Assert.That(UserDataPaths.ConfigFile, Is.EqualTo(Path.Combine(dir, "config.toml")));
    }

    [Test]
    public void StateDirOverrideHonoursEnvVar()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vompl-test-state-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("VOMPL_STATE_DIR", dir);
        Assert.That(UserDataPaths.StateDir, Is.EqualTo(dir));
        Assert.That(UserDataPaths.StateDb, Is.EqualTo(Path.Combine(dir, "state.db")));
    }

    [Test]
    public void EmptyOverrideFallsThroughToSpecialFolder()
    {
        Environment.SetEnvironmentVariable("VOMPL_CONFIG_DIR", "");
        // Empty string must not be treated as the resolved directory — that would dump files into CWD.
        Assert.That(UserDataPaths.ConfigDir, Is.Not.Empty);
        Assert.That(Path.IsPathRooted(UserDataPaths.ConfigDir), Is.True);
    }
}
