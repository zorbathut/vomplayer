namespace Vomplayer.UserData;

// Track-kind discriminator, kept separate from Vomplayer.Playback.MediaTrack because it lives on the persistence side and is what the SQLite schema's `kind` column stores. The string form ("video"/"audio"/"subtitle") matches the schema's CHECK constraint and is round-tripped via MediaKindNames below — keeping the round-trip in one place avoids drift between persisted strings and enum names if the enum is ever renamed.
public enum MediaKind
{
    Video,
    Audio,
    Subtitle,
}

public static class MediaKindNames
{
    // Production callers always read kinds back via Get(directory, kind) on a known MediaKind, so we never need a string→enum reverse map. Kept as an extension point if a future "list all preferences" ever needs it; until then, KISS.
    public static string ToColumnValue(MediaKind kind)
    {
        switch (kind)
        {
            case MediaKind.Video:
                return "video";
            case MediaKind.Audio:
                return "audio";
            case MediaKind.Subtitle:
                return "subtitle";
            default:
                throw new System.ArgumentOutOfRangeException(nameof(kind), kind, "unknown MediaKind");
        }
    }
}
