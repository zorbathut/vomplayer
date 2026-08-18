using System;
using System.Collections.Generic;

namespace Vomplayer.UserData;

// Which cookie source the user picked for yt-dlp's --cookies-from-browser. None = don't pass the flag at all (the default, and the behavior before this option existed). Browser = one of yt-dlp's supported browsers, used bare. Custom = a full spec the user typed, which may carry a keyring, profile path, and/or Firefox container.
public enum CookieSourceKind
{
    None,
    Browser,
    Custom,
}

// A parsed cookie-source setting. Value is "" for None, the yt-dlp browser name for Browser, and the raw spec for Custom.
public sealed record CookieSourceChoice(CookieSourceKind Kind, string Value);

// Pure, GTK-free helpers for the [yt_dlp] cookies_from_browser config value. Mirrors the raw-string-in-config / parse-later pattern used by ThemeModeParser and Hotkeys: config.toml stores one string, and the dropdown-vs-textfield split the preferences dialog shows is *derived* from it rather than stored alongside it, so the two can never disagree and hand-editing the file stays a one-step operation.
//
// The dropdown's row ordering deliberately lives in PreferencesDialog (like the existing ThemeOrder array), not here — this class stays free of presentation concerns so it's usable from UserConfig, which loads before gtk_init.
public static class CookieSource
{
    // yt-dlp's SUPPORTED_BROWSERS, in yt-dlp's own spelling — the strings are passed through to --cookies-from-browser verbatim, and double as the dropdown's row set and the validation whitelist. The list isn't filtered by platform, so a config file stays portable across machines; the one choice that can never work locally is caught by ValidationError instead, which keeps the row visible and explains itself rather than silently vanishing.
    public static IReadOnlyList<string> Browsers { get; } = new[] { "brave", "chrome", "chromium", "edge", "firefox", "opera", "safari", "vivaldi", "whale" };

    // Split the stored string into the (dropdown row, textfield contents) pair the preferences dialog renders. Unlike ThemeModeParser.Parse there's no warn callback, because there's no fallback happening: a token that isn't a bare browser name is a *legitimate* Custom spec (profile paths, keyrings, containers all live there), not an unrecognized value we're quietly replacing. A genuinely bogus browser name is caught by ValidationError at save time instead.
    public static CookieSourceChoice Parse(string? raw)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return new CookieSourceChoice(CookieSourceKind.None, "");
        }
        foreach (var browser in Browsers)
        {
            if (string.Equals(trimmed, browser, StringComparison.OrdinalIgnoreCase))
            {
                // Normalize to yt-dlp's own spelling so a hand-edited "Firefox" round-trips to "firefox".
                return new CookieSourceChoice(CookieSourceKind.Browser, browser);
            }
        }
        return new CookieSourceChoice(CookieSourceKind.Custom, trimmed);
    }

    // The string written to config.toml. Custom with nothing typed collapses to "" and therefore reads back as None — the dialog rejects that combination before it reaches here, but the collapse keeps a hand-edited empty value meaningful.
    public static string ToConfigString(CookieSourceKind kind, string value)
    {
        switch (kind)
        {
            case CookieSourceKind.None:
                return "";
            case CookieSourceKind.Browser:
                return value.Trim();
            case CookieSourceKind.Custom:
                return value.Trim();
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown CookieSourceKind");
        }
    }

    // Returns a user-facing message if `spec` names a browser yt-dlp doesn't support, else null. Deliberately checks *only* the browser name: that is exactly and only what yt-dlp itself validates before it has a URL (`yt-dlp --cookies-from-browser notabrowser` exits 2 on the browser name; `--cookies-from-browser firefox:/nonexistent` gets as far as complaining about the missing URL). Profile paths, keyrings and containers are checked later by yt-dlp against the real filesystem, so guessing at them here would be a home-grown grammar that could only be wrong. Reuses the same Browsers list the dropdown is built from, so the two can't drift apart.
    public static string? ValidationError(string? spec, bool isMacOs)
    {
        var trimmed = (spec ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }
        // The spec is BROWSER[+KEYRING][:PROFILE][::CONTAINER]; the browser name runs up to the first '+' or ':'.
        int end = trimmed.IndexOfAny(new[] { '+', ':' });
        var browser = end < 0 ? trimmed : trimmed.Substring(0, end);
        if (string.Equals(browser, "safari", StringComparison.OrdinalIgnoreCase) && !isMacOs)
        {
            return "Safari cookies are only available on macOS.";
        }
        foreach (var known in Browsers)
        {
            if (string.Equals(browser, known, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }
        return $"Unsupported browser \"{browser}\". Supported browsers are: {string.Join(", ", Browsers)}";
    }

    // Dropdown row ordering: row 0 is None, rows 1..Browsers.Count are Browsers in order, and the row after those is Custom. The arithmetic lives here rather than in PreferencesDialog so it's unit-testable — an off-by-one would otherwise silently select the wrong browser in code no test can reach. The dialog still owns the display strings, and builds them by walking Browsers in this same order.
    public static int CustomRow
    {
        get { return Browsers.Count + 1; }
    }

    public static int RowFor(CookieSourceChoice choice)
    {
        switch (choice.Kind)
        {
            case CookieSourceKind.None:
                return 0;
            case CookieSourceKind.Browser:
                for (int i = 0; i < Browsers.Count; i++)
                {
                    if (Browsers[i] == choice.Value)
                    {
                        return i + 1;
                    }
                }
                // Parse only ever returns Browser for a name it found in this same list.
                throw new ArgumentOutOfRangeException(nameof(choice), choice.Value, "browser not in CookieSource.Browsers");
            case CookieSourceKind.Custom:
                return CustomRow;
            default:
                throw new ArgumentOutOfRangeException(nameof(choice), choice.Kind, "unknown CookieSourceKind");
        }
    }

    // The config string a given row + textfield state means. Rows outside the model fold to the nearest end rather than indexing off the browser list — GTK_INVALID_LIST_POSITION casts to -1, and any row past the end is Custom either way.
    public static string SpecForRow(int row, string customText)
    {
        if (row <= 0)
        {
            return ToConfigString(CookieSourceKind.None, "");
        }
        if (row >= CustomRow)
        {
            return ToConfigString(CookieSourceKind.Custom, customText);
        }
        return ToConfigString(CookieSourceKind.Browser, Browsers[row - 1]);
    }
}
