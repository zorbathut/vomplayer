using System;
using System.Collections.Generic;

namespace Vomplayer.UserData;

// Interface for the saved-playlists store. Behind the seam so the VM (PlaylistAutosave) can be unit-tested without touching SQLite. Production wiring uses SavedPlaylists(stateDb.Connection).
//
// A "saved playlist" is one logical media session — a single-stream queue OR a PiP dual-stream session, identified by an opaque GUID never shown to the user. The Recent menu surfaces these by Title; the Title is computed and re-stamped on every relevant change (no parsing at menu-open time).
public interface ISavedPlaylists
{
    // Upsert (INSERT OR REPLACE). The whole row is rewritten and last_used_at bumped to "now". stream_count is derived from streams.Count after empty-stream filtering — callers pass everything they have, the implementation drops streams whose Items.Count == 0 so an empty Secondary slot in PiP doesn't pollute the saved record. If after filtering all streams are empty, Save is a silent no-op (don't create a row for a playlist with no items).
    void Save(Guid guid, string title, IReadOnlyList<SavedPlaylistStream> streams);

    // Bumps last_used_at without rewriting payload_json. Used when re-loading an existing playlist via the Recent menu — the contents haven't changed, just the recency. Silent no-op if guid isn't in the store (matches RecentFiles.RecordPosition semantics).
    void Touch(Guid guid);

    // Returns the entry for guid, or null if missing.
    SavedPlaylistEntry? GetById(Guid guid);

    // Returns the absolute most-recent entry, or null if the store is empty. Used by app startup to decide whether to autoload.
    SavedPlaylistEntry? GetMostRecent();

    // Most-recent-first, capped at limit.
    IReadOnlyList<SavedPlaylistEntry> GetMostRecent(int limit);
}

// One stream within a saved playlist. SlotIndex is 0 for primary, 1 for secondary; the storage format supports arbitrary N for forward-compat. CurrentIndex is the index within Items that was current when the playlist was last saved.
public sealed record SavedPlaylistStream(int SlotIndex, int CurrentIndex, IReadOnlyList<string> Items);

// One saved playlist. StreamCount is the number of non-empty streams in this entry (denormalized so the startup query can filter without parsing payload_json).
public sealed record SavedPlaylistEntry(Guid Guid, string Title, DateTimeOffset LastUsedAt, int StreamCount, IReadOnlyList<SavedPlaylistStream> Streams);
