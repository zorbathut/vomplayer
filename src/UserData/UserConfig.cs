using System;
using System.IO;
using System.Text.Json;
using Tomlyn;

namespace Vomplayer.UserData;

// User-editable configuration backed by a TOML file. Loaded once at startup; defaults apply when the file is missing or when a key isn't set. No file is written automatically — the file appears the first time the user creates one by hand. Save() will be added when a real settings dialog needs it; until then, "config doesn't exist" is the steady state for fresh installs.
//
// Malformed TOML doesn't crash the app: the broken file is rotated to <path>.malformed-<timestamp>.bak, a stderr line names both the parse error and the rotation target, and load proceeds with defaults. The user keeps their text (in the .bak) and the player keeps starting. A rotation that itself fails (read-only filesystem, etc.) logs both errors and continues with defaults but leaves the broken file in place — so next launch will warn again until the user resolves it.
//
// IMPORTANT: the only exposed setting today is [placeholder].example, which is deliberately a throwaway. Real settings will be added under their own sections as user-facing knobs land. Don't add knobs to [placeholder] — promote them out into a properly-named section (and a real default value) when they exist.
public sealed class UserConfig
{
    public PlaceholderSection Placeholder { get; set; } = new();

    public sealed class PlaceholderSection
    {
        public string Example { get; set; } = "hello";
    }

    // Tomlyn 2.x reuses System.Text.Json.JsonNamingPolicy for property naming. SnakeCaseLower maps PascalCase POCO members to snake_case TOML keys: PlaceholderSection.Example becomes [placeholder].example. SourceName feeds Tomlyn's diagnostics so a parse-error message names the file we were reading instead of leaving SourceName blank.
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
