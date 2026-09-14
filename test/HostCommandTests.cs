using System;
using System.Diagnostics;
using Vomplayer.Services;

namespace Vomplayer.Tests;

// HostCommand.Run against real binaries: the report text must carry stdout, the exit code, a timeout verdict (and actually return promptly), and a start failure — every outcome the diagnostics dump can hit.
[TestFixture]
public class HostCommandTests
{
    [Test]
    public void RunCapturesOutputAndExitCode()
    {
        var text = HostCommand.Run("/bin/echo", new[] { "hello", "world" }, TimeSpan.FromSeconds(5));
        Assert.That(text, Does.Contain("hello world"));
        Assert.That(text, Does.Contain("exit 0"));
        Assert.That(text, Does.StartWith("$ /bin/echo hello world"));
    }

    [Test]
    public void RunReportsANonZeroExit()
    {
        var text = HostCommand.Run("/bin/false", Array.Empty<string>(), TimeSpan.FromSeconds(5));
        Assert.That(text, Does.Contain("exit 1"));
    }

    [Test]
    public void RunKillsAndReportsOnTimeout()
    {
        var sw = Stopwatch.StartNew();
        string text = string.Empty;
        Assert.That(() => text = HostCommand.Run("/bin/sleep", new[] { "10" }, TimeSpan.FromMilliseconds(300)), Throws.Nothing, "a probe always yields a line, never an exception");
        sw.Stop();
        Assert.That(text, Does.Contain("timed out"));
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public void RunReportsAStartFailure()
    {
        var text = HostCommand.Run("/nonexistent/binary", Array.Empty<string>(), TimeSpan.FromSeconds(5));
        Assert.That(text, Does.Contain("failed to start"));
    }

    // The sandbox/host argv seam, shared with the yt-dlp launcher: pure, so it's pinned here rather than trusted.
    [Test]
    public void BuildPrefixesFlatpakSpawnOnlyInsideASandbox()
    {
        Assert.That(HostCommand.Build(inFlatpak: false, new[] { "flatpak", "--version" }), Is.EqualTo(new[] { "flatpak", "--version" }));
        Assert.That(HostCommand.Build(inFlatpak: true, new[] { "flatpak", "--version" }), Is.EqualTo(new[] { "flatpak-spawn", "--host", "--watch-bus", "flatpak", "--version" }));
        Assert.That(() => HostCommand.Build(inFlatpak: true, Array.Empty<string>()), Throws.ArgumentException);
    }
}
