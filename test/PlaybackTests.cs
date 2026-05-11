using System;
using Vomplayer.Mpv;
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

    // Regression: a `track-list/count` property change can arrive on the main thread (via postToMainThread → GLib idle) after Dispose has already torn down the dispatcher. OnMpvPropertyChanged would then route into ReloadTracks → dispatcher.Post and throw ObjectDisposedException, surfacing as `[vomplayer] idle callback threw: ObjectDisposedException` on shutdown.
    [Test]
    public void PropertyChangeAfterDisposeIsSwallowed()
    {
        var pb = new Playback.Playback(a => a());
        pb.Dispose();
        var change = new PropertyChange("track-list/count", new MpvPropertyValue("0"), 0);
        Assert.DoesNotThrow(() => pb.IngestPropertyChangeForTest(change));
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

    // Volume defaults to 100 so the slider lands at full pre-Initialize and the synthesized first observe fire (which carries mpv's real default of 100) doesn't visibly jump on screen.
    [Test]
    public void VolumeDefaultsToHundred()
    {
        using var pb = new Playback.Playback(a => a());
        Assert.That(pb.Volume, Is.EqualTo(100));
    }

    [Test]
    public void IsMutedDefaultsFalse()
    {
        using var pb = new Playback.Playback(a => a());
        Assert.That(pb.IsMuted, Is.False);
    }

    [Test]
    public void UpdateVolumeTracksValue()
    {
        using var pb = new Playback.Playback(a => a());
        pb.UpdateVolume(42);
        Assert.That(pb.Volume, Is.EqualTo(42));
    }

    [Test]
    public void UpdateVolumeNullCoalescesToHundred()
    {
        // mpv shouldn't fire null for `volume` (not a file-bound property), but if it does we land at the documented default rather than carrying a stale value forward — symmetric with the existing time-pos/duration ?? 0 pattern.
        using var pb = new Playback.Playback(a => a());
        pb.UpdateVolume(50);
        pb.UpdateVolume(null);
        Assert.That(pb.Volume, Is.EqualTo(100));
    }

    [Test]
    public void UpdateMuteTracksValue()
    {
        using var pb = new Playback.Playback(a => a());
        pb.UpdateMute(true);
        Assert.That(pb.IsMuted, Is.True);
        pb.UpdateMute(false);
        Assert.That(pb.IsMuted, Is.False);
    }

    [Test]
    public void UpdateMuteNullCoalescesToFalse()
    {
        using var pb = new Playback.Playback(a => a());
        pb.UpdateMute(true);
        pb.UpdateMute(null);
        Assert.That(pb.IsMuted, Is.False);
    }

    [Test]
    public void IsEofReachedDefaultsFalse()
    {
        using var pb = new Playback.Playback(a => a());
        Assert.That(pb.IsEofReached, Is.False);
    }

    [Test]
    public void UpdateIsEofReachedTracksValue()
    {
        using var pb = new Playback.Playback(a => a());
        pb.UpdateIsEofReached(true);
        Assert.That(pb.IsEofReached, Is.True);
        pb.UpdateIsEofReached(false);
        Assert.That(pb.IsEofReached, Is.False);
    }

    [Test]
    public void UpdateIsEofReachedNullCoalescesToFalse()
    {
        using var pb = new Playback.Playback(a => a());
        pb.UpdateIsEofReached(true);
        pb.UpdateIsEofReached(null);
        Assert.That(pb.IsEofReached, Is.False);
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
        Assert.Throws<ArgumentNullException>(() => pb.LoadFile(null!, startPaused: false));
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
    public void HwdecTranscriptDefaultsEmpty()
    {
        using var pb = new Playback.Playback(a => a());
        Assert.That(pb.HwdecTranscript, Is.Empty);
    }

    [Test]
    public void HwdecTranscriptCapturesHwdecPrefixedLines()
    {
        using var pb = new Playback.Playback(a => a());
        pb.IngestLogMessageForTest(new Vomplayer.Mpv.LogMessage("vd", "v", "Looking at hwdec auto-safe."), 0);
        pb.IngestLogMessageForTest(new Vomplayer.Mpv.LogMessage("vaapi", "warn", "Profile not supported."), 0);
        pb.IngestLogMessageForTest(new Vomplayer.Mpv.LogMessage("vd", "info", "Selecting hardware decoder vaapi-copy."), 0);
        Assert.That(pb.HwdecTranscript, Is.EqualTo(new[]
        {
            "[vd/v] Looking at hwdec auto-safe.",
            "[vaapi/warn] Profile not supported.",
            "[vd/info] Selecting hardware decoder vaapi-copy.",
        }));
    }

    [Test]
    public void HwdecTranscriptCapturesSubprefixedLines()
    {
        // Real mpv 0.40 emits hwdec-rejection lines under sub-prefixes like ffmpeg/h264_vaapi or vd/lavc — the strict-equality filter that an earlier draft used would have dropped them. Verify the prefix-root match catches the slash-suffixed forms.
        using var pb = new Playback.Playback(a => a());
        pb.IngestLogMessageForTest(new Vomplayer.Mpv.LogMessage("ffmpeg/h264_vaapi", "warn", "Failed to initialise VAAPI connection: -1"), 0);
        pb.IngestLogMessageForTest(new Vomplayer.Mpv.LogMessage("vd/lavc", "v", "Decoder reconfig"), 0);
        pb.IngestLogMessageForTest(new Vomplayer.Mpv.LogMessage("vo/gpu", "info", "Using vaapi (copy) as hwdec interop"), 0);
        Assert.That(pb.HwdecTranscript, Is.EqualTo(new[]
        {
            "[ffmpeg/h264_vaapi/warn] Failed to initialise VAAPI connection: -1",
            "[vd/lavc/v] Decoder reconfig",
            "[vo/gpu/info] Using vaapi (copy) as hwdec interop",
        }));
    }

    [Test]
    public void HwdecTranscriptDropsUnrelatedPrefixes()
    {
        using var pb = new Playback.Playback(a => a());
        // cplayer is mpv's own command-line front-end; ao is audio-out; both fire chatty "v"-level messages we don't want polluting the hwdec trail. Note `vdec_lavc` is NOT a real mpv prefix but verifies the root-match doesn't accidentally match `vd` against an unrelated identifier sharing the prefix's letters.
        pb.IngestLogMessageForTest(new Vomplayer.Mpv.LogMessage("cplayer", "v", "Playing: foo.mp4"), 0);
        pb.IngestLogMessageForTest(new Vomplayer.Mpv.LogMessage("ao", "v", "Audio out reconfig."), 0);
        pb.IngestLogMessageForTest(new Vomplayer.Mpv.LogMessage("vdec_lavc", "v", "Spurious match guard."), 0);
        pb.IngestLogMessageForTest(new Vomplayer.Mpv.LogMessage("vd", "v", "Trying hardware decoding via vaapi."), 0);
        Assert.That(pb.HwdecTranscript, Is.EqualTo(new[]
        {
            "[vd/v] Trying hardware decoding via vaapi.",
        }));
    }

    [Test]
    public void HwdecTranscriptEvictsOldestPastLimit()
    {
        // Bound is 256; push past it and verify the queue shrunk to bound and the oldest line was dropped.
        using var pb = new Playback.Playback(a => a());
        const int total = 300;
        for (int i = 0; i < total; i++)
        {
            pb.IngestLogMessageForTest(new Vomplayer.Mpv.LogMessage("vd", "v", $"line {i}"), 0);
        }
        Assert.That(pb.HwdecTranscript, Has.Count.EqualTo(256));
        Assert.That(pb.HwdecTranscript[0], Is.EqualTo($"[vd/v] line {total - 256}"));
        Assert.That(pb.HwdecTranscript[255], Is.EqualTo($"[vd/v] line {total - 1}"));
    }

    [Test]
    public void HwdecTranscriptClearsOnEpochAdvance()
    {
        // The LoadFile boundary doesn't clear synchronously — instead, the dispatcher worker bumps an epoch counter and stamps it onto subsequent log messages, and main-thread receipt clears the transcript when an epoch-bumped message (or an explicit epoch-advance marker) arrives. Verify both paths: bump via a higher-epoch message, and bump via the explicit AdvanceLogEpoch marker (used by LoadFile so a load with no log emissions still clears).
        using var pb = new Playback.Playback(a => a());
        pb.IngestLogMessageForTest(new Vomplayer.Mpv.LogMessage("vd", "v", "previous-file tail"), 0);
        Assert.That(pb.HwdecTranscript, Has.Count.EqualTo(1));

        // Path 1: epoch advanced by an arriving message.
        pb.IngestLogMessageForTest(new Vomplayer.Mpv.LogMessage("vd", "v", "new-file probe"), 1);
        Assert.That(pb.HwdecTranscript, Is.EqualTo(new[] { "[vd/v] new-file probe" }));

        // Path 2: explicit epoch advance clears even when no new message follows yet.
        pb.AdvanceLogEpochForTest(2);
        Assert.That(pb.HwdecTranscript, Is.Empty);
    }

    [Test]
    public void HwdecTranscriptIgnoresStaleEpoch()
    {
        // Once main has advanced past an epoch, late-arriving lower-epoch messages still get appended (they're not racing the boundary; they belong to the file whose epoch they carry, which has already become "old"). The clear is on advance, not on rejection; we deliberately don't try to drop tail noise that already passed the prefix filter — the next epoch-bump clears it. Pin this behavior so a future rewrite can't tighten it without an explicit decision.
        using var pb = new Playback.Playback(a => a());
        pb.AdvanceLogEpochForTest(2);
        pb.IngestLogMessageForTest(new Vomplayer.Mpv.LogMessage("vd", "v", "stale tail"), 1);
        Assert.That(pb.HwdecTranscript, Is.EqualTo(new[] { "[vd/v] stale tail" }));
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
        var video = new[] { new MediaTrack(1, null, null, false, null) };
        var audio = new[] { new MediaTrack(1, null, "eng", false, null) };
        var subs = new[] { new MediaTrack(1, "English", "eng", false, null) };
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

        var first = new[] { new MediaTrack(1, "English", "eng", false, null) };
        var sameContent = new[] { new MediaTrack(1, "English", "eng", false, null) };
        var different = new[] { new MediaTrack(2, "French", "fre", false, null) };

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
    public void TracksReloadedFiresAfterAllThreeUpdates()
    {
        // Drives the Update*Tracks methods directly (the same seam ReloadTracks's main-thread post calls). TracksReloaded itself fires inside ReloadTracks's lambda — not inside Update*Tracks — so this test exercises the event subscription hookup, not the firing site directly. The firing-site test would need the dispatcher worker, which is mpv-bound.
        using var pb = new Playback.Playback(a => a());
        int fires = 0;
        pb.TracksReloaded += () => fires++;

        // No automatic firing on Update*Tracks (those just set the property + fire PropertyChanged); TracksReloaded fires only from the ReloadTracks main-thread callback. Drive it explicitly via the test seam.
        pb.RaiseTracksReloadedForTest();
        pb.RaiseTracksReloadedForTest();

        Assert.That(fires, Is.EqualTo(2));
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

    [Test]
    public void ChaptersDefaultEmpty()
    {
        using var pb = new Playback.Playback(a => a());
        Assert.That(pb.Chapters, Is.Empty);
    }

    [Test]
    public void ChaptersUpdateOnNewSnapshot()
    {
        using var pb = new Playback.Playback(a => a());
        var snap = new[]
        {
            new MediaChapter(0, "Intro", 0.0),
            new MediaChapter(1, "Act 1", 60.0),
        };
        pb.UpdateChapters(snap);
        Assert.That(pb.Chapters, Is.EqualTo(snap));
    }

    [Test]
    public void ChaptersDedupEqualSnapshots()
    {
        using var pb = new Playback.Playback(a => a());
        var fires = 0;
        pb.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Playback.Playback.Chapters))
            {
                fires++;
            }
        };

        var first = new[] { new MediaChapter(0, "Intro", 0.0), new MediaChapter(1, "Act 1", 60.0) };
        var sameContent = new[] { new MediaChapter(0, "Intro", 0.0), new MediaChapter(1, "Act 1", 60.0) };
        var different = new[] { new MediaChapter(0, "Intro", 0.0), new MediaChapter(1, "Act 1", 90.0) };

        pb.UpdateChapters(first);
        pb.UpdateChapters(sameContent); // record-equal → no PropertyChanged
        pb.UpdateChapters(different);

        Assert.That(fires, Is.EqualTo(2));
    }

    [Test]
    public void ChaptersDedupBackToEmpty()
    {
        using var pb = new Playback.Playback(a => a());
        int fires = 0;
        pb.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Playback.Playback.Chapters))
            {
                fires++;
            }
        };
        pb.UpdateChapters(Array.Empty<MediaChapter>());
        Assert.That(fires, Is.Zero);
    }

    // The trust-monitor wiring lives in OnMpvPropertyChanged's switch — feed PropertyChange events directly through the test seam so a future refactor that decouples the observer from the monitor surfaces here.
    private static Playback.Playback NewWithFakeClock(Func<double> clock)
    {
        var pb = new Playback.Playback(a => a(), clock);
        pb.Initialize();
        return pb;
    }

    [Test]
    public void ContainerFpsObservationMirrorsToVideoFps()
    {
        double now = 0.0;
        using var pb = NewWithFakeClock(() => now);
        pb.IngestPropertyChangeForTest(new PropertyChange("container-fps", new MpvPropertyValue(25.0), 0));
        Assert.That(pb.VideoFps, Is.EqualTo(25.0));
        Assert.That(pb.IsSourceFpsTrusted, Is.True);
    }

    [Test]
    public void EstimatedVfFpsObservationFeedsTrustMonitor()
    {
        double now = 0.0;
        using var pb = NewWithFakeClock(() => now);
        pb.IngestPropertyChangeForTest(new PropertyChange("container-fps", new MpvPropertyValue(25.0), 0));
        // Fast-forward past warmup, then sustain divergence past the threshold.
        now = 2.5;
        pb.IngestPropertyChangeForTest(new PropertyChange("estimated-vf-fps", new MpvPropertyValue(15.0), 0));
        now = 5.6;
        pb.IngestPropertyChangeForTest(new PropertyChange("estimated-vf-fps", new MpvPropertyValue(15.0), 0));
        Assert.That(pb.IsSourceFpsTrusted, Is.False);
        Assert.That(pb.EstimatedVfFps, Is.EqualTo(15.0));
    }

    [Test]
    public void IsSourceFpsTrustedChangedFiresOnFlip()
    {
        double now = 0.0;
        using var pb = NewWithFakeClock(() => now);
        bool? lastFire = null;
        pb.IsSourceFpsTrustedChanged += b => lastFire = b;
        pb.IngestPropertyChangeForTest(new PropertyChange("container-fps", new MpvPropertyValue(25.0), 0));
        now = 2.5;
        pb.IngestPropertyChangeForTest(new PropertyChange("estimated-vf-fps", new MpvPropertyValue(15.0), 0));
        now = 5.6;
        pb.IngestPropertyChangeForTest(new PropertyChange("estimated-vf-fps", new MpvPropertyValue(15.0), 0));
        Assert.That(lastFire, Is.False);
    }

    [Test]
    public void ClearAfterSetIssuesRemoveOnlyOnce()
    {
        // Regression: Set then Clear should leave frameMultiplierApplied=false so a subsequent Clear is a no-op (no spurious mpv command). Tests the same-thread state machine without depending on the dispatcher worker.
        using var pb = NewWithFakeClock(() => 0.0);
        // Drive the LoadFile preempt scenario indirectly: Set, then Clear (twice).
        pb.SetFrameMultiplier(48.0);
        pb.ClearFrameMultiplier();
        pb.ClearFrameMultiplier();
        // Can't observe dispatcher.Post counts in-process; the assertion is "no exception throws and state is internally consistent". Real-mpv coverage lives in manual smoke + the VideoContext-level regression test.
        Assert.Pass();
    }

    [Test]
    public void SetFrameMultiplierUpdatesTrustMonitorExpected()
    {
        // Regression: applying vf=fps doubles estimated-vf-fps without indicating VFR. The monitor must compare against the expected post-filter rate (set by SetFrameMultiplier), not the declared source rate. Without this update, applying ×2 on a CFR 25 fps source would trip sustained divergence (50 vs declared 25), flip Untrusted, and unwind the filter — a self-defeating loop.
        double now = 0.0;
        using var pb = NewWithFakeClock(() => now);
        pb.IngestPropertyChangeForTest(new PropertyChange("container-fps", new MpvPropertyValue(25.0), 0));
        // Policy decides ×2; tells Playback. (In production VideoContext.ApplyVrrPolicy makes this call.)
        now = 0.5;
        pb.SetFrameMultiplier(50.0);
        // Past warmup, estimated converges to post-filter 50.
        now = 3.0;
        pb.IngestPropertyChangeForTest(new PropertyChange("estimated-vf-fps", new MpvPropertyValue(50.0), 0));
        now = 5.0;
        pb.IngestPropertyChangeForTest(new PropertyChange("estimated-vf-fps", new MpvPropertyValue(50.0), 0));
        now = 7.0;
        pb.IngestPropertyChangeForTest(new PropertyChange("estimated-vf-fps", new MpvPropertyValue(50.0), 0));
        Assert.That(pb.IsSourceFpsTrusted, Is.True);
    }

    [Test]
    public void ClearFrameMultiplierRestoresExpectedToDeclared()
    {
        // After clearing the multiplier (e.g. dragged to a non-VRR output), estimated-vf-fps reverts to the declared source rate. Trust monitor must follow.
        double now = 0.0;
        using var pb = NewWithFakeClock(() => now);
        pb.IngestPropertyChangeForTest(new PropertyChange("container-fps", new MpvPropertyValue(25.0), 0));
        now = 0.5;
        pb.SetFrameMultiplier(50.0);
        now = 5.0;
        pb.ClearFrameMultiplier();
        // Past the post-clear warmup, estimated should match declared 25.
        now = 8.0;
        pb.IngestPropertyChangeForTest(new PropertyChange("estimated-vf-fps", new MpvPropertyValue(25.0), 0));
        now = 10.0;
        pb.IngestPropertyChangeForTest(new PropertyChange("estimated-vf-fps", new MpvPropertyValue(25.0), 0));
        Assert.That(pb.IsSourceFpsTrusted, Is.True);
    }

    [Test]
    public void SeekEndedTransitionRestartsWarmup()
    {
        double now = 0.0;
        using var pb = NewWithFakeClock(() => now);
        pb.IngestPropertyChangeForTest(new PropertyChange("container-fps", new MpvPropertyValue(25.0), 0));
        // Past warmup, accumulate some pre-flip disagreement.
        now = 2.5;
        pb.IngestPropertyChangeForTest(new PropertyChange("estimated-vf-fps", new MpvPropertyValue(15.0), 0));
        now = 4.0;
        pb.IngestPropertyChangeForTest(new PropertyChange("estimated-vf-fps", new MpvPropertyValue(15.0), 0));
        // Seek begins, then ends. Warmup restarts. mpv flags marshal as int (1 = true, 0 = false) — see MpvPropertyValue.AsFlag.
        pb.IngestPropertyChangeForTest(new PropertyChange("seeking", new MpvPropertyValue(1), 0));
        now = 5.0;
        pb.IngestPropertyChangeForTest(new PropertyChange("seeking", new MpvPropertyValue(0), 0));
        // Within the new warmup, divergent samples must be discarded.
        now = 6.5;
        pb.IngestPropertyChangeForTest(new PropertyChange("estimated-vf-fps", new MpvPropertyValue(15.0), 0));
        Assert.That(pb.IsSourceFpsTrusted, Is.True);
    }
}
