using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Tomlyn;

namespace Vomplayer.UserData;

// User-editable configuration backed by a TOML file. Loaded once at startup; defaults apply when the file is missing or when a key isn't set. Save() writes the current state back; today only the hotkeys preferences dialog calls it, but the file format is human-edit-friendly so users can also touch it directly.
//
// Malformed TOML doesn't crash the app: the broken file is rotated to <path>.malformed-<timestamp>.bak, a stderr line names both the parse error and the rotation target, and load proceeds with defaults. The user keeps their text (in the .bak) and the player keeps starting. A rotation that itself fails (read-only filesystem, etc.) logs both errors and continues with defaults but leaves the broken file in place — so next launch will warn again until the user resolves it.
public sealed class UserConfig
{
    public HotkeysSection Hotkeys { get; set; } = new();
    public ApplicationSection Application { get; set; } = new();

    // Process-level behavior toggles. Today this is just the single-instance switch, but the section exists as a stable home for future startup/runtime toggles (default volume, window-size memory, …) so we don't churn the schema every time one shows up.
    //
    // `single_instance` defaults to true — a second `./vomplayer foo.mp4` invocation forwards the file to the running primary via GApplication's D-Bus handshake instead of spawning a fresh window. Pre-existing user TOMLs without this section fall back to the POCO default, so the upgrade is transparent.
    // `theme` is the appearance preference: "auto" (follow the desktop), "light", or "dark". Stored as a raw string — not the ThemeMode enum — so this POCO stays GTK-free (config loads before gtk_init) and the file stays human-editable; MainWindow parses it into a ThemeMode after init, falling back to auto on anything unrecognized. Defaults to "auto" so pre-existing TOMLs and fresh installs follow the system.
    public sealed class ApplicationSection
    {
        public bool SingleInstance { get; set; } = true;
        public string Theme { get; set; } = "auto";
    }

    // Trigger strings stay as raw text here so we can read/write the file without depending on GTK being initialized — config loads before gtk_init in Program.Main, and gtk_accelerator_parse / gtk_accelerator_name would then be unsafe to call. The HotkeyMap derived from this section is built later, on the GTK main thread, in MainWindow.
    //
    // Default values match the pre-customization behavior wired into MainWindow before this section existed: f / Shift+F / F11 / double-click for fullscreen, Space for play/pause, Escape for exit-fullscreen, Ctrl+O / Ctrl+Q for the file menu. Diagnostic overlay and Preferences have no key by default — the menu items are how a fresh install reaches them.
    //
    // Tomlyn behavior: if a section is present but a key is missing, the POCO default wins (the deserializer doesn't touch unset properties). If the user writes `play_pause = []`, that empty list survives — interpreted as "no binding for play/pause", which is the intended override semantics.
    public sealed class HotkeysSection
    {
        public List<string> Open { get; set; } = new() { "<Primary>O" };
        public List<string> Quit { get; set; } = new() { "<Primary>Q" };
        public List<string> PlayPause { get; set; } = new() { "space" };
        public List<string> ToggleFullscreen { get; set; } = new() { "f", "<Shift>F", "F11", "MouseDoubleClick1" };
        public List<string> ExitFullscreen { get; set; } = new() { "Escape" };
        public List<string> ToggleDiagnosticOverlay { get; set; } = new();
        public List<string> ShowPreferences { get; set; } = new();

        public IReadOnlyDictionary<string, List<string>> ToDictionary()
        {
            return new Dictionary<string, List<string>>
            {
                ["open"] = Open,
                ["quit"] = Quit,
                ["play_pause"] = PlayPause,
                ["toggle_fullscreen"] = ToggleFullscreen,
                ["exit_fullscreen"] = ExitFullscreen,
                ["toggle_diagnostic_overlay"] = ToggleDiagnosticOverlay,
                ["show_preferences"] = ShowPreferences,
            };
        }

        public static HotkeysSection FromDictionary(IReadOnlyDictionary<string, List<string>> raw)
        {
            var section = new HotkeysSection();
            // Each property starts at its default; only overwrite when the dict has the key. The action enum's TOML key set is closed (HotkeyMap.ActionToTomlKey is the only writer) so we don't need an unknown-key warning here — that lives in HotkeyMap.FromTomlForm where the user-facing parsing happens.
            if (raw.TryGetValue("open", out var open)) { section.Open = new List<string>(open); }
            if (raw.TryGetValue("quit", out var quit)) { section.Quit = new List<string>(quit); }
            if (raw.TryGetValue("play_pause", out var pp)) { section.PlayPause = new List<string>(pp); }
            if (raw.TryGetValue("toggle_fullscreen", out var fs)) { section.ToggleFullscreen = new List<string>(fs); }
            if (raw.TryGetValue("exit_fullscreen", out var ef)) { section.ExitFullscreen = new List<string>(ef); }
            if (raw.TryGetValue("toggle_diagnostic_overlay", out var diag)) { section.ToggleDiagnosticOverlay = new List<string>(diag); }
            if (raw.TryGetValue("show_preferences", out var sp)) { section.ShowPreferences = new List<string>(sp); }
            return section;
        }
    }

    // Tomlyn 2.x reuses System.Text.Json.JsonNamingPolicy for property naming. SnakeCaseLower maps PascalCase POCO members to snake_case TOML keys: HotkeysSection.PlayPause becomes [hotkeys].play_pause. SourceName feeds Tomlyn's diagnostics so a parse-error message names the file we were reading instead of leaving SourceName blank.
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
