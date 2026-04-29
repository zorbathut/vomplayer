using System;
using System.Collections.Generic;
using Vomplayer.Playback;

namespace Vomplayer.UserData;

// Pure resolver from "what the user picked last time" to "which available track in the new file most resembles it". Static + no I/O so it's easy to table-test.
//
// Algorithm: strict priority cascade over four identity criteria, in order:
//   1. Title (e.g., "Director's Commentary", "Forced Subs") — strongest because authored, intentional, and stable across releases that share a mastering pipeline.
//   2. External filename (e.g., "commentary.ac3") — strong when present; if a sidecar by the same name lives next to a sibling file, that's almost certainly the same intended track. Only meaningful when both saved and candidate are external.
//   3. Language (e.g., "eng") — common case; user prefers a language and that selection generalizes.
//   4. Index within kind — the saved track's 0-based position among same-kind tracks at save time. Weak signal cross-file (a 5-track file's index 2 is not "the same track" as a 3-track file's index 2), but at least it's a *user-chosen* index, so it's preferred over the absolute-last-resort lowest-mpv-id tiebreaker that follows.
//
// Each candidate gets a (titleHit, extFilenameHit, langHit, indexMatch) rank tuple of 0/1 values. Highest tuple under lexicographic comparison wins; ties broken by lowest mpv id (very last resort — id is just stream order from the muxer, carrying no user intent). When even the best tuple is all zeros, there's no information at all — return no match rather than pick a semantically unrelated track.
public static class TrackMatcher
{
    // Returns true if a match was found and writes the resulting track id to `trackId` (null = apply "None"). Returns false if no rank tier matched — the caller should not change the current selection.
    public static bool TryMatch(TrackPreference saved, IReadOnlyList<MediaTrack> available, out int? trackId)
    {
        if (saved == null)
        {
            throw new ArgumentNullException(nameof(saved));
        }
        if (available == null)
        {
            throw new ArgumentNullException(nameof(available));
        }

        if (saved.IsNone)
        {
            // Explicit "off" preference always applies, regardless of what the new file has.
            trackId = null;
            return true;
        }

        MediaTrack? best = null;
        (int Title, int ExtFilename, int Lang, int IndexMatch) bestRank = (0, 0, 0, 0);
        for (int i = 0; i < available.Count; i++)
        {
            var track = available[i];
            (int Title, int ExtFilename, int Lang, int IndexMatch) rank = (
                TitleHits(saved, track) ? 1 : 0,
                ExternalFilenameHits(saved, track) ? 1 : 0,
                LangHits(saved, track) ? 1 : 0,
                saved.IndexInKind == i ? 1 : 0);
            int cmp = rank.CompareTo(bestRank);
            // Strictly better rank → take it. Equal rank with at least one positive component → tie-break by lowest mpv id. Equal-and-all-zero is skipped via the bestRank-positive check below.
            if (cmp > 0 || (cmp == 0 && best != null && track.Id < best.Id))
            {
                best = track;
                bestRank = rank;
            }
        }
        if (best != null && bestRank != (0, 0, 0, 0))
        {
            trackId = best.Id;
            return true;
        }

        trackId = null;
        return false;
    }

    private static bool TitleHits(TrackPreference saved, MediaTrack track)
    {
        return !string.IsNullOrEmpty(saved.Title) && !string.IsNullOrEmpty(track.Title)
            && string.Equals(saved.Title, track.Title, StringComparison.OrdinalIgnoreCase);
    }

    // ExternalFilename equality (e.g., the user picked "commentary.ac3" for movie1.mkv; movie2.mkv in the same dir has the same sidecar auto-loaded). Only counts when both sides are external; comparing a saved external filename against an embedded track's null filename would be misleading. Filename comparison is case-insensitive — case-insensitive filesystems (macOS default, Windows) make case differences uninformative; case-sensitive filesystems rarely produce the kind of file naming convention where case alone distinguishes intended tracks.
    private static bool ExternalFilenameHits(TrackPreference saved, MediaTrack track)
    {
        return saved.External && track.External
            && !string.IsNullOrEmpty(saved.ExternalFilename) && !string.IsNullOrEmpty(track.ExternalFilename)
            && string.Equals(saved.ExternalFilename, track.ExternalFilename, StringComparison.OrdinalIgnoreCase);
    }

    private static bool LangHits(TrackPreference saved, MediaTrack track)
    {
        return !string.IsNullOrEmpty(saved.Lang) && !string.IsNullOrEmpty(track.Lang)
            && string.Equals(saved.Lang, track.Lang, StringComparison.OrdinalIgnoreCase);
    }

    // Build a TrackPreference from the user's explicit menu choice. trackId == null is the "None" path; otherwise we pull title/lang/external/index from the same-kind list at the moment of selection. trackId not present in availableSameKind is treated as "track went away just now" and returns null — caller should skip the save.
    public static TrackPreference? FromUserChoice(int? trackId, IReadOnlyList<MediaTrack> availableSameKind)
    {
        if (availableSameKind == null)
        {
            throw new ArgumentNullException(nameof(availableSameKind));
        }
        if (trackId == null)
        {
            return new TrackPreference(IsNone: true, Title: null, Lang: null, External: false, ExternalFilename: null, IndexInKind: null);
        }
        for (int i = 0; i < availableSameKind.Count; i++)
        {
            var track = availableSameKind[i];
            if (track.Id == trackId.Value)
            {
                return new TrackPreference(
                    IsNone: false,
                    Title: track.Title,
                    Lang: track.Lang,
                    External: track.External,
                    ExternalFilename: track.ExternalFilename,
                    IndexInKind: i);
            }
        }
        return null;
    }
}
