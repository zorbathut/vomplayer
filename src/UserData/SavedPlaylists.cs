using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Vomplayer.UserData;

// SQLite-backed ISavedPlaylists. The connection is owned by StateDatabase; this class just runs queries against it. Schema lives in StateDatabase.ApplyV4Schema.
public sealed class SavedPlaylists : ISavedPlaylists
{
    // System.Text.Json wire shape. Property names match the JSON column documented in StateDatabase.ApplyV4Schema. Camel-case JSON for readability when inspecting the column directly with sqlite3.
    private sealed class StreamDto
    {
        public int Slot { get; set; }
        public int CurrentIndex { get; set; }
        public List<string> Items { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly SqliteConnection connection;

    public SavedPlaylists(SqliteConnection connection)
    {
        if (connection == null)
        {
            throw new ArgumentNullException(nameof(connection));
        }
        this.connection = connection;
    }

    public void Save(Guid guid, string title, IReadOnlyList<SavedPlaylistStream> streams)
    {
        if (title == null)
        {
            throw new ArgumentNullException(nameof(title));
        }
        if (streams == null)
        {
            throw new ArgumentNullException(nameof(streams));
        }
        // Filter out streams whose Items list is empty. An empty Secondary slot in PiP would otherwise produce a JSON entry with `items: []`; we'd then have to special-case it everywhere it's read. Cleaner to drop here and let downstream code assume every saved stream has at least one item.
        var nonEmpty = new List<SavedPlaylistStream>(streams.Count);
        foreach (var s in streams)
        {
            if (s.Items.Count > 0)
            {
                nonEmpty.Add(s);
            }
        }
        if (nonEmpty.Count == 0)
        {
            // No-op: don't create a row for a playlist that holds nothing. Matches the "playlists are autosaved when they have content" invariant the autosave service relies on for restoration; an empty row would surface in the Recent menu with no items to play.
            return;
        }
        var payload = new List<StreamDto>(nonEmpty.Count);
        foreach (var s in nonEmpty)
        {
            payload.Add(new StreamDto
            {
                Slot = s.SlotIndex,
                CurrentIndex = s.CurrentIndex,
                Items = new List<string>(s.Items),
            });
        }
        string json = JsonSerializer.Serialize(payload, JsonOptions);
        using var cmd = connection.CreateCommand();
        // INSERT OR REPLACE rewrites the entire row in one statement — atomic at the SQLite level, no transaction needed for the single-table case. last_used_at uses .NET ticks (100ns), same encoding as recent_files.last_opened, so back-to-back saves of distinct GUIDs produce strictly-increasing values for the GetMostRecent ORDER BY.
        cmd.CommandText = """
            INSERT OR REPLACE INTO saved_playlists (guid, title, last_used_at, stream_count, payload_json)
            VALUES ($g, $t, $u, $sc, $p);
            """;
        cmd.Parameters.AddWithValue("$g", guid.ToString("N"));
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.UtcTicks);
        cmd.Parameters.AddWithValue("$sc", nonEmpty.Count);
        cmd.Parameters.AddWithValue("$p", json);
        cmd.ExecuteNonQuery();
    }

    public void Touch(Guid guid)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE saved_playlists SET last_used_at = $u WHERE guid = $g;";
        cmd.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.UtcTicks);
        cmd.Parameters.AddWithValue("$g", guid.ToString("N"));
        // Silent no-op for unknown guid mirrors RecentFiles.RecordPosition: the contract is that Touch is called on guids the caller just received from GetById/GetMostRecent, so a missed row indicates concurrent deletion (not a feature in v1) rather than a logic bug worth throwing on.
        cmd.ExecuteNonQuery();
    }

    public SavedPlaylistEntry? GetById(Guid guid)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT guid, title, last_used_at, stream_count, payload_json FROM saved_playlists WHERE guid = $g;";
        cmd.Parameters.AddWithValue("$g", guid.ToString("N"));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        return ReadEntry(reader);
    }

    public SavedPlaylistEntry? GetMostRecent()
    {
        var list = GetMostRecent(1);
        return list.Count == 0 ? null : list[0];
    }

    public IReadOnlyList<SavedPlaylistEntry> GetMostRecent(int limit)
    {
        if (limit < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "limit must be non-negative");
        }
        var result = new List<SavedPlaylistEntry>(limit);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT guid, title, last_used_at, stream_count, payload_json
            FROM saved_playlists
            ORDER BY last_used_at DESC, guid DESC
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$n", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadEntry(reader));
        }
        return result;
    }

    private static SavedPlaylistEntry ReadEntry(SqliteDataReader reader)
    {
        var guid = Guid.ParseExact(reader.GetString(0), "N");
        string title = reader.GetString(1);
        long ticks = reader.GetInt64(2);
        int streamCount = reader.GetInt32(3);
        string json = reader.GetString(4);
        var streams = DeserializeStreams(json);
        return new SavedPlaylistEntry(
            guid,
            title,
            new DateTimeOffset(ticks, TimeSpan.Zero),
            streamCount,
            streams);
    }

    private static IReadOnlyList<SavedPlaylistStream> DeserializeStreams(string json)
    {
        var dtos = JsonSerializer.Deserialize<List<StreamDto>>(json, JsonOptions);
        if (dtos == null)
        {
            // payload_json is non-null at the schema level; a deserialized null means the column held the literal string "null", which can only happen if a third party wrote the row directly. Fail loud rather than silently returning an empty list — CLAUDE.md bans silent error handling, and an empty list would silently make the entry useless on restore.
            throw new InvalidOperationException("saved_playlists.payload_json deserialized to null");
        }
        var result = new List<SavedPlaylistStream>(dtos.Count);
        foreach (var dto in dtos)
        {
            result.Add(new SavedPlaylistStream(dto.Slot, dto.CurrentIndex, dto.Items.AsReadOnly()));
        }
        return result;
    }
}
