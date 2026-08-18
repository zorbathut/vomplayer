using System;
using Vomplayer.UserData;

namespace Vomplayer.Tests;

[TestFixture]
public class CookieSourceTests
{
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void ParseTreatsBlankAsNone(string? raw)
    {
        Assert.That(CookieSource.Parse(raw), Is.EqualTo(new CookieSourceChoice(CookieSourceKind.None, "")));
    }

    [Test]
    public void BrowsersMatchesYtDlpsSupportedSet()
    {
        // Pinned as literals, not derived from CookieSource.Browsers — the risk worth guarding is this list drifting from yt-dlp's own SUPPORTED_BROWSERS, and a test that iterates the list under test cannot see that. Verified against yt-dlp 2026.07.04.
        Assert.That(CookieSource.Browsers, Is.EqualTo(new[] { "brave", "chrome", "chromium", "edge", "firefox", "opera", "safari", "vivaldi", "whale" }));
    }

    [TestCase("brave")]
    [TestCase("chrome")]
    [TestCase("chromium")]
    [TestCase("edge")]
    [TestCase("firefox")]
    [TestCase("opera")]
    [TestCase("safari")]
    [TestCase("vivaldi")]
    [TestCase("whale")]
    public void ParseRecognizesEveryBrowserYtDlpSupports(string browser)
    {
        Assert.That(CookieSource.Parse(browser), Is.EqualTo(new CookieSourceChoice(CookieSourceKind.Browser, browser)));
    }

    [TestCase("Firefox")]
    [TestCase("FIREFOX")]
    [TestCase("  firefox  ")]
    public void ParseNormalizesBrowserNamesToYtDlpsSpelling(string raw)
    {
        // Case and whitespace are user typing artifacts; the canonical lowercase name is what we hand to yt-dlp and write back to config.
        Assert.That(CookieSource.Parse(raw), Is.EqualTo(new CookieSourceChoice(CookieSourceKind.Browser, "firefox")));
    }

    [TestCase("firefox:Profile 2::work")]
    [TestCase("chrome+gnomekeyring")]
    [TestCase("chromium:/home/u/.config/chromium")]
    public void ParseKeepsAFullSpecVerbatimAsCustom(string raw)
    {
        Assert.That(CookieSource.Parse(raw), Is.EqualTo(new CookieSourceChoice(CookieSourceKind.Custom, raw)));
    }

    [Test]
    public void ParseKeepsAnUnknownBrowserRatherThanDroppingIt()
    {
        // "netscape" isn't a yt-dlp browser, but silently rewriting it to None would throw away what the user typed. Custom preserves it; ValidationError is what tells them it's wrong.
        Assert.That(CookieSource.Parse("netscape"), Is.EqualTo(new CookieSourceChoice(CookieSourceKind.Custom, "netscape")));
    }

    [Test]
    public void ToConfigStringNoneIsEmptyRegardlessOfValue()
    {
        Assert.That(CookieSource.ToConfigString(CookieSourceKind.None, "firefox"), Is.EqualTo(""));
    }

    [Test]
    public void ToConfigStringBrowserIsTheBareName()
    {
        Assert.That(CookieSource.ToConfigString(CookieSourceKind.Browser, "firefox"), Is.EqualTo("firefox"));
    }

    [Test]
    public void ToConfigStringCustomTrimsAndBlankCollapsesToNone()
    {
        Assert.That(CookieSource.ToConfigString(CookieSourceKind.Custom, "  firefox:x  "), Is.EqualTo("firefox:x"));
        // Picking Custom and typing nothing is indistinguishable from None once written, and reads back as None. Accepted consequence of storing one derived string.
        Assert.That(CookieSource.ToConfigString(CookieSourceKind.Custom, "   "), Is.EqualTo(""));
    }

    [Test]
    public void ToConfigStringThrowsOnUnknownKind()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CookieSource.ToConfigString((CookieSourceKind)99, "x"));
    }

    [TestCase("")]
    [TestCase("firefox")]
    [TestCase("firefox:Profile 2::work")]
    [TestCase("chrome+gnomekeyring")]
    [TestCase("Chromium")]
    [TestCase("  edge  ")]
    public void ValidationErrorAcceptsAnythingWhoseBrowserNameYtDlpKnows(string spec)
    {
        // Only the browser name is checkable before yt-dlp has a URL — profile paths, keyrings and containers are validated later, by yt-dlp, against the real filesystem.
        Assert.That(CookieSource.ValidationError(spec, isMacOs: false), Is.Null);
    }

    [Test]
    public void ValidationErrorAcceptsNull()
    {
        Assert.That(CookieSource.ValidationError(null, isMacOs: false), Is.Null);
    }

    [TestCase("netscape")]
    [TestCase("netscape:profile")]
    [TestCase("ff+kwallet")]
    public void ValidationErrorRejectsAnUnknownBrowserAndNamesTheSupportedOnes(string spec)
    {
        var message = CookieSource.ValidationError(spec, isMacOs: false);
        Assert.That(message, Is.Not.Null);
        Assert.That(message, Does.Contain("firefox"));
    }

    [TestCase("safari")]
    [TestCase("Safari")]
    [TestCase("safari:profile")]
    public void ValidationErrorRejectsSafariOffMacOs(string spec)
    {
        // yt-dlp's Safari cookie support raises "unsupported platform: linux" — this is the one supported-browser choice that is guaranteed to fail rather than merely likely to, so it's worth catching at save time instead of on every URL load afterwards.
        Assert.That(CookieSource.ValidationError(spec, isMacOs: false), Does.Contain("macOS"));
        Assert.That(CookieSource.ValidationError(spec, isMacOs: true), Is.Null);
    }

    [Test]
    public void RowForPlacesNoneFirstBrowsersInOrderAndCustomLast()
    {
        Assert.That(CookieSource.RowFor(new CookieSourceChoice(CookieSourceKind.None, "")), Is.EqualTo(0));
        Assert.That(CookieSource.RowFor(new CookieSourceChoice(CookieSourceKind.Browser, "brave")), Is.EqualTo(1));
        Assert.That(CookieSource.RowFor(new CookieSourceChoice(CookieSourceKind.Browser, "whale")), Is.EqualTo(CookieSource.Browsers.Count));
        Assert.That(CookieSource.RowFor(new CookieSourceChoice(CookieSourceKind.Custom, "firefox:x")), Is.EqualTo(CookieSource.CustomRow));
    }

    [Test]
    public void SpecForRowIsTheInverseOfRowForAcrossEveryRow()
    {
        // Walks the whole model, so an off-by-one anywhere in the mapping shows up as the wrong browser rather than passing silently.
        Assert.That(CookieSource.SpecForRow(0, "ignored"), Is.EqualTo(""));
        for (int i = 0; i < CookieSource.Browsers.Count; i++)
        {
            Assert.That(CookieSource.SpecForRow(i + 1, "ignored"), Is.EqualTo(CookieSource.Browsers[i]), $"row {i + 1}");
        }
        Assert.That(CookieSource.SpecForRow(CookieSource.CustomRow, "firefox:x"), Is.EqualTo("firefox:x"));
    }

    [TestCase(-1)]
    [TestCase(int.MinValue)]
    public void SpecForRowFoldsBelowRangeToNone(int row)
    {
        // Gtk.DropDown reports GTK_INVALID_LIST_POSITION when deselected, which casts to -1. Folding beats indexing off the front of the browser list.
        Assert.That(CookieSource.SpecForRow(row, "firefox:x"), Is.EqualTo(""));
    }

    [Test]
    public void SpecForRowFoldsAboveRangeToCustom()
    {
        Assert.That(CookieSource.SpecForRow(CookieSource.CustomRow + 5, "firefox:x"), Is.EqualTo("firefox:x"));
    }

    [TestCase("")]
    [TestCase("firefox")]
    [TestCase("firefox:Profile 2::work")]
    [TestCase("netscape")]
    public void ParseAndToConfigStringRoundTrip(string raw)
    {
        var choice = CookieSource.Parse(raw);
        Assert.That(CookieSource.ToConfigString(choice.Kind, choice.Value), Is.EqualTo(raw));
    }
}
