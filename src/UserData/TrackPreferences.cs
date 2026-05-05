using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Vomplayer.UserData;

// SQLite-backed ITrackPreferences. The connection is owned by StateDatabase; this class just runs queries against it.
public sealed class TrackPreferences : ITrackPreferences
{
    private readonly SqliteConnection connection;

    public TrackPreferences(SqliteConnection connection)
    {
        if (connection == null)
        {
            throw new ArgumentNullException(nameof(connection));
        }
        this.connection = connection;
    }

    public void Record(string directory, MediaKind kind, TrackPreference preference)
    {
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("directory must be non-empty", nameof(directory));
        }
        if (preference == null)
        {
            throw new ArgumentNullException(nameof(preference));
        }
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO track_preferences (directory, kind, is_none, title, lang, external, external_filename, index_in_kind)
            VALUES ($d, $k, $n, $t, $l, $e, $ef, $i)
            ON CONFLICT(directory, kind) DO UPDATE SET
              is_none           = excluded.is_none,
              title             = excluded.title,
              lang              = excluded.lang,
              external          = excluded.external,
              external_filename = excluded.external_filename,
              index_in_kind     = excluded.index_in_kind;
            """;
        cmd.Parameters.AddWithValue("$d", directory);
        cmd.Parameters.AddWithValue("$k", MediaKindNames.ToColumnValue(kind));
        cmd.Parameters.AddWithValue("$n", preference.IsNone ? 1 : 0);
        cmd.Parameters.AddWithValue("$t", (object?)preference.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$l", (object?)preference.Lang ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$e", preference.External ? 1 : 0);
        cmd.Parameters.AddWithValue("$ef", (object?)preference.ExternalFilename ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$i", (object?)preference.IndexInKind ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public TrackPreference? Get(string directory, MediaKind kind)
    {
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("directory must be non-empty", nameof(directory));
        }
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT is_none, title, lang, external, external_filename, index_in_kind
            FROM track_preferences
            WHERE directory = $d AND kind = $k;
            """;
        cmd.Parameters.AddWithValue("$d", directory);
        cmd.Parameters.AddWithValue("$k", MediaKindNames.ToColumnValue(kind));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        return new TrackPreference(
            IsNone: reader.GetInt64(0) != 0,
            Title: reader.IsDBNull(1) ? null : reader.GetString(1),
            Lang: reader.IsDBNull(2) ? null : reader.GetString(2),
            External: reader.GetInt64(3) != 0,
            ExternalFilename: reader.IsDBNull(4) ? null : reader.GetString(4),
            IndexInKind: reader.IsDBNull(5) ? (int?)null : (int)reader.GetInt64(5));
    }

    // Resolves the directory key used as the row's identity from a path-or-URI. Returns null for any input that doesn't have a meaningful local-filesystem directory: empty strings, URI schemes other than file (http/https/smb/...), paths with no parent (a bare filename like "foo.mp4" with no leading directory), and paths that fail GetFullPath/GetDirectoryName for any reason. Returning null means "don't save and don't look up" — non-local sources just don't participate in the per-directory preference system.
    public static string? TryGetDirectoryKey(string? pathOrUri)
    {
        if (!IsLocalFilesystemPath(pathOrUri))
        {
            return null;
        }
        try
        {
            var full = Path.GetFullPath(pathOrUri!);
            var dir = Path.GetDirectoryName(full);
            // Path.GetDirectoryName returns "" for paths with no directory component (after GetFullPath, this is unusual but defensive — and an empty string would crash Record's IsNullOrEmpty check rather than be skipped silently). Treat as non-savable.
            return string.IsNullOrEmpty(dir) ? null : dir;
        }
        catch (Exception ex)
        {
            // Path.GetFullPath throws on invalid characters in some platforms; treat as non-savable rather than crashing the menu action that triggered the call. Logged per the never-swallow policy so a real bug producing a stream of these isn't invisible.
            Console.Error.WriteLine($"[vompl] track-prefs: GetFullPath('{pathOrUri}') failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // Cheap scheme detection — anything that looks like `scheme://...` with the scheme up to ~10 chars is treated as a URI. Catches http://, https://, smb://, ftp://, sftp://, dvd://, bd://, etc. without needing a full URI parser. Bare Windows drive paths (`C:\foo`) survive because the colon isn't followed by `//`. Shared with the per-file resume-position layer in ViewModelMain, which has the same "is this a persistable local path" question.
    public static bool IsLocalFilesystemPath(string? pathOrUri)
    {
        if (string.IsNullOrEmpty(pathOrUri))
        {
            return false;
        }
        int colonSlashIdx = pathOrUri.IndexOf("://", StringComparison.Ordinal);
        return colonSlashIdx <= 0 || colonSlashIdx > 10;
    }
}
