using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Vomplayer.UserData;

// Owns the state.db SqliteConnection and its schema migration registry. Per-feature persistence classes (RecentFiles, TrackPreferences, …) take the Connection in their constructor — they don't open or migrate the DB themselves, which avoids the "two classes racing migrations on the same file" problem and removes the order-dependent "RecentFiles.Open before TrackPreferences.Open" load-bearing contract.
//
// Migrations live in the Migrations array below. Each entry takes the DB from (Version-1) state to Version state, atomically (each step runs in its own transaction with a matching PRAGMA user_version bump). To add a v(N+1):
//   1. Append `new Migration(N+1, "describe what changes", ApplyV(N+1)Schema)` to the array.
//   2. Implement `ApplyV(N+1)Schema(SqliteTransaction)` with the schema changes — INCLUDING any data backfill needed to migrate existing v(N) rows.
//   3. Add a test that pre-stages a v(N) DB via OpenConnectionAndMigrateTo(path, N), populates v(N)-shaped data, then verifies normal Open() ends up at v(N+1) with the data correctly transformed.
// Never edit a published migration's body — that would silently change schema for users whose DB already passed through it. Treat the array as append-only history.
public sealed class StateDatabase : IDisposable
{
    internal sealed record Migration(int Version, string Description, Action<SqliteTransaction> Apply);

    internal static readonly IReadOnlyList<Migration> Migrations = new Migration[]
    {
        new Migration(1, "initial schema: recent_files table", ApplyV1Schema),
        new Migration(2, "track_preferences table for per-directory video/audio/subtitle remembered choices", ApplyV2Schema),
        new Migration(3, "recent_files.position_seconds column for per-file resume", ApplyV3Schema),
        new Migration(4, "saved_playlists table for autosaved playlist history (single + multi-stream PiP)", ApplyV4Schema),
    };

    internal static int CurrentSchemaVersion
    {
        get { return Migrations[^1].Version; }
    }

    public SqliteConnection Connection { get; }

    private StateDatabase(SqliteConnection connection)
    {
        Connection = connection;
    }

    public static StateDatabase Open(string dbPath)
    {
        return new StateDatabase(OpenConnectionAndMigrateTo(dbPath, CurrentSchemaVersion));
    }

    // Test-only escape hatch. Opens the DB the same way Open() does (WAL, busy_timeout) but stops the migration chain at `targetVersion` instead of running to current. The returned connection is at v=targetVersion exactly, so callers can populate vN-shaped data and then close + reopen via Open() to exercise the vN→current path. targetVersion=0 returns an open connection with no migrations applied, useful for verifying the full migration cycle from a blank DB.
    internal static SqliteConnection OpenConnectionAndMigrateTo(string dbPath, int targetVersion)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var connection = new SqliteConnection($"Data Source={dbPath}");
        try
        {
            connection.Open();
            // WAL mode survives connection close; we still set it on every open because a fresh DB starts in DELETE mode and the cost of issuing a no-op pragma on an already-WAL DB is trivial. PRAGMA journal_mode returns the resulting mode as a row — verify it actually stuck (not just "we asked nicely"). WAL fails silently on filesystems that don't support the required mmap/locking semantics (some FUSE mounts, certain network filesystems); silently falling back to DELETE would defeat the multi-process safety the user explicitly asked for.
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode=WAL;";
                var actual = cmd.ExecuteScalar() as string;
                if (!string.Equals(actual, "wal", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Failed to enable WAL on {dbPath}; PRAGMA journal_mode returned '{actual ?? "<null>"}'. The filesystem may not support WAL.");
                }
            }
            // Pin busy_timeout explicitly so behavior doesn't drift if Microsoft.Data.Sqlite changes its default. 5s is enough to outlast any plausible single-statement contention from a sibling vomplayer process; any longer and a hung peer would freeze the GTK main thread visibly.
            Execute(connection, "PRAGMA busy_timeout=5000;");
            MigrateTo(connection, targetVersion);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
        return connection;
    }

    // Walk the Migrations array forward from the connection's current user_version up to (and including) targetVersion. Each step runs in its own transaction together with the user_version bump, so a crash between steps leaves the DB at the last completed version (next launch retries from there). Asking to migrate to a version below the current user_version is a downgrade — refused, since downgrades have no defined semantics and a bare attempt could corrupt the DB.
    internal static void MigrateTo(SqliteConnection connection, int targetVersion)
    {
        int version = ReadUserVersion(connection);
        if (version > targetVersion)
        {
            throw new InvalidOperationException(
                $"state.db is at schema version {version}, asked to migrate to {targetVersion}; refusing to downgrade (would corrupt the database).");
        }
        foreach (var m in Migrations)
        {
            if (m.Version <= version)
            {
                continue;
            }
            if (m.Version > targetVersion)
            {
                break;
            }
            try
            {
                using var tx = connection.BeginTransaction();
                m.Apply(tx);
                SetUserVersion(tx, m.Version);
                tx.Commit();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Schema migration to v{m.Version} ({m.Description}) failed: {ex.Message}", ex);
            }
            version = m.Version;
        }
    }

    private static void ApplyV1Schema(SqliteTransaction tx)
    {
        using var cmd = tx.Connection!.CreateCommand();
        cmd.Transaction = tx;
        // path_or_uri is UNIQUE so the upsert in RecentFiles.Record() can use ON CONFLICT to update in place. Index on last_opened keeps GetMostRecent's ORDER BY/LIMIT cheap. INTEGER PRIMARY KEY (rowid alias) rather than AUTOINCREMENT — the id is internal-only and rowid reuse on delete is fine for this use case.
        cmd.CommandText = """
            CREATE TABLE recent_files (
              id           INTEGER PRIMARY KEY,
              path_or_uri  TEXT NOT NULL UNIQUE,
              last_opened  INTEGER NOT NULL,
              open_count   INTEGER NOT NULL DEFAULT 1
            );
            CREATE INDEX recent_files_last_opened ON recent_files (last_opened DESC);
            """;
        cmd.ExecuteNonQuery();
    }

    // Per-file resume position. NULL means "no saved position" (the default for every existing v2 row and any new file before it accumulates a save). REAL because mpv's time-pos is sub-second; saving to integer seconds would round-trip lose precision the user might notice on a frame-accurate seek. No companion saved_at column — `last_opened` already records when we touched the row, and YAGNI on a stale-position-sweep until one is actually wanted.
    private static void ApplyV3Schema(SqliteTransaction tx)
    {
        using var cmd = tx.Connection!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "ALTER TABLE recent_files ADD COLUMN position_seconds REAL;";
        cmd.ExecuteNonQuery();
    }

    // Saved-playlists store. JSON-blob shape over normalized child tables: v1 has no requirement to query inside playlist contents (no "all playlists containing X" / no playlist filtering), so a single-table upsert is dramatically simpler than three-table FK cascades. payload_json is a JSON array of `{slot, current_index, items}` objects — schemaless room to grow to N streams without further migrations. stream_count is denormalized so the startup "if most-recent is multi-stream → don't autoload" check is a single indexed lookup instead of a JSON parse.
    private static void ApplyV4Schema(SqliteTransaction tx)
    {
        using var cmd = tx.Connection!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE saved_playlists (
              guid          TEXT PRIMARY KEY,
              title         TEXT NOT NULL,
              last_used_at  INTEGER NOT NULL,
              stream_count  INTEGER NOT NULL,
              payload_json  TEXT NOT NULL
            );
            CREATE INDEX saved_playlists_last_used ON saved_playlists (last_used_at DESC);
            """;
        cmd.ExecuteNonQuery();
    }

    // Per-directory remembered track choices, used by TrackPreferences. Composite primary key (directory, kind) means Record can use ON CONFLICT for in-place updates — there's only ever one preference per (directory, kind). is_none = 1 represents the user explicitly choosing "off" (e.g., subtitles disabled); the matching logic short-circuits to apply null in that case. Title/lang/external_filename are nullable because the source track may not have provided them — TrackMatcher tolerates nulls during scoring. index_in_kind is the 0-based position among same-kind tracks at save time and is the matcher's last-resort tiebreaker.
    private static void ApplyV2Schema(SqliteTransaction tx)
    {
        using var cmd = tx.Connection!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE track_preferences (
              directory          TEXT NOT NULL,
              kind               TEXT NOT NULL CHECK (kind IN ('video', 'audio', 'subtitle')),
              is_none            INTEGER NOT NULL CHECK (is_none IN (0, 1)),
              title              TEXT,
              lang               TEXT,
              external           INTEGER NOT NULL CHECK (external IN (0, 1)),
              external_filename  TEXT,
              index_in_kind      INTEGER,
              PRIMARY KEY (directory, kind)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static int ReadUserVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void SetUserVersion(SqliteTransaction tx, int version)
    {
        using var cmd = tx.Connection!.CreateCommand();
        cmd.Transaction = tx;
        // PRAGMA user_version doesn't accept bound parameters, so format inline. The value is bounded by Migrations[].Version (compile-time), not user input, so there's no injection vector.
        cmd.CommandText = $"PRAGMA user_version = {version};";
        cmd.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        Connection.Dispose();
    }
}
