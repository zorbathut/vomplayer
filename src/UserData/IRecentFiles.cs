using System;

namespace Vomplayer.UserData;

// Interface for the recent-files list. Behind the seam so the VM (which records on file open) can be unit-tested without touching SQLite. Production wiring constructs RecentFiles on the shared StateDatabase connection.
public interface IRecentFiles
{
    // Record that the user opened this path (or URI). Repeated calls for the same path update last-opened and bump open-count rather than inserting a duplicate.
    void Record(string pathOrUri);

    // Save the play position for a path or URI, establishing the row when the position is the first thing we've learned about it. Caller is responsible for filtering out unsavable cases (URIs, no duration yet, near end of file); this layer just writes whatever it's given.
    void RecordPosition(string pathOrUri, double positionSeconds);

    // Returns the saved play position for a file, or null if no position is saved (or the file isn't in recents). Local file paths only; URI sources don't accumulate positions and the VM filters them at the call site.
    double? GetPosition(string pathOrUri);
}
