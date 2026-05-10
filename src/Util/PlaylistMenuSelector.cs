using System.Collections.Generic;
using Vomplayer.UserData;

namespace Vomplayer.Util;

// Pure selector for the File → Recent submenu, applied over saved-playlist entries (formerly recent-files entries — playlists are the unit of recency since v4). Two-phase pick over a most-recent-first input list, then a chronological re-sort:
//   Phase 1 (directory coverage): walk in order; for each entry whose representative-file directory hasn't been picked yet, take it. Stops at directoryCoverageSlots. Entries whose representative file is a URI / non-local path (TryGetDirectoryKey returns null) are skipped here — "directory" is meaningless for them — but they're still eligible in Phase 2.
//   Phase 2 (fill): walk in order again; take any entry not already picked until the chosen set reaches totalSlots.
// The final list is sorted by LastUsedAt descending so the menu reads chronologically regardless of which phase placed a given entry. The selector dedupes by Guid, so the same playlist can't appear twice even if the input list happens to contain it twice (it shouldn't, but the contract is robust to it).
//
// Per the design discussion: most playlists are single-file, so the "directory" of a playlist is just the directory of slot 0's currently-selected item. For multi-file playlists this picks one representative file — heuristic, but good enough for a UX-grouping selector.
public static class PlaylistMenuSelector
{
    public static IReadOnlyList<SavedPlaylistEntry> Select(IReadOnlyList<SavedPlaylistEntry> entries, int totalSlots, int directoryCoverageSlots)
    {
        if (entries == null)
        {
            throw new System.ArgumentNullException(nameof(entries));
        }
        if (totalSlots < 0)
        {
            throw new System.ArgumentOutOfRangeException(nameof(totalSlots));
        }
        if (directoryCoverageSlots < 0 || directoryCoverageSlots > totalSlots)
        {
            throw new System.ArgumentOutOfRangeException(nameof(directoryCoverageSlots));
        }

        var chosenGuids = new HashSet<System.Guid>();
        var seenDirectories = new HashSet<string>();
        var chosen = new List<SavedPlaylistEntry>(totalSlots);

        // Phase 1: directory coverage. The input is most-recent-first, so the first entry we see for a given directory IS that directory's most recent playlist.
        foreach (var entry in entries)
        {
            if (chosen.Count >= directoryCoverageSlots)
            {
                break;
            }
            var dir = DirectoryKeyOf(entry);
            if (dir == null)
            {
                continue;
            }
            if (seenDirectories.Add(dir))
            {
                chosen.Add(entry);
                chosenGuids.Add(entry.Guid);
            }
        }

        // Phase 2: fill. Take any entry not already chosen until we hit totalSlots. URI-only / dir-less playlists land here naturally.
        foreach (var entry in entries)
        {
            if (chosen.Count >= totalSlots)
            {
                break;
            }
            if (chosenGuids.Add(entry.Guid))
            {
                chosen.Add(entry);
            }
        }

        chosen.Sort((a, b) => b.LastUsedAt.CompareTo(a.LastUsedAt));
        return chosen;
    }

    // Representative-file directory key for an entry. Picks slot 0's currently-selected item; falls back to slot 0's first item if CurrentIndex is somehow out of range. Returns null for URI-only entries (they can't participate in the directory-coverage phase but still appear in fill).
    internal static string? DirectoryKeyOf(SavedPlaylistEntry entry)
    {
        SavedPlaylistStream? slot0 = null;
        foreach (var s in entry.Streams)
        {
            if (s.SlotIndex == 0)
            {
                slot0 = s;
                break;
            }
        }
        if (slot0 == null || slot0.Items.Count == 0)
        {
            return null;
        }
        int idx = slot0.CurrentIndex >= 0 && slot0.CurrentIndex < slot0.Items.Count ? slot0.CurrentIndex : 0;
        return TrackPreferences.TryGetDirectoryKey(slot0.Items[idx]);
    }
}
