using System;
using System.Collections.Generic;
using Vomplayer.Playback;
using Vomplayer.UserData;

namespace Vomplayer.Tests;

[TestFixture]
public class TrackMatcherTests
{
    private static MediaTrack T(int id, string? title = null, string? lang = null, bool external = false, string? externalFilename = null)
    {
        return new MediaTrack(id, title, lang, external, externalFilename);
    }

    private static TrackPreference Pref(bool isNone = false, string? title = null, string? lang = null, bool external = false, string? externalFilename = null, int? index = null)
    {
        return new TrackPreference(isNone, title, lang, external, externalFilename, index);
    }

    [Test]
    public void NoneShortCircuitsToApplyNull()
    {
        var available = new[] { T(1, "Some Track", "eng") };
        bool ok = TrackMatcher.TryMatch(Pref(isNone: true), available, out var id);
        Assert.That(ok, Is.True);
        Assert.That(id, Is.Null);
    }

    [Test]
    public void NoneAppliesEvenWhenAvailableIsEmpty()
    {
        bool ok = TrackMatcher.TryMatch(Pref(isNone: true), Array.Empty<MediaTrack>(), out var id);
        Assert.That(ok, Is.True);
        Assert.That(id, Is.Null);
    }

    [Test]
    public void EmptyAvailableWithSpecificPrefDoesNotMatch()
    {
        bool ok = TrackMatcher.TryMatch(Pref(title: "Foo", lang: "eng", index: 0), Array.Empty<MediaTrack>(), out var id);
        Assert.That(ok, Is.False);
        Assert.That(id, Is.Null);
    }

    [Test]
    public void TitleExactMatchWins()
    {
        var available = new[]
        {
            T(1, "English", "eng"),
            T(2, "Director's Commentary", "eng"),
            T(3, "French", "fre"),
        };
        bool ok = TrackMatcher.TryMatch(Pref(title: "Director's Commentary", lang: "eng"), available, out var id);
        Assert.That(ok, Is.True);
        Assert.That(id, Is.EqualTo(2));
    }

    [Test]
    public void TitleMatchIsCaseInsensitive()
    {
        var available = new[] { T(7, "Forced Subs", "eng"), T(8, "Full Subs", "eng") };
        bool ok = TrackMatcher.TryMatch(Pref(title: "FORCED SUBS"), available, out var id);
        Assert.That(ok, Is.True);
        Assert.That(id, Is.EqualTo(7));
    }

    [Test]
    public void LangMatchUsedWhenTitleAbsent()
    {
        var available = new[]
        {
            T(1, "Track A", "eng"),
            T(2, "Track B", "fre"),
            T(3, "Track C", "spa"),
        };
        bool ok = TrackMatcher.TryMatch(Pref(lang: "fre"), available, out var id);
        Assert.That(ok, Is.True);
        Assert.That(id, Is.EqualTo(2));
    }

    [Test]
    public void LangMatchIsCaseInsensitive()
    {
        var available = new[] { T(1, null, "eng"), T(2, null, "fre") };
        bool ok = TrackMatcher.TryMatch(Pref(lang: "ENG"), available, out var id);
        Assert.That(ok, Is.True);
        Assert.That(id, Is.EqualTo(1));
    }

    [Test]
    public void TitleTierBeatsLangTier()
    {
        // Saved: title="Forced", lang="eng". Available: a track with the same title in French + a track with the same lang but different title. Title tier beats.
        var available = new[]
        {
            T(1, "Forced", "fre"),    // (1,0,0)
            T(2, "Full", "eng"),      // (0,0,1)
        };
        bool ok = TrackMatcher.TryMatch(Pref(title: "Forced", lang: "eng"), available, out var id);
        Assert.That(ok, Is.True);
        Assert.That(id, Is.EqualTo(1));
    }

    [Test]
    public void WithinTitleTierLangCascades()
    {
        // Two title-tier matches; lang cascades within the tier to break the tie.
        var available = new[]
        {
            T(1, "Forced", "fre"),    // (1,0,0)
            T(2, "Forced", "eng"),    // (1,0,1)
            T(3, "Full", "eng"),      // (0,0,1)
        };
        bool ok = TrackMatcher.TryMatch(Pref(title: "Forced", lang: "eng"), available, out var id);
        Assert.That(ok, Is.True);
        Assert.That(id, Is.EqualTo(2));
    }

    [Test]
    public void ExternalFilenameTierMatches()
    {
        // Saved: external "commentary.ac3" with no title/lang. Available: an unrelated external + the matching one. Filename hit wins.
        var available = new[]
        {
            T(1, null, null, external: true, externalFilename: "music.ac3"),
            T(2, null, null, external: true, externalFilename: "commentary.ac3"),
        };
        bool ok = TrackMatcher.TryMatch(Pref(external: true, externalFilename: "commentary.ac3"), available, out var id);
        Assert.That(ok, Is.True);
        Assert.That(id, Is.EqualTo(2));
    }

    [Test]
    public void ExternalFilenameMatchIsCaseInsensitive()
    {
        var available = new[] { T(1, null, null, external: true, externalFilename: "Commentary.AC3") };
        bool ok = TrackMatcher.TryMatch(Pref(external: true, externalFilename: "commentary.ac3"), available, out var id);
        Assert.That(ok, Is.True);
        Assert.That(id, Is.EqualTo(1));
    }

    [Test]
    public void ExternalFilenameOnlyCountsWhenBothSidesExternal()
    {
        // An embedded track with a non-null ExternalFilename shouldn't score (mpv doesn't emit one for embedded tracks anyway, but defensive).
        var available = new[]
        {
            T(1, "Embedded", "eng", external: false, externalFilename: null),
        };
        bool ok = TrackMatcher.TryMatch(Pref(external: true, externalFilename: "side.ac3"), available, out var id);
        Assert.That(ok, Is.False);
    }

    [Test]
    public void TitleTierBeatsExternalFilenamePlusLang()
    {
        // (1,0,0) > (0,1,1) under lexicographic priority — title tier wins regardless of how many lower-tier hits the other candidate has.
        var available = new[]
        {
            T(1, null, "eng", external: true, externalFilename: "commentary.ac3"),  // (0,1,1)
            T(2, "Commentary", "fre", external: false, externalFilename: null),    // (1,0,0)
        };
        bool ok = TrackMatcher.TryMatch(Pref(title: "Commentary", lang: "eng", external: true, externalFilename: "commentary.ac3"), available, out var id);
        Assert.That(ok, Is.True);
        Assert.That(id, Is.EqualTo(2));
    }

    [Test]
    public void IndexTierUsedWhenNoStrongerTierMatches()
    {
        var available = new[]
        {
            T(11, "Wholly different track", "jpn"),
            T(22, "Another", "spa"),
            T(33, "Yet another", "kor"),
        };
        // Saved title/lang don't match any available; index=1 → second slot → id=22.
        bool ok = TrackMatcher.TryMatch(Pref(title: "Original Title", lang: "eng", index: 1), available, out var id);
        Assert.That(ok, Is.True);
        Assert.That(id, Is.EqualTo(22));
    }

    [Test]
    public void IndexOutOfRangeDropsToNoMatch()
    {
        var available = new[] { T(11, "Wholly different track", "jpn") };
        bool ok = TrackMatcher.TryMatch(Pref(title: "Original", lang: "eng", index: 5), available, out var id);
        Assert.That(ok, Is.False);
        Assert.That(id, Is.Null);
    }

    [Test]
    public void IndexNegativeDropsToNoMatch()
    {
        var available = new[] { T(11, "Whatever", "jpn") };
        bool ok = TrackMatcher.TryMatch(Pref(title: "Original", lang: "eng", index: -1), available, out var id);
        Assert.That(ok, Is.False);
    }

    [Test]
    public void StrongerTierBeatsIndexMatch()
    {
        // Lang matches at id=1 (tier 3); index=2 points to id=3 (tier 4). Lang tier dominates regardless of index.
        var available = new[]
        {
            T(1, null, "eng"),  // (0,0,1,0)
            T(2, null, "fre"),  // (0,0,0,0)
            T(3, null, "spa"),  // (0,0,0,1)
        };
        bool ok = TrackMatcher.TryMatch(Pref(lang: "eng", index: 2), available, out var id);
        Assert.That(ok, Is.True);
        Assert.That(id, Is.EqualTo(1));
    }

    [Test]
    public void IndexMatchBreaksTieAheadOfLowestId()
    {
        // Three lang-tier matches, all (0,0,1,?). Saved index=2 promotes the candidate at position 2 above the lowest-id pick (id=3 at position 0). Without the index tier, lowest-id tiebreak would pick id=3; with it, the user's prior position wins.
        var available = new[]
        {
            T(3, null, "eng"),  // pos 0 → (0,0,1,0)
            T(7, null, "eng"),  // pos 1 → (0,0,1,0)
            T(5, null, "eng"),  // pos 2 → (0,0,1,1)  ← saved index
        };
        bool ok = TrackMatcher.TryMatch(Pref(lang: "eng", index: 2), available, out var id);
        Assert.That(ok, Is.True);
        Assert.That(id, Is.EqualTo(5));
    }

    [Test]
    public void LowestIdIsOnlyAFinalTiebreakerWithinFullTuple()
    {
        // All three candidates fully tied at (0,0,1,0) — same lang hit, no index match for any (saved index=null). Lowest mpv id wins as the very last resort.
        var available = new[]
        {
            T(7, null, "eng"),
            T(3, null, "eng"),
            T(5, null, "eng"),
        };
        bool ok = TrackMatcher.TryMatch(Pref(lang: "eng"), available, out var id);
        Assert.That(ok, Is.True);
        Assert.That(id, Is.EqualTo(3));
    }

    [Test]
    public void NullSavedFieldsContributeNothing()
    {
        // Pref with all identity fields null and no index → no signal at all → no match (not "first track wins").
        var available = new[] { T(1, "A", "eng"), T(2, "B", "fre") };
        bool ok = TrackMatcher.TryMatch(Pref(), available, out var id);
        Assert.That(ok, Is.False);
        Assert.That(id, Is.Null);
    }

    [Test]
    public void NullArgumentsThrow()
    {
        Assert.Throws<ArgumentNullException>(() => TrackMatcher.TryMatch(null!, new[] { T(1) }, out _));
        Assert.Throws<ArgumentNullException>(() => TrackMatcher.TryMatch(Pref(), null!, out _));
    }

    [Test]
    public void FromUserChoiceNullIdProducesIsNonePref()
    {
        var available = new[] { T(1, "A", "eng") };
        var pref = TrackMatcher.FromUserChoice(null, available);
        Assert.That(pref, Is.Not.Null);
        Assert.That(pref!.IsNone, Is.True);
        Assert.That(pref.Title, Is.Null);
        Assert.That(pref.IndexInKind, Is.Null);
    }

    [Test]
    public void FromUserChoiceFillsInTrackFields()
    {
        var available = new[]
        {
            T(1, "First", "eng"),
            T(7, "Commentary", "eng", external: true, externalFilename: "commentary.ac3"),
            T(9, "French", "fre"),
        };
        var pref = TrackMatcher.FromUserChoice(7, available);
        Assert.That(pref, Is.Not.Null);
        Assert.That(pref!.IsNone, Is.False);
        Assert.That(pref.Title, Is.EqualTo("Commentary"));
        Assert.That(pref.Lang, Is.EqualTo("eng"));
        Assert.That(pref.External, Is.True);
        Assert.That(pref.ExternalFilename, Is.EqualTo("commentary.ac3"));
        Assert.That(pref.IndexInKind, Is.EqualTo(1));
    }

    [Test]
    public void FromUserChoiceUnknownIdReturnsNull()
    {
        var available = new[] { T(1, "A", "eng") };
        var pref = TrackMatcher.FromUserChoice(99, available);
        Assert.That(pref, Is.Null);
    }
}
