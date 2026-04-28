using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Vomplayer.UserData;

// SQLite-backed IRecentFiles. WAL mode is enabled so multiple vomplayer processes can read/write concurrently without long blocking; a writer briefly holds an exclusive lock during the upsert but readers proceed against the last committed snapshot.
//
// Schema migrations live in the Migrations array below. Each entry takes the DB from (Version-1) state to Version state, atomically (each step runs in its own transaction with a matching PRAGMA user_version bump). To add a v2:
//   1. Append `new Migration(2, "describe what changes", ApplyV2Schema)` to the array.
//   2. Implement `ApplyV2Schema(SqliteTransaction)` with the schema changes — INCLUDING any data backfill needed to migrate existing v1 rows.
//   3. Add a test that pre-stages a v1 DB via OpenConnectionAndMigrateTo(path, 1), populates v1-shaped data, then verifies normal Open() ends up at v2 with the data correctly transformed.
// Never edit a published migration's body — that would silently change schema for users whose DB already passed through it. Treat the array as append-only history.
public sealed class RecentFiles : IRecentFiles, IDisposable
{
    internal sealed record Migration(int Version, string Description, Action<SqliteTransaction> Apply);

    internal static readonly IReadOnlyList<Migration> Migrations = new Migration[]
    {
        new Migration(1, "initial schema: recent_files table", ApplyV1Schema),
    };

    internal static int CurrentSchemaVersion
    {
        get { return Migrations[^1].Version; }
    }

    private readonly SqliteConnection connection;

    private RecentFiles(SqliteConnection connection)
    {
        this.connection = connection;
    }

    public static RecentFiles Open(string dbPath)
    {
        return new RecentFiles(OpenConnectionAndMigrateTo(dbPath, CurrentSchemaVersion));
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
        // path_or_uri is UNIQUE so the upsert in Record() can use ON CONFLICT to update in place. Index on last_opened keeps GetMostRecent's ORDER BY/LIMIT cheap. INTEGER PRIMARY KEY (rowid alias) rather than AUTOINCREMENT — the id is internal-only and rowid reuse on delete is fine for this use case.
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

    public void Record(string pathOrUri)
    {
        if (string.IsNullOrEmpty(pathOrUri))
        {
            throw new ArgumentException("pathOrUri must be non-empty", nameof(pathOrUri));
        }
        using var cmd = connection.CreateCommand();
        // Upsert: insert if new, otherwise bump last_opened and increment open_count. last_opened is .NET ticks (100ns resolution since 0001-01-01) rather than Unix-ms — this gives enough granularity that back-to-back Record() calls in a tight loop produce strictly increasing values, so re-recording the same file in a single user gesture lands above its prior version under ORDER BY last_opened.
        cmd.CommandText = """
            INSERT INTO recent_files (path_or_uri, last_opened, open_count)
            VALUES ($p, $t, 1)
            ON CONFLICT(path_or_uri) DO UPDATE SET
              last_opened = excluded.last_opened,
              open_count  = open_count + 1;
            """;
        cmd.Parameters.AddWithValue("$p", pathOrUri);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.UtcTicks);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<RecentFileEntry> GetMostRecent(int limit)
    {
        if (limit < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "limit must be non-negative");
        }
        var result = new List<RecentFileEntry>(limit);
        using var cmd = connection.CreateCommand();
        // Tiebreaker `id DESC`: even at 100ns timestamp resolution, two Record() calls of *different* paths can in theory land on the same tick. Newer rowid wins, matching "the later upsert came last." Re-recording the same path doesn't tie because the upsert assigns a strictly newer last_opened.
        cmd.CommandText = """
            SELECT path_or_uri, last_opened, open_count
            FROM recent_files
            ORDER BY last_opened DESC, id DESC
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$n", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string path = reader.GetString(0);
            long lastOpenedTicks = reader.GetInt64(1);
            long openCount = reader.GetInt64(2);
            result.Add(new RecentFileEntry(
                path,
                new DateTimeOffset(lastOpenedTicks, TimeSpan.Zero),
                openCount));
        }
        return result;
    }

    public void Dispose()
    {
        connection.Dispose();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
