using System;

namespace Vomplayer.UserData;

// Which light/dark appearance the user wants. Auto follows the desktop (the value GTK reports at launch); Light/Dark force the GTK4 `gtk-application-prefer-dark-theme` setting one way or the other. See ThemeModeParser for the string<->enum mapping and the prefer-dark resolution; the GTK application lives in MainWindow.
public enum ThemeMode
{
    Auto,
    Light,
    Dark,
}

// Pure, GTK-free helpers for ThemeMode. Kept free of any GTK dependency so it's usable from UserConfig (which loads before gtk_init) and unit-testable in isolation. Mirrors the raw-string-in-config / parse-later pattern used by Hotkeys.
public static class ThemeModeParser
{
    // Parse the config string form. Case-insensitive; trims surrounding whitespace. Anything unrecognized (including empty) falls back to Auto and reports via warn — never a silent fallback, per the project's no-silent-error policy.
    public static ThemeMode Parse(string? raw, Action<string> warn)
    {
        if (warn == null)
        {
            throw new ArgumentNullException(nameof(warn));
        }
        var trimmed = (raw ?? string.Empty).Trim();
        switch (trimmed.ToLowerInvariant())
        {
            case "auto":
                return ThemeMode.Auto;
            case "light":
                return ThemeMode.Light;
            case "dark":
                return ThemeMode.Dark;
            default:
                warn($"config: unrecognized theme \"{raw}\"; falling back to auto");
                return ThemeMode.Auto;
        }
    }

    // The string written to config.toml. Stable, lowercase, human-friendly.
    public static string ToConfigString(ThemeMode mode)
    {
        switch (mode)
        {
            case ThemeMode.Auto:
                return "auto";
            case ThemeMode.Light:
                return "light";
            case ThemeMode.Dark:
                return "dark";
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "unknown ThemeMode");
        }
    }

    // Map a mode onto GTK4's `gtk-application-prefer-dark-theme` boolean. Light/Dark force the value; Auto returns the system's value (the caller captures GTK's launch-time setting and passes it as systemDefault).
    public static bool ResolvePreferDark(ThemeMode mode, bool systemDefault)
    {
        switch (mode)
        {
            case ThemeMode.Light:
                return false;
            case ThemeMode.Dark:
                return true;
            case ThemeMode.Auto:
                return systemDefault;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "unknown ThemeMode");
        }
    }
}
