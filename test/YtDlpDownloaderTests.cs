using NUnit.Framework;
using Vomplayer.Services;

namespace Vomplayer.Tests;

// Only the line-parsing surface is unit-testable here — anything that spawns the actual binary belongs in integration tests / manual smoke. TryParseProgress is internal and reachable via InternalsVisibleTo.
[TestFixture]
public class YtDlpDownloaderTests
{
    [Test]
    public void TryParseProgressParsesNumericFields()
    {
        bool ok = YtDlpDownloader.TryParseProgress("VOMPLPROG 1234 5678 NA downloading", out var p);
        Assert.That(ok, Is.True);
        Assert.That(p.DownloadedBytes, Is.EqualTo(1234));
        Assert.That(p.TotalBytes, Is.EqualTo(5678));
        Assert.That(p.Status, Is.EqualTo("downloading"));
    }

    [Test]
    public void TryParseProgressTreatsNAAsNullTotal()
    {
        bool ok = YtDlpDownloader.TryParseProgress("VOMPLPROG 4096 NA NA downloading", out var p);
        Assert.That(ok, Is.True);
        Assert.That(p.DownloadedBytes, Is.EqualTo(4096));
        Assert.That(p.TotalBytes, Is.Null);
    }

    [Test]
    public void TryParseProgressRejectsLineWithoutPrefix()
    {
        bool ok = YtDlpDownloader.TryParseProgress("[download] 12.3% of 100MiB", out _);
        Assert.That(ok, Is.False);
    }

    [Test]
    public void TryParseProgressRejectsTooFewFields()
    {
        bool ok = YtDlpDownloader.TryParseProgress("VOMPLPROG 1234", out _);
        Assert.That(ok, Is.False);
    }

    [Test]
    public void TryParseProgressTreatsUnparseableDownloadedAsZero()
    {
        // NA-as-downloaded happens early in the connection phase before yt-dlp knows the size. We map to 0 so the dialog renders as 0% rather than crashing.
        bool ok = YtDlpDownloader.TryParseProgress("VOMPLPROG NA 9999 NA downloading", out var p);
        Assert.That(ok, Is.True);
        Assert.That(p.DownloadedBytes, Is.EqualTo(0));
        Assert.That(p.TotalBytes, Is.EqualTo(9999));
    }

    [Test]
    public void TryParseProgressFallsBackToEstimateWhenTotalIsNA()
    {
        // HLS/DASH downloads: total_bytes is NA and the usable figure lands in total_bytes_estimate (a float). The bar should be determinate off the estimate.
        bool ok = YtDlpDownloader.TryParseProgress("VOMPLPROG 4096 NA 12345678.0 downloading", out var p);
        Assert.That(ok, Is.True);
        Assert.That(p.TotalBytes, Is.EqualTo(12345678L));
        Assert.That(p.DownloadedBytes, Is.EqualTo(4096));
    }

    [Test]
    public void TryParseProgressHandlesFinishedStatus()
    {
        bool ok = YtDlpDownloader.TryParseProgress("VOMPLPROG 50000 50000 NA finished", out var p);
        Assert.That(ok, Is.True);
        Assert.That(p.Status, Is.EqualTo("finished"));
    }

    [Test]
    public void BuildCommandOutsideFlatpakIsBareBinary()
    {
        // Outside a sandbox we invoke yt-dlp directly on PATH — no wrapping.
        Assert.That(YtDlpDownloader.BuildCommand(false), Is.EqualTo(new[] { "yt-dlp" }));
    }

    [Test]
    public void BuildCommandInsideFlatpakWrapsWithHostSpawn()
    {
        // Full ordered sequence: --host must precede the command, and --watch-bus must not silently drop out (it's what tears down the host yt-dlp on cancel/crash).
        Assert.That(
            YtDlpDownloader.BuildCommand(true),
            Is.EqualTo(new[] { "flatpak-spawn", "--host", "--watch-bus", "yt-dlp" }));
    }

    [Test]
    public void ConstructorRejectsEmptyCommand()
    {
        var cache = new UrlDownloadCache(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vompl-ctor-test"));
        Assert.Throws<System.ArgumentException>(() => new YtDlpDownloader(cache, System.Array.Empty<string>()));
    }
}
