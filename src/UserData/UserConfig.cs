using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Tomlyn;

namespace Vomplayer.UserData;

// User-editable configuration backed by a TOML file. Loaded once at startup; defaults apply when the file is missing or when a key isn't set. Save() writes the current state back; today only the preferences dialog (hotkeys + application settings) calls it, but the file format is human-edit-friendly so users can also touch it directly.
//
// Malformed TOML doesn't crash the app: the broken file is rotated to <path>.malformed-<timestamp>.bak, a stderr line names both the parse error and the rotation target, and load proceeds with defaults. The user keeps their text (in the .bak) and the player keeps starting. A rotation that itself fails (read-only filesystem, etc.) logs both errors and continues with defaults but leaves the broken file in place — so next launch will warn again until the user resolves it.
public sealed class UserConfig
{
    // Trigger strings stay as raw text here so we can read/write the file without depending on GTK being initialized — config loads before gtk_init in Program.Main, and gtk_accelerator_parse / gtk_accelerator_name would then be unsafe to call. The HotkeyMap derived from this section is built later, on the GTK main thread, in MainWindow.
    //
    // Shaped as a plain action-key → trigger-strings dictionary rather than a fixed POCO so the schema has a single source of truth: HotkeyMap owns the action set (ActionToTomlKey / FromTomlForm) and the defaults (HotkeyMap.Default). A fixed-property section here once duplicated both and drifted — it silently dropped every action added after the original seven on save, and its stale copy of the play_pause default shadowed the real one. Missing keys fall back to defaults in HotkeyMap.FromTomlForm; an explicit `play_pause = []` survives as "no binding"; unknown keys warn there too.
    public Dictionary<string, List<string>> Hotkeys { get; set; } = new();
    public ApplicationSection Application { get; set; } = new();

    // Process-level behavior toggles. Today this is just the open-in-new-window switch, but the section exists as a stable home for future startup/runtime toggles (default volume, window-size memory, …) so we don't churn the schema every time one shows up.
    //
    // `open_in_new_window` defaults to false — a second `./vomplayer foo.mp4` invocation forwards the file to the running primary via GApplication's D-Bus handshake instead of spawning a fresh window. Set it true to make every command-line invocation spawn its own window instead. Pre-existing user TOMLs without this section fall back to the POCO default, so they keep the reuse-window behavior.
    // `theme` is the appearance preference: "auto" (follow the desktop), "light", or "dark". Stored as a raw string — not the ThemeMode enum — so this POCO stays GTK-free (config loads before gtk_init) and the file stays human-editable; MainWindow parses it into a ThemeMode after init, falling back to auto on anything unrecognized. Defaults to "auto" so pre-existing TOMLs and fresh installs follow the system.
    // `chapter_seek_preroll_seconds` shifts chapter seeks (marker clicks and next/previous-chapter) to land this many seconds *before* the chapter's cue, for a short lead-in. Defaults to 0.0 (land exactly on the cue). The value is read at startup and on each preferences save, then applied by ViewModelMain via the pure ChapterStep resolver.
    public sealed class ApplicationSection
    {
        public bool OpenInNewWindow { get; set; } = false;
        public string Theme { get; set; } = "auto";
        public double ChapterSeekPrerollSeconds { get; set; } = 0.0;
    }

    // Tomlyn 2.x reuses System.Text.Json.JsonNamingPolicy for property naming. SnakeCaseLower maps PascalCase POCO members to snake_case TOML keys: ApplicationSection.OpenInNewWindow becomes [application].open_in_new_window. Dictionary keys (the [hotkeys] section) pass through untouched. SourceName feeds Tomlyn's diagnostics so a parse-error message names the file we were reading instead of leaving SourceName blank.
    private static TomlSerializerOptions BuildOptions(string sourceName)
    {
        return new TomlSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            SourceName = sourceName,
        };
    }

    public static UserConfig LoadOrDefault(string path)
    {
        if (!File.Exists(path))
        {
            return new UserConfig();
        }
        var text = File.ReadAllText(path);
        try
        {
            // Tomlyn's signature is nullable for the no-data case, but for our root POCO Deserialize returns a non-null instance for any well-formed input (including empty); a null here would mean the parser changed shape under us. Throw rather than return defaults silently — that would mask a real bug.
            var loaded = TomlSerializer.Deserialize<UserConfig>(text, BuildOptions(path));
            if (loaded == null)
            {
                throw new InvalidOperationException($"TomlSerializer.Deserialize returned null for {path}");
            }
            return loaded;
        }
        catch (TomlException ex)
        {
            RotateBrokenFile(path, ex);
            return new UserConfig();
        }
    }

    // Writes the current state to disk, creating the parent directory if needed. Atomic via tmp-file + File.Move-with-overwrite so a crash mid-write doesn't leave a half-written config.toml that the next launch would rotate to .bak.
    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var text = TomlSerializer.Serialize(this, BuildOptions(path));
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    // ISO 8601 basic format (no separators) so the suffix sorts lexically and is filesystem-safe on every platform we care about. The Z marks UTC; the timestamp is fine-grained enough that two same-second rotations from sibling processes won't typically collide, and File.Move's overwrite=false behavior would surface a collision rather than silently clobber a prior .bak.
    private static void RotateBrokenFile(string path, TomlException parseError)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var bakPath = $"{path}.malformed-{stamp}.bak";
        try
        {
            File.Move(path, bakPath);
            Console.Error.WriteLine(
                $"[vompl] config: {path} failed to parse ({parseError.Message}); preserved as {bakPath}, continuing with defaults");
        }
        catch (Exception moveEx)
        {
            // Rotation failed (read-only filesystem, permissions, name collision). Log both errors so a bug report has the full chain; continue with defaults so the player still starts. Next launch will hit the same path and try to rotate again — that's fine, it'll just log again until the user resolves it.
            Console.Error.WriteLine(
                $"[vompl] config: {path} failed to parse ({parseError.Message}); rotation to {bakPath} also failed ({moveEx.Message}); continuing with defaults but the broken file is still in place");
        }
    }
}
