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

    // Update the saved play position for an already-recorded file. Caller is responsible for filtering out unsavable cases (URIs, near-end-of-file, etc.); this layer just writes whatever it's given. Silent no-op if the file isn't in recents — the contract is that VM paths Record() before they ever RecordPosition(), so a missing row means a programming bug, not a runtime case to synthesize a row for.
    void RecordPosition(string pathOrUri, double positionSeconds);

    // Returns the saved play position for a file, or null if no position is saved (or the file isn't in recents). Local file paths only; URI sources don't accumulate positions and the VM filters them at the call site.
    double? GetPosition(string pathOrUri);
}

public sealed record RecentFileEntry(string PathOrUri, DateTimeOffset LastOpened, long OpenCount);
