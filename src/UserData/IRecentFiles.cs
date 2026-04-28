using System;
using System.Collections.Generic;

namespace Vomplayer.UserData;

// Interface for the recent-files list. Behind the seam so the VM (which records on file open) can be unit-tested without touching SQLite. Production wiring uses RecentFiles.Open(...).
public interface IRecentFiles
{
    // Record that the user opened this path (or URI). Repeated calls for the same path update last-opened and bump open-count rather than inserting a duplicate.
    void Record(string pathOrUri);

    // Most-recently-opened first. `limit` caps the result count.
    IReadOnlyList<RecentFileEntry> GetMostRecent(int limit);
}

public sealed record RecentFileEntry(string PathOrUri, DateTimeOffset LastOpened, long OpenCount);
