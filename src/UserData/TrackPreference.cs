namespace Vomplayer.UserData;

// What the user picked for a (directory, kind) tuple. Read back from track_preferences table, fed into TrackMatcher when a new file in that directory loads.
//
// IsNone=true represents the user explicitly choosing "off" (e.g., subtitles disabled). When IsNone is true, the other identity fields (Title/Lang/External/ExternalFilename/IndexInKind) carry no meaning — the matcher short-circuits to "apply null id".
//
// When IsNone=false, the identity fields describe the track that was selected at save time. Title/Lang/ExternalFilename are all nullable because the source track may not have provided them; the matcher tolerates nulls during scoring (a null saved field never contributes to the score, but doesn't disqualify either). IndexInKind is the 0-based position among same-kind tracks at save time, used as a last-resort tiebreaker.
public sealed record TrackPreference(
    bool IsNone,
    string? Title,
    string? Lang,
    bool External,
    string? ExternalFilename,
    int? IndexInKind);
