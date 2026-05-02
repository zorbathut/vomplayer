using System.Collections.Generic;
using Vomplayer.UserData;

namespace Vomplayer.Util;

// Pure selector for the File → Recent Files submenu. Two-phase pick over a most-recent-first input list, then a chronological re-sort:
//   Phase 1 (directory coverage): walk in order; for each entry whose directory hasn't been picked yet, take it. Stops at directoryCoverageSlots. URI / non-local entries (TryGetDirectoryKey returns null) are skipped here — "directory" is meaningless for them — but they're still eligible in Phase 2.
//   Phase 2 (fill): walk in order again; take any entry not already picked until the chosen set reaches totalSlots.
// The final list is sorted by LastOpened descending so the menu reads chronologically regardless of which phase placed a given entry. The selector dedupes by PathOrUri (Phase 2's "not already picked" check), so the same file can't appear twice even if the input list happens to contain it twice (it shouldn't, but the contract is robust to it).
public static class RecentFilesMenuSelector
{
    public static IReadOnlyList<RecentFileEntry> Select(IReadOnlyList<RecentFileEntry> entries, int totalSlots, int directoryCoverageSlots)
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

        var chosenPaths = new HashSet<string>();
        var seenDirectories = new HashSet<string>();
        var chosen = new List<RecentFileEntry>(totalSlots);

        // Phase 1: directory coverage. The input is most-recent-first, so the first entry we see for a given directory IS that directory's most recent file.
        foreach (var entry in entries)
        {
            if (chosen.Count >= directoryCoverageSlots)
            {
                break;
            }
            var dir = TrackPreferences.TryGetDirectoryKey(entry.PathOrUri);
            if (dir == null)
            {
                continue;
            }
            if (seenDirectories.Add(dir))
            {
                chosen.Add(entry);
                chosenPaths.Add(entry.PathOrUri);
            }
        }

        // Phase 2: fill. Take any entry not already chosen until we hit totalSlots. URIs land here naturally.
        foreach (var entry in entries)
        {
            if (chosen.Count >= totalSlots)
            {
                break;
            }
            if (chosenPaths.Add(entry.PathOrUri))
            {
                chosen.Add(entry);
            }
        }

        chosen.Sort((a, b) => b.LastOpened.CompareTo(a.LastOpened));
        return chosen;
    }
}
