using System;
using System.IO;

namespace Vomplayer.UserData;

// Resolves where the TOML config file and the SQLite state DB live on disk. Two split locations because the access patterns differ: config is read-mostly and human-edited (XDG_CONFIG_HOME family on Linux); state is incremental and machine-written (XDG_DATA_HOME family). The freedesktop spec's preferred slot for incremental state is XDG_STATE_HOME (~/.local/state), not XDG_DATA_HOME (~/.local/share), but .NET has no SpecialFolder for STATE_HOME and rolling our own resolver to honor it would mean re-implementing the cross-platform fallback chain. Recents ("recently used files") is named in the spec's XDG_DATA_HOME examples, so the placement is at least defensible. VOMPL_CONFIG_DIR / VOMPL_STATE_DIR override the resolved directory wholesale, used by tests and by anyone running a portable install. The override is the directory, not the file path, so the same env var can host config.toml and state.db side by side without a second var.
public static class UserDataPaths
{
    private const string AppDirName = "vomplayer";
    private const string ConfigFileName = "config.toml";
    private const string StateDbFileName = "state.db";

    public static string ConfigDir
    {
        get
        {
            var ov = Environment.GetEnvironmentVariable("VOMPL_CONFIG_DIR");
            if (!string.IsNullOrEmpty(ov))
            {
                return ov;
            }
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppDirName);
        }
    }

    public static string StateDir
    {
        get
        {
            var ov = Environment.GetEnvironmentVariable("VOMPL_STATE_DIR");
            if (!string.IsNullOrEmpty(ov))
            {
                return ov;
            }
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppDirName);
        }
    }

    // Cache for ephemeral, regenerable artifacts (yt-dlp downloads etc). Per the freedesktop spec the right slot is XDG_CACHE_HOME (~/.cache); .NET has no SpecialFolder for that on Linux either, so we use LocalApplicationData with a "cache" suffix — a defensible match in the same spirit as StateDir's compromise. VOMPL_CACHE_DIR overrides for tests / portable installs.
    public static string CacheDir
    {
        get
        {
            var ov = Environment.GetEnvironmentVariable("VOMPL_CACHE_DIR");
            if (!string.IsNullOrEmpty(ov))
            {
                return ov;
            }
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppDirName,
                "cache");
        }
    }

    public static string ConfigFile
    {
        get { return Path.Combine(ConfigDir, ConfigFileName); }
    }

    public static string StateDb
    {
        get { return Path.Combine(StateDir, StateDbFileName); }
    }

    // Per-URL yt-dlp download cache root. Sits under CacheDir so the override env var (VOMPL_CACHE_DIR) catches it for tests / portable installs without a separate variable. Symmetric with ConfigFile / StateDb — exposing the full path here keeps the literal "url-downloads" out of consumer call sites.
    public static string UrlDownloadCacheRoot
    {
        get { return Path.Combine(CacheDir, "url-downloads"); }
    }
}
