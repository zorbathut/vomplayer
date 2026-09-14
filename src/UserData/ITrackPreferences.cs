using System.Collections.Generic;

namespace Vomplayer.UserData;

// Per-directory remembered track choices. Behind the seam so the VM (which records on user choice and queries on file load) can be unit-tested without touching SQLite. Production wiring constructs TrackPreferences on the shared StateDatabase connection.
//
// Directory keys are absolute filesystem paths produced by TryGetDirectoryKey — non-local paths (http://, smb://, etc.) return null and the VM should skip both record and lookup for those.
public interface ITrackPreferences
{
    // Save (or replace) the user's choice for a (directory, kind) pair. UPSERT semantics — repeated calls overwrite.
    void Record(string directory, MediaKind kind, TrackPreference preference);

    // Look up the saved choice for a (directory, kind) pair, or null if nothing was recorded.
    TrackPreference? Get(string directory, MediaKind kind);

    // Every directory with at least one saved choice, sorted. Read by the diagnostics log so a load can say whether its directory key matches anything saved under a possibly different spelling.
    IReadOnlyList<string> ListDirectories();
}
