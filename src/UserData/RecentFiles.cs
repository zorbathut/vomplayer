using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Vomplayer.UserData;

// SQLite-backed IRecentFiles. The connection is owned by StateDatabase; this class just runs queries against it. WAL mode is enabled at StateDatabase.Open time, so multiple vomplayer processes can read/write concurrently without long blocking.
public sealed class RecentFiles : IRecentFiles
{
    private readonly SqliteConnection connection;

    public RecentFiles(SqliteConnection connection)
    {
        if (connection == null)
        {
            throw new ArgumentNullException(nameof(connection));
        }
        this.connection = connection;
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
}
