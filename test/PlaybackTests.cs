using System;
using Vomplayer.Playback;

namespace Vomplayer.Tests;

[TestFixture]
public class PlaybackTests
{
    private static Playback.Playback NewHeadlessInitialized()
    {
        // Synchronous post — dispatcher forwards events via this hook, so event handlers run on the test thread.
        var pb = new Playback.Playback(a => a());
        pb.Initialize();
        return pb;
    }

    [Test]
    public void NullPostThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new Playback.Playback(null!));
    }

    [Test]
    public void DisposeWithoutInitializeIsSafe()
    {
        var pb = new Playback.Playback(a => a());
        Assert.DoesNotThrow(() => pb.Dispose());
    }

    [Test]
    public void IsPausedDefaultsTrue()
    {
        using var pb = NewHeadlessInitialized();
        Assert.That(pb.IsPaused, Is.True);
    }

    [Test]
    public void IsCoreIdleDefaultsTrue()
    {
        using var pb = NewHeadlessInitialized();
        Assert.That(pb.IsCoreIdle, Is.True);
    }

    // Regression: libmpv aborts the host process with `free(): invalid pointer` if a `seek` command runs before any file has been loaded. User-visible symptom was the seek slider crashing the player when clicked with no media open. Sleeps bracket the Seek call to give the dispatcher's worker thread time to drain the posted action — without them the test could reach Dispose before the seek even reaches mpv, masking the abort. Reaching the end of this test without the test host being killed is the assertion; reverting the gate in Playback.Seek causes this test to abort the entire test run.
    [Test]
    public void SeekBeforeLoadFileIsSafe()
    {
        using var pb = NewHeadlessInitialized();
        System.Threading.Thread.Sleep(300);
        pb.Seek(0.0);
        System.Threading.Thread.Sleep(300);
    }

    [Test]
    public void LoadFileNullPathThrows()
    {
        using var pb = NewHeadlessInitialized();
        Assert.Throws<ArgumentNullException>(() => pb.LoadFile(null!));
    }

    [TestCase(null, false)]
    [TestCase("", false)]
    [TestCase("bt.1886", false)]
    [TestCase("srgb", false)]
    [TestCase("gamma2.2", false)]
    [TestCase("st428", false)]
    [TestCase("v-log", false)]
    [TestCase("s-log1", false)]
    [TestCase("pq", true)]
    [TestCase("hlg", true)]
    public void IsHdrGammaClassifiesTransferFunctions(string? gamma, bool expected)
    {
        Assert.That(Playback.Playback.IsHdrGamma(gamma), Is.EqualTo(expected));
    }

    // HDR-transition tests drive UpdateSourceHdr directly on an uninitialized Playback. Skipping Initialize() avoids mpv's event-pump thread, which would concurrently dispatch observed properties onto the same object. Exercising UpdateSourceHdr (internal) rather than the private OnMpvPropertyChanged dispatcher keeps the test seam narrow — dispatch routing itself is a one-line case in OnMpvPropertyChanged.
    [Test]
    public void SourceHdrChangedFiresOnSdrToHdrTransition()
    {
        using var pb = new Playback.Playback(a => a());
        var fires = new System.Collections.Generic.List<bool>();
        pb.SourceHdrChanged += v => fires.Add(v);

        pb.UpdateSourceHdr("pq");

        Assert.That(fires, Is.EqualTo(new[] { true }));
    }

    [Test]
    public void SourceHdrChangedDoesNotFireOnSdrRepeats()
    {
        using var pb = new Playback.Playback(a => a());
        var fires = new System.Collections.Generic.List<bool>();
        pb.SourceHdrChanged += v => fires.Add(v);

        pb.UpdateSourceHdr(null);
        pb.UpdateSourceHdr("bt.1886");
        pb.UpdateSourceHdr("srgb");

        Assert.That(fires, Is.Empty);
    }

    [Test]
    public void SourceHdrChangedDoesNotFireOnHdrRepeats()
    {
        using var pb = new Playback.Playback(a => a());
        var fires = new System.Collections.Generic.List<bool>();

        pb.UpdateSourceHdr("pq");
        pb.SourceHdrChanged += v => fires.Add(v);

        // Both pq and hlg are HDR — switching between them is not a state transition.
        pb.UpdateSourceHdr("pq");
        pb.UpdateSourceHdr("hlg");
        pb.UpdateSourceHdr("pq");

        Assert.That(fires, Is.Empty);
    }

    [Test]
    public void SourceHdrChangedFiresOnHdrToSdrTransition()
    {
        using var pb = new Playback.Playback(a => a());
        pb.UpdateSourceHdr("pq");

        var fires = new System.Collections.Generic.List<bool>();
        pb.SourceHdrChanged += v => fires.Add(v);

        pb.UpdateSourceHdr("bt.1886");

        Assert.That(fires, Is.EqualTo(new[] { false }));
    }

    [Test]
    public void HwdecCurrentEmptyAndNullCoalesceToNull()
    {
        using var pb = new Playback.Playback(a => a());
        pb.UpdateHwdecCurrent("");
        Assert.That(pb.HwdecCurrent, Is.Null);
        pb.UpdateHwdecCurrent(null);
        Assert.That(pb.HwdecCurrent, Is.Null);
    }

    [Test]
    public void HwdecCurrentTracksBackendName()
    {
        using var pb = new Playback.Playback(a => a());
        pb.UpdateHwdecCurrent("vaapi");
        Assert.That(pb.HwdecCurrent, Is.EqualTo("vaapi"));
        pb.UpdateHwdecCurrent("no");
        Assert.That(pb.HwdecCurrent, Is.EqualTo("no"));
    }

    [Test]
    public void HwdecCurrentNotifiesOnlyOnTransition()
    {
        using var pb = new Playback.Playback(a => a());
        var fires = new System.Collections.Generic.List<string?>();
        pb.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Playback.Playback.HwdecCurrent))
            {
                fires.Add(pb.HwdecCurrent);
            }
        };

        pb.UpdateHwdecCurrent(null);      // null -> null, no fire
        pb.UpdateHwdecCurrent("vaapi");   // null -> vaapi
        pb.UpdateHwdecCurrent("vaapi");   // dedup
        pb.UpdateHwdecCurrent("no");      // vaapi -> no
        pb.UpdateHwdecCurrent("");        // no -> null (empty coalesces)

        Assert.That(fires, Is.EqualTo(new string?[] { "vaapi", "no", null }));
    }

    [Test]
    public void SourceHdrChangedFiresFullTransitionCycle()
    {
        using var pb = new Playback.Playback(a => a());
        var fires = new System.Collections.Generic.List<bool>();
        pb.SourceHdrChanged += v => fires.Add(v);

        pb.UpdateSourceHdr("bt.1886"); // SDR -> SDR (no fire)
        pb.UpdateSourceHdr("pq");      // SDR -> HDR (true)
        pb.UpdateSourceHdr("hlg");     // HDR -> HDR (no fire)
        pb.UpdateSourceHdr("srgb");    // HDR -> SDR (false)
        pb.UpdateSourceHdr("pq");      // SDR -> HDR (true)

        Assert.That(fires, Is.EqualTo(new[] { true, false, true }));
    }

    // Track-list transitions exercise the same internal-method seam used elsewhere (UpdateSourceHdr / UpdateHwdecCurrent): no real mpv pump, just direct state writes that verify dedup semantics. The dedup matters because mpv's `track-list/count` observation fires for any track-type change (audio, video, sub) — a single audio track add otherwise re-allocates and re-notifies on the video and subtitle subsets too.
    [Test]
    public void TrackListsDefaultToEmpty()
    {
        using var pb = new Playback.Playback(a => a());
        Assert.That(pb.VideoTracks, Is.Empty);
        Assert.That(pb.AudioTracks, Is.Empty);
        Assert.That(pb.SubtitleTracks, Is.Empty);
    }

    [Test]
    public void TrackListsUpdateOnNewSnapshot()
    {
        using var pb = new Playback.Playback(a => a());
        var video = new[] { new MediaTrack(1, null, null, false) };
        var audio = new[] { new MediaTrack(1, null, "eng", false) };
        var subs = new[] { new MediaTrack(1, "English", "eng", false) };
        pb.UpdateVideoTracks(video);
        pb.UpdateAudioTracks(audio);
        pb.UpdateSubtitleTracks(subs);
        Assert.That(pb.VideoTracks, Is.EqualTo(video));
        Assert.That(pb.AudioTracks, Is.EqualTo(audio));
        Assert.That(pb.SubtitleTracks, Is.EqualTo(subs));
    }

    [Test]
    public void TrackListsDedupEqualSnapshots()
    {
        using var pb = new Playback.Playback(a => a());
        var fires = new System.Collections.Generic.List<string>();
        pb.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Playback.Playback.VideoTracks)
                || e.PropertyName == nameof(Playback.Playback.AudioTracks)
                || e.PropertyName == nameof(Playback.Playback.SubtitleTracks))
            {
                fires.Add(e.PropertyName);
            }
        };

        var first = new[] { new MediaTrack(1, "English", "eng", false) };
        var sameContent = new[] { new MediaTrack(1, "English", "eng", false) };
        var different = new[] { new MediaTrack(2, "French", "fre", false) };

        pb.UpdateSubtitleTracks(first);
        pb.UpdateSubtitleTracks(sameContent); // record-equal → no PropertyChanged
        pb.UpdateSubtitleTracks(different);

        // Symmetric check on a different kind to confirm the dedup helper is shared.
        pb.UpdateAudioTracks(first);
        pb.UpdateAudioTracks(sameContent); // same dedup
        pb.UpdateAudioTracks(different);

        Assert.That(fires, Is.EqualTo(new[]
        {
            nameof(Playback.Playback.SubtitleTracks),
            nameof(Playback.Playback.SubtitleTracks),
            nameof(Playback.Playback.AudioTracks),
            nameof(Playback.Playback.AudioTracks),
        }));
    }

    [Test]
    public void TrackListsDedupBackToEmpty()
    {
        using var pb = new Playback.Playback(a => a());
        var fires = new System.Collections.Generic.List<string>();
        pb.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Playback.Playback.VideoTracks)
                || e.PropertyName == nameof(Playback.Playback.AudioTracks)
                || e.PropertyName == nameof(Playback.Playback.SubtitleTracks))
            {
                fires.Add(e.PropertyName);
            }
        };

        // Initial state is empty for all three; pushing another empty snapshot to any of them must not fire (defends against the spurious-track-list-count-fire case where one kind changes and the other two don't).
        pb.UpdateVideoTracks(Array.Empty<MediaTrack>());
        pb.UpdateAudioTracks(Array.Empty<MediaTrack>());
        pb.UpdateSubtitleTracks(Array.Empty<MediaTrack>());
        Assert.That(fires, Is.Empty);
    }

    [Test]
    public void CurrentTrackIdsTrackValue()
    {
        using var pb = new Playback.Playback(a => a());
        var fires = new System.Collections.Generic.List<(string Name, int? Value)>();
        pb.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Playback.Playback.CurrentVideoId))
            {
                fires.Add((e.PropertyName, pb.CurrentVideoId));
            }
            else if (e.PropertyName == nameof(Playback.Playback.CurrentAudioId))
            {
                fires.Add((e.PropertyName, pb.CurrentAudioId));
            }
            else if (e.PropertyName == nameof(Playback.Playback.CurrentSubtitleId))
            {
                fires.Add((e.PropertyName, pb.CurrentSubtitleId));
            }
        };

        pb.UpdateCurrentVideoId(1);
        pb.UpdateCurrentVideoId(1); // dedup (ObservableProperty same-value gate)
        pb.UpdateCurrentAudioId(2);
        pb.UpdateCurrentSubtitleId(3);
        pb.UpdateCurrentVideoId(null);
        pb.UpdateCurrentAudioId(null);
        pb.UpdateCurrentSubtitleId(null);

        Assert.That(fires, Is.EqualTo(new[]
        {
            (nameof(Playback.Playback.CurrentVideoId), (int?)1),
            (nameof(Playback.Playback.CurrentAudioId), (int?)2),
            (nameof(Playback.Playback.CurrentSubtitleId), (int?)3),
            (nameof(Playback.Playback.CurrentVideoId), (int?)null),
            (nameof(Playback.Playback.CurrentAudioId), (int?)null),
            (nameof(Playback.Playback.CurrentSubtitleId), (int?)null),
        }));
    }

}
