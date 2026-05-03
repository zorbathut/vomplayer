using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Vomplayer.Playback;
using Vomplayer.Services;
using Vomplayer.UserData;
using Vomplayer.ViewModels;

namespace Vomplayer.Tests;

[TestFixture]
public partial class MultiVideoCoordinatorTests
{
    // FakePlayback for coordinator tests. Tracks play/pause/seek/frame/chapter/volume call counts so we can verify fan-out vs. isolated routing.
    private sealed partial class FakePlayback : ObservableObject, IPlayback
    {
        [ObservableProperty]
        private double positionSeconds;

        [ObservableProperty]
        private double durationSeconds;

        [ObservableProperty]
        private bool isPaused = true;

        [ObservableProperty]
        private bool isSeeking;

        [ObservableProperty]
        private bool isCoreIdle = true;

        [ObservableProperty]
        private bool isEofReached;

        [ObservableProperty]
        private double volume = 100;

        [ObservableProperty]
        private bool isMuted;

        [ObservableProperty]
        private IReadOnlyList<MediaTrack> videoTracks = Array.Empty<MediaTrack>();

        [ObservableProperty]
        private IReadOnlyList<MediaTrack> audioTracks = Array.Empty<MediaTrack>();

        [ObservableProperty]
        private IReadOnlyList<MediaTrack> subtitleTracks = Array.Empty<MediaTrack>();

        [ObservableProperty]
        private IReadOnlyList<MediaChapter> chapters = Array.Empty<MediaChapter>();

        [ObservableProperty]
        private int? currentVideoId;

        [ObservableProperty]
        private int? currentAudioId;

        [ObservableProperty]
        private int? currentSubtitleId;

        [ObservableProperty]
        private double? videoAspect;

        [ObservableProperty]
        private double? videoFps;

        [ObservableProperty]
        private string? mediaTitle;

        public event Action? FileLoaded;
        public event Action<int>? FileEnded;
        public event Action? TracksReloaded;
        public event Action<bool>? SourceHdrChanged;

        public bool IsSourceHdr { get; set; }
        public string? HwdecCurrent { get; set; }
        public string DiagTag { get; set; } = "?";

        public List<string> LoadedFiles { get; } = new();
        public int TogglePauseCalls { get; private set; }
        public List<bool> SetPausedCalls { get; } = new();
        public List<double> SeekCalls { get; } = new();
        public List<double> SeekRelativeCalls { get; } = new();
        public int StepFrameForwardCalls { get; private set; }
        public int StepFrameBackCalls { get; private set; }
        public List<int> StepChapterCalls { get; } = new();
        public List<double> SetVolumeCalls { get; } = new();
        public int ToggleMuteCalls { get; private set; }
        public int EnableHdrOutputCalls { get; private set; }
        public int DisableHdrOutputCalls { get; private set; }

        public void Initialize() { }
        public void LoadFile(string path) { LoadedFiles.Add(path); }
        public void TogglePause() { TogglePauseCalls++; IsPaused = !IsPaused; }
        public void SetPaused(bool paused) { SetPausedCalls.Add(paused); IsPaused = paused; }
        public void Seek(double seconds) { SeekCalls.Add(seconds); }
        public void SeekRelative(double seconds) { SeekRelativeCalls.Add(seconds); }
        public void StepFrameForward() { StepFrameForwardCalls++; }
        public void StepFrameBack() { StepFrameBackCalls++; }
        public void StepChapter(int delta) { StepChapterCalls.Add(delta); }
        public void LoadAudio(string path) { }
        public void LoadSubtitle(string path) { }
        public void SetVideo(int? trackId) { }
        public void SetAudio(int? trackId) { }
        public void SetSubtitle(int? trackId) { }
        public void SetVolume(double percent) { SetVolumeCalls.Add(percent); Volume = percent; }
        public void AdjustVolume(double deltaPercent) { }
        public void ToggleMute() { ToggleMuteCalls++; IsMuted = !IsMuted; }
        public void EnableHdrOutput() { EnableHdrOutputCalls++; }
        public void DisableHdrOutput() { DisableHdrOutputCalls++; }

        public void RaiseFileLoaded() { FileLoaded?.Invoke(); }
        public void RaiseFileEnded(int reason) { FileEnded?.Invoke(reason); }
        public void RaiseTracksReloaded() { TracksReloaded?.Invoke(); }
        public void RaiseSourceHdrChanged(bool isHdr) { IsSourceHdr = isHdr; SourceHdrChanged?.Invoke(isHdr); }

        public void Dispose() { }
    }

    private sealed class StubFilePicker : IFilePicker
    {
        public Task<string?> PickVideoFileAsync(string title) { return Task.FromResult<string?>(null); }
        public Task<string?> PickAudioFileAsync(string title) { return Task.FromResult<string?>(null); }
        public Task<string?> PickSubtitleFileAsync(string title) { return Task.FromResult<string?>(null); }
    }

    private sealed class StubRecentFiles : IRecentFiles
    {
        public void Record(string pathOrUri) { }
        public IReadOnlyList<RecentFileEntry> GetMostRecent(int limit) { return Array.Empty<RecentFileEntry>(); }
        public void RecordPosition(string pathOrUri, double positionSeconds) { }
        public double? GetPosition(string pathOrUri) { return null; }
    }

    private sealed class StubTrackPreferences : ITrackPreferences
    {
        public void Record(string directory, MediaKind kind, TrackPreference preference) { }
        public TrackPreference? Get(string directory, MediaKind kind) { return null; }
    }

    private sealed class StubUrlDownloader : IUrlDownloader
    {
        public bool IsAvailable() { return false; }
        public Task<IReadOnlyList<string>> ProbeAsync(string url, CancellationToken ct) { return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()); }
        public Task<string> DownloadAsync(string url, IProgress<UrlDownloadProgress>? progress, CancellationToken ct) { return Task.FromResult(url); }
    }

    private sealed class StubUrlPrompt : IUrlPrompt
    {
        public Task<string?> PromptForUrlAsync(string title) { return Task.FromResult<string?>(null); }
        public void ShowError(string title, string message) { }
        public UrlProgressHandle ShowDownloadProgress(string title, CancellationTokenSource cts)
        {
            return new UrlProgressHandle(new NoopDisposable(), new Progress<UrlDownloadProgress>(_ => { }));
        }
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }

    // Owns one FakePlayback + the ViewModel built around it. EnableSecondary spins up a second FakePlayback + VideoContext, hands it to the VM.
    private sealed class Harness : IDisposable
    {
        public FakePlayback PrimaryPlayback { get; }
        public FakePlayback? SecondaryPlayback { get; private set; }
        public ViewModelMain Vm { get; }

        public Harness()
        {
            PrimaryPlayback = new FakePlayback();
            Vm = new ViewModelMain(PrimaryPlayback, new StubFilePicker(), new StubRecentFiles(), new StubTrackPreferences(), new StubUrlDownloader(), new StubUrlPrompt());
        }

        public VideoContext NewSecondary()
        {
            SecondaryPlayback = new FakePlayback();
            return new VideoContext(SecondaryPlayback, new StubFilePicker(), new StubRecentFiles(), new StubTrackPreferences(), new StubUrlDownloader(), new StubUrlPrompt());
        }

        public void EnablePip()
        {
            Vm.EnablePip(NewSecondary());
        }

        // Drive a single context to file-EOF: load a playlist, fire FileLoaded, set duration, set EOF. Mirrors a natural file end.
        public static void ReachFileEof(VideoContext ctx, FakePlayback pb)
        {
            pb.RaiseFileLoaded();
            pb.DurationSeconds = 60;
            pb.IsEofReached = true;
        }

        public void Dispose()
        {
            Vm.Dispose();
        }
    }

    [Test]
    public void PipOffAdvancesPrimaryAtEofLikeSingleVideo()
    {
        // Regression check: with PiP off, the coordinator must NOT have stolen advance dispatch from VideoContext. Primary should auto-advance at EOF exactly as it did pre-coordinator.
        using var h = new Harness();
        h.Vm.LoadPaths(new[] { "/a.mp4", "/b.mp4" }, replace: true);
        Harness.ReachFileEof(h.Vm.Primary, h.PrimaryPlayback);
        Assert.That(h.Vm.Primary.Playlist.CurrentIndex, Is.EqualTo(1));
        Assert.That(h.PrimaryPlayback.LoadedFiles, Is.EqualTo(new[] { "/a.mp4", "/b.mp4" }));
    }

    [Test]
    public void PipOnBothAtEofWithNextAdvancesBoth()
    {
        using var h = new Harness();
        h.Vm.LoadPaths(new[] { "/a1.mp4", "/a2.mp4" }, replace: true);
        h.EnablePip();
        h.Vm.Secondary!.LoadPaths(new[] { "/b1.mp4", "/b2.mp4" }, replace: true);

        // Primary reaches EOF first — must NOT advance alone (lockstep gate).
        Harness.ReachFileEof(h.Vm.Primary, h.PrimaryPlayback);
        Assert.That(h.Vm.Primary.Playlist.CurrentIndex, Is.EqualTo(0), "lockstep: primary alone must not advance");
        Assert.That(h.PrimaryPlayback.LoadedFiles, Is.EqualTo(new[] { "/a1.mp4" }));

        // Secondary reaches EOF — both advance.
        Harness.ReachFileEof(h.Vm.Secondary!, h.SecondaryPlayback!);
        Assert.That(h.Vm.Primary.Playlist.CurrentIndex, Is.EqualTo(1));
        Assert.That(h.Vm.Secondary!.Playlist.CurrentIndex, Is.EqualTo(1));
        Assert.That(h.PrimaryPlayback.LoadedFiles, Is.EqualTo(new[] { "/a1.mp4", "/a2.mp4" }));
        Assert.That(h.SecondaryPlayback!.LoadedFiles, Is.EqualTo(new[] { "/b1.mp4", "/b2.mp4" }));
    }

    [Test]
    public void PipOnBothAtEofPrimaryOutOfItemsDoesNotAdvance()
    {
        using var h = new Harness();
        h.Vm.LoadPaths(new[] { "/a.mp4" }, replace: true);
        h.EnablePip();
        h.Vm.Secondary!.LoadPaths(new[] { "/b1.mp4", "/b2.mp4" }, replace: true);

        Harness.ReachFileEof(h.Vm.Primary, h.PrimaryPlayback);
        Harness.ReachFileEof(h.Vm.Secondary!, h.SecondaryPlayback!);
        // Primary at last item; lockstep halts the pair.
        Assert.That(h.Vm.Primary.Playlist.CurrentIndex, Is.EqualTo(0));
        Assert.That(h.Vm.Secondary!.Playlist.CurrentIndex, Is.EqualTo(0));
        Assert.That(h.PrimaryPlayback.LoadedFiles, Is.EqualTo(new[] { "/a.mp4" }));
        Assert.That(h.SecondaryPlayback!.LoadedFiles, Is.EqualTo(new[] { "/b1.mp4" }));
    }

    [Test]
    public void PipOnBothAtEofSecondaryOutOfItemsDoesNotAdvance()
    {
        using var h = new Harness();
        h.Vm.LoadPaths(new[] { "/a1.mp4", "/a2.mp4" }, replace: true);
        h.EnablePip();
        h.Vm.Secondary!.LoadPaths(new[] { "/b.mp4" }, replace: true);

        Harness.ReachFileEof(h.Vm.Primary, h.PrimaryPlayback);
        Harness.ReachFileEof(h.Vm.Secondary!, h.SecondaryPlayback!);
        Assert.That(h.Vm.Primary.Playlist.CurrentIndex, Is.EqualTo(0));
        Assert.That(h.Vm.Secondary!.Playlist.CurrentIndex, Is.EqualTo(0));
    }

    [Test]
    public void PipOnBothAtEofPreservesPausedContextPauseState()
    {
        // User paused one half (Primary) while waiting for the longer half (Secondary) to finish. Lockstep advance must NOT unpause the half the user deliberately paused. Playback.LoadFile always issues pause=no on the dispatcher; the coordinator re-applies pause=true via SetPaused after AdvanceAndLoadIfPossible so the paused context lands paused on the next track.
        using var h = new Harness();
        h.Vm.LoadPaths(new[] { "/a1.mp4", "/a2.mp4" }, replace: true);
        h.EnablePip();
        h.Vm.Secondary!.LoadPaths(new[] { "/b1.mp4", "/b2.mp4" }, replace: true);

        // Both contexts running, then user pauses primary.
        h.PrimaryPlayback.IsPaused = false;
        h.SecondaryPlayback!.IsPaused = false;
        h.PrimaryPlayback.IsPaused = true;

        Harness.ReachFileEof(h.Vm.Primary, h.PrimaryPlayback);
        Harness.ReachFileEof(h.Vm.Secondary!, h.SecondaryPlayback!);

        // Both advanced.
        Assert.That(h.Vm.Primary.Playlist.CurrentIndex, Is.EqualTo(1));
        Assert.That(h.Vm.Secondary!.Playlist.CurrentIndex, Is.EqualTo(1));

        // Primary was paused → exactly one SetPaused(true) recorded, and it lands AFTER both LoadFile calls completed (synchronous main-thread path; VM doesn't issue any SetPaused before the advance, only after). An incorrectly-ordered fix that re-paused before LoadFile would still pass the membership check, so we pin the exact log shape.
        Assert.That(h.PrimaryPlayback.SetPausedCalls, Is.EqualTo(new[] { true }));
        // Secondary was running → coordinator must not touch it at all. LoadFile's pause=no in real Playback carries through unmodified.
        Assert.That(h.SecondaryPlayback!.SetPausedCalls, Is.Empty);
    }

    [Test]
    public void NoSelectionTransportFansOutToBoth()
    {
        using var h = new Harness();
        h.EnablePip();
        Assert.That(h.Vm.SelectedSlot, Is.Null, "default after EnablePip is broadcast/sync");

        // Both contexts need a duration > 0 for SeekTo to compute a normalized time.
        h.PrimaryPlayback.DurationSeconds = 60;
        h.SecondaryPlayback!.DurationSeconds = 60;

        // Sync-mode SeekTo: Primary takes the absolute target derived from the normalized scrubber value; Secondary takes the same absolute-seconds delta off its own current position via SeekRelative. With both at position 0 and equal 60s durations, a 0.5 click puts Primary at 30 and shifts Secondary by +30. The asymmetric-durations / non-zero-positions test below pins the actual offset-preserving behavior.
        h.Vm.SeekTo(0.5);
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 30.0 }));
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls, Is.EqualTo(new[] { 30.0 }));

        h.Vm.SeekRelative(5);
        Assert.That(h.PrimaryPlayback.SeekRelativeCalls, Is.EqualTo(new[] { 5.0 }));
        // SeekRelative appends to Secondary's relative-seek log alongside the prior sync-mode SeekTo call (which lands here too, see above).
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls, Is.EqualTo(new[] { 30.0, 5.0 }));

        h.Vm.StepFrameForward();
        h.Vm.StepFrameBack();
        Assert.That(h.PrimaryPlayback.StepFrameForwardCalls, Is.EqualTo(1));
        Assert.That(h.PrimaryPlayback.StepFrameBackCalls, Is.EqualTo(1));
        Assert.That(h.SecondaryPlayback!.StepFrameForwardCalls, Is.EqualTo(1));
        Assert.That(h.SecondaryPlayback!.StepFrameBackCalls, Is.EqualTo(1));

        h.Vm.StepChapter(1);
        Assert.That(h.PrimaryPlayback.StepChapterCalls, Is.EqualTo(new[] { 1 }));
        Assert.That(h.SecondaryPlayback!.StepChapterCalls, Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public void SyncSeekToAppliesAbsoluteDeltaToSecondary()
    {
        // The whole point of switching SeekTo from proportional-of-own-duration to absolute-delta: when the videos have different durations and/or different starting offsets, a scrubber click that takes Primary "back N seconds" must take Secondary back the same N seconds — not "back N% of its own duration".
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 600;   // 10:00
        h.SecondaryPlayback!.DurationSeconds = 900; // 15:00, asymmetric on purpose
        h.PrimaryPlayback.PositionSeconds = 480;   // Primary at 8:00
        h.SecondaryPlayback!.PositionSeconds = 700; // Secondary at 11:40 (offset +220s from Primary)

        // Click takes Primary from 8:00 (480s) back to 5:00 (300s) — a -180s delta. Secondary must move by -180s as well, NOT to 0.5 * 900 = 450s.
        h.Vm.SeekTo(0.5);
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 300.0 }));
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty, "Secondary must not receive an absolute seek to a normalized-of-own-duration target");
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls, Is.EqualTo(new[] { -180.0 }));
    }

    [Test]
    public void SyncSeekToBurstAnchorsAcrossRapidTicks()
    {
        // Scrubber drag (and scroll-wheel spin, and rapid hotkey presses) emit SeekTo faster than mpv echoes time-pos back into Primary.Position. Without anchoring, each in-burst SeekTo would compute deltaSeconds against the same stale Primary.Position and Secondary would cumulatively overshoot. The implicit-burst anchor uses the *previous* commanded Primary target as the anchor when the next SeekTo lands within SyncSeekImplicitBurstTicks (250 ms) — the test runs in microseconds, so all four SeekTos fall inside the window.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 100;
        h.SecondaryPlayback!.DurationSeconds = 100;
        h.PrimaryPlayback.PositionSeconds = 0;
        h.SecondaryPlayback!.PositionSeconds = 0;

        // Four rapid SeekTos with no Position echo in between. If the VM read Primary.Position fresh on every call, deltas would be {30, 50, 70, 40} = 190 cumulative — wrong. Correct delta-of-deltas given the anchor mechanism: first uses Position=0 (anchor=null at start), then each uses the previous commanded target as the anchor.
        h.Vm.SeekTo(0.3);  // anchor=0 (Position),     delta=+30, anchor→30
        h.Vm.SeekTo(0.5);  // anchor=30 (last target), delta=+20, anchor→50
        h.Vm.SeekTo(0.7);  // anchor=50,               delta=+20, anchor→70
        h.Vm.SeekTo(0.4);  // anchor=70,               delta=-30, anchor→40

        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 30.0, 50.0, 70.0, 40.0 }));
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls, Is.EqualTo(new[] { 30.0, 20.0, 20.0, -30.0 }));
    }

    [Test]
    public void SyncSeekToAnchorExpiresAfterBurstWindow()
    {
        // Test the OTHER half of the implicit-burst design: when SeekTos straddle the SyncSeekImplicitBurstTicks window, the second one should re-anchor against Primary.Position rather than reuse the previous commanded target. We drive ViewModelMain.NowProvider explicitly so the expiration is deterministic and not flaky on slow CI.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 100;
        h.SecondaryPlayback!.DurationSeconds = 100;

        long now = 0;
        h.Vm.NowProvider = () => now;

        // First SeekTo at t=0 — anchor=Primary.Position=0, target=50.
        h.PrimaryPlayback.PositionSeconds = 0;
        h.Vm.SeekTo(0.5);
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 50.0 }));
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls, Is.EqualTo(new[] { 50.0 }));

        // Advance the clock past the burst threshold AND simulate that mpv echoed Primary's position to the commanded target. The next SeekTo must read Primary.Position fresh (=50) rather than reuse the stored anchor (also 50 — coincidentally the same here, so use a different new target to make the expectation visible).
        now += (long)(Stopwatch.Frequency * 0.5);   // 500 ms — well past the 250 ms threshold
        h.PrimaryPlayback.PositionSeconds = 50;
        h.Vm.SeekTo(0.7);
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 50.0, 70.0 }));
        // delta = 70 - Primary.Position(=50) = 20. Same value the burst-anchor path would have given (50→70=20), but this value is computed from Position, not from the stored anchor — verify that by next testing the case where Position has DRIFTED past the anchor (natural playback).
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls, Is.EqualTo(new[] { 50.0, 20.0 }));

        // Long pause: Primary plays naturally to 95s. Threshold expired. Next SeekTo back to 30 must compute delta = 30 - 95 = -65, NOT delta = 30 - 70 = -40 (stale anchor).
        now += (long)(Stopwatch.Frequency * 10.0);
        h.PrimaryPlayback.PositionSeconds = 95;
        h.Vm.SeekTo(0.3);
        Assert.That(h.PrimaryPlayback.SeekCalls.Count, Is.EqualTo(3));
        Assert.That(h.PrimaryPlayback.SeekCalls[2], Is.EqualTo(30.0));
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls.Count, Is.EqualTo(3));
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls[2], Is.EqualTo(-65.0), "expired anchor → re-read Primary.Position = 95");
    }

    [Test]
    public void SyncStepChapterFromBeforeFirstChapter()
    {
        // Primary's position is BEFORE chapter 0's time (rare — chapters that don't start at 0). The general targetIndex math handles this: currentIndex = -1, delta = +1 → targetIndex = 0, target = chapters[0].TimeSeconds.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 300;
        h.SecondaryPlayback!.DurationSeconds = 300;
        h.PrimaryPlayback.Chapters = new[]
        {
            new MediaChapter(0, "Act 1", 30),  // chapter 0 starts at 30s, not 0
            new MediaChapter(1, "Act 2", 120),
        };
        h.PrimaryPlayback.PositionSeconds = 10;  // before chapter 0

        h.Vm.StepChapter(1);   // mpv: lands at chapter 0 (time 30). Delta = 30 - 10 = 20.
        Assert.That(h.PrimaryPlayback.StepChapterCalls, Is.EqualTo(new[] { 1 }));
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls, Is.EqualTo(new[] { 20.0 }));
        Assert.That(h.SecondaryPlayback!.StepChapterCalls, Is.Empty, "absolute-delta path, not fan-out");

        // Step back from before chapter 0: targetIndex = -1 + (-1) = -2 → out of range → fan out (no chapter exists before chapter 0 to seek to).
        h.Vm.StepChapter(-1);
        Assert.That(h.PrimaryPlayback.StepChapterCalls, Is.EqualTo(new[] { 1, -1 }));
        Assert.That(h.SecondaryPlayback!.StepChapterCalls, Is.EqualTo(new[] { -1 }));
    }

    [Test]
    public void SyncSeekToAnchorClearsOnFileLoad()
    {
        // File reload resets Primary.Position to 0 and resets the user's "established offset" between the two streams entirely — an implicit-burst anchor commanded against the previous file would mis-anchor the first SeekTo against the new file. Verify FileLoaded clears the anchor so the next SeekTo reads Primary.Position fresh.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 100;
        h.SecondaryPlayback!.DurationSeconds = 100;

        // Establish an anchor at 50.
        h.Vm.SeekTo(0.5);
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 50.0 }));
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls, Is.EqualTo(new[] { 50.0 }));

        // New file loads on Primary; anchor invalidates. New file is shorter for visibility.
        h.PrimaryPlayback.PositionSeconds = 10;  // pretend the new file's position-0 was followed by a quick natural-playback tick to 10
        h.PrimaryPlayback.RaiseFileLoaded();

        // Next SeekTo (still within the 250 ms timestamp window) must read Primary.Position=10 fresh, not use the stale anchor=50.
        h.Vm.SeekTo(0.3);
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 50.0, 30.0 }));
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls.Count, Is.EqualTo(2));
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls[1], Is.EqualTo(30.0 - 10.0), "FileLoaded must clear the anchor — delta is computed from fresh Primary.Position");
    }

    [Test]
    public void SyncStepChapterAppliesAbsoluteDeltaToSecondary()
    {
        // Per-context StepChapter would advance each video to its own next chapter — chapter timestamps differ wildly between videos (one per scene vs. one per act, etc.) so the streams drift apart. Rule: derive Primary's chapter target seconds-delta and apply that delta to Secondary as a relative seek.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 300;
        h.SecondaryPlayback!.DurationSeconds = 300;
        h.PrimaryPlayback.Chapters = new[]
        {
            new MediaChapter(0, "Intro", 0),
            new MediaChapter(1, "Act 1", 60),
            new MediaChapter(2, "Act 2", 180),
        };
        h.PrimaryPlayback.PositionSeconds = 30;   // currently in Intro (chapter 0)
        h.SecondaryPlayback!.PositionSeconds = 95; // arbitrary unrelated offset

        // Step +1 → Primary's target is chapter 1 at 60s; delta from Primary.Position (30) is +30. Secondary moves by +30, NOT to its own chapter 1.
        h.Vm.StepChapter(1);
        Assert.That(h.PrimaryPlayback.StepChapterCalls, Is.EqualTo(new[] { 1 }));
        Assert.That(h.SecondaryPlayback!.StepChapterCalls, Is.Empty, "Secondary takes a relative seek, not its own chapter step");
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls, Is.EqualTo(new[] { 30.0 }));
    }

    [Test]
    public void SyncStepChapterClampingFallsBackToFanOut()
    {
        // When Primary's chapter step would clamp past either end, mpv's `add chapter` re-seek behavior at clamp boundaries isn't worth reproducing VM-side (re-seek to current chapter start? stay put? version-dependent). Fall back to per-context StepChapter so both contexts mpv-clamp independently — matches the pre-fix behavior at boundaries.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 300;
        h.SecondaryPlayback!.DurationSeconds = 300;
        h.PrimaryPlayback.Chapters = new[]
        {
            new MediaChapter(0, "Intro", 0),
            new MediaChapter(1, "Outro", 200),
        };
        h.PrimaryPlayback.PositionSeconds = 250;  // currently in Outro (chapter 1, the last)

        h.Vm.StepChapter(1);   // would clamp past end
        Assert.That(h.PrimaryPlayback.StepChapterCalls, Is.EqualTo(new[] { 1 }));
        Assert.That(h.SecondaryPlayback!.StepChapterCalls, Is.EqualTo(new[] { 1 }), "clamping → fan out per-context");
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls, Is.Empty);
    }

    [Test]
    public void SyncStepFrameAppliesAbsoluteDeltaToSecondary()
    {
        // Per-context frame-step advances each video by 1/fps_video — when fps differs (Primary 24, Secondary 60), the videos drift relative to each other by ~25 ms per step. Rule: frame-step both (atomic-pause-and-step on each, no SetPaused/SeekRelative race window where Secondary could decode extra frames between two dispatcher commands), then apply a *correction* SeekRelative on Secondary equal to Primary.frameDuration - Secondary.frameDuration, so Secondary's net move equals Primary's frame duration regardless of fps mismatch.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 60;
        h.SecondaryPlayback!.DurationSeconds = 60;
        h.PrimaryPlayback.VideoFps = 24;
        h.SecondaryPlayback!.VideoFps = 60;

        h.Vm.StepFrameForward();
        Assert.That(h.PrimaryPlayback.StepFrameForwardCalls, Is.EqualTo(1));
        Assert.That(h.SecondaryPlayback!.StepFrameForwardCalls, Is.EqualTo(1), "Secondary frame-steps too — atomic pause-and-step on its end avoids the SetPaused/SeekRelative dispatcher race");
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls.Count, Is.EqualTo(1));
        // Correction = Primary.frame - Secondary.frame = 1/24 - 1/60. Net Secondary move = (its own 1/60 frame) + (1/24 - 1/60 correction) = 1/24 = Primary.frame. ✓
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls[0], Is.EqualTo(1.0 / 24 - 1.0 / 60).Within(1e-9));

        h.Vm.StepFrameBack();
        Assert.That(h.PrimaryPlayback.StepFrameBackCalls, Is.EqualTo(1));
        Assert.That(h.SecondaryPlayback!.StepFrameBackCalls, Is.EqualTo(1));
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls.Count, Is.EqualTo(2));
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls[1], Is.EqualTo(-1.0 / 24 - -1.0 / 60).Within(1e-9));
    }

    [Test]
    public void SyncStepFrameWithEqualFpsSkipsCorrection()
    {
        // When Primary and Secondary share fps, the correction is exactly 0 — emitting a no-op SeekRelative would still be safe but pollutes the dispatcher; verify we elide it.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 60;
        h.SecondaryPlayback!.DurationSeconds = 60;
        h.PrimaryPlayback.VideoFps = 30;
        h.SecondaryPlayback!.VideoFps = 30;

        h.Vm.StepFrameForward();
        Assert.That(h.PrimaryPlayback.StepFrameForwardCalls, Is.EqualTo(1));
        Assert.That(h.SecondaryPlayback!.StepFrameForwardCalls, Is.EqualTo(1));
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls, Is.Empty);
    }

    [Test]
    public void SyncStepFrameWithoutPrimaryFpsFallsBackToFanOut()
    {
        // When Primary has no fps (audio-only / pre-load / VFR with no container-fps), the absolute-seconds delta of one frame is unknown. Fall back to per-context StepFrame — drift is then bounded to ~one frame per step, matching pre-fix behavior. Same fallback exists in production for sources that don't report container-fps.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 60;
        h.SecondaryPlayback!.DurationSeconds = 60;
        // Both fps null by default — leaving as-is.

        h.Vm.StepFrameForward();
        Assert.That(h.PrimaryPlayback.StepFrameForwardCalls, Is.EqualTo(1));
        Assert.That(h.SecondaryPlayback!.StepFrameForwardCalls, Is.EqualTo(1), "no fps → fan-out");
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SetPausedCalls, Is.Empty);
    }

    [Test]
    public void SelectedSecondaryRoutesTransportToSecondaryOnly()
    {
        using var h = new Harness();
        h.EnablePip();
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        Assert.That(h.Vm.SelectedSlot, Is.EqualTo(ViewModelMain.VideoSlot.Secondary));

        h.PrimaryPlayback.DurationSeconds = 60;
        h.SecondaryPlayback!.DurationSeconds = 60;

        h.Vm.SeekTo(0.25);
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 15.0 }));

        h.Vm.StepFrameForward();
        Assert.That(h.PrimaryPlayback.StepFrameForwardCalls, Is.EqualTo(0));
        Assert.That(h.SecondaryPlayback!.StepFrameForwardCalls, Is.EqualTo(1));
    }

    [Test]
    public void SelectedSecondaryRoutesPlayPauseToSecondaryOnly()
    {
        // PlayPause with a selection is isolated: only the selected stream toggles. Primary's pause state is untouched. This is the "cue PiP without disturbing primary" workflow.
        using var h = new Harness();
        h.EnablePip();
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.PrimaryPlayback.DurationSeconds = 60;
        h.SecondaryPlayback!.DurationSeconds = 60;

        h.Vm.PlayPauseCommand.Execute(null);
        Assert.That(h.PrimaryPlayback.TogglePauseCalls, Is.EqualTo(0), "primary not toggled");
        Assert.That(h.PrimaryPlayback.SetPausedCalls, Is.Empty, "primary not set");
        Assert.That(h.SecondaryPlayback!.TogglePauseCalls, Is.EqualTo(1), "secondary toggled");
    }

    [Test]
    public void NoSelectionPlayPauseSyncsBothFromPrimaryFlip()
    {
        // When no selection and both already in sync: PlayPause sync-broadcasts SetPaused(target) on every stream, target = !Primary.IsPaused. Both streams converge to one state.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 60;
        h.SecondaryPlayback!.DurationSeconds = 60;
        // Both start IsPaused=true (default). Primary's flip → target=false (play).
        h.Vm.PlayPauseCommand.Execute(null);
        Assert.That(h.PrimaryPlayback.SetPausedCalls, Is.EqualTo(new[] { false }));
        Assert.That(h.SecondaryPlayback!.SetPausedCalls, Is.EqualTo(new[] { false }));
        Assert.That(h.PrimaryPlayback.IsPaused, Is.False);
        Assert.That(h.SecondaryPlayback!.IsPaused, Is.False);

        // Hit again: Primary flips to true, both converge to true.
        h.Vm.PlayPauseCommand.Execute(null);
        Assert.That(h.PrimaryPlayback.SetPausedCalls, Is.EqualTo(new[] { false, true }));
        Assert.That(h.SecondaryPlayback!.SetPausedCalls, Is.EqualTo(new[] { false, true }));
    }

    [Test]
    public void NoSelectionPlayPauseConvergesDriftedStreams()
    {
        // Drift scenario: user previously had Secondary selected and toggled it independently, leaving Primary playing + Secondary paused (or vice versa). Then they deselect and hit Space — both must converge to the same state. Tiebreaker: Primary's flip drives, so all streams end at NOT(primary's previous state).
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 60;
        h.SecondaryPlayback!.DurationSeconds = 60;
        h.PrimaryPlayback.IsPaused = false;       // primary playing
        h.SecondaryPlayback!.IsPaused = true;     // secondary paused — drift
        // No selection (broadcast/sync). PlayPause: target = !Primary.IsPaused = true → both end paused.
        h.Vm.PlayPauseCommand.Execute(null);
        Assert.That(h.PrimaryPlayback.SetPausedCalls, Is.EqualTo(new[] { true }));
        Assert.That(h.SecondaryPlayback!.SetPausedCalls, Is.EqualTo(new[] { true }));
        Assert.That(h.PrimaryPlayback.IsPaused, Is.True);
        Assert.That(h.SecondaryPlayback!.IsPaused, Is.True);

        // Inverse drift: Primary paused, Secondary playing. PlayPause → target=false → both play.
        h.PrimaryPlayback.SetPausedCalls.Clear();
        h.SecondaryPlayback!.SetPausedCalls.Clear();
        h.PrimaryPlayback.IsPaused = true;
        h.SecondaryPlayback!.IsPaused = false;
        h.Vm.PlayPauseCommand.Execute(null);
        Assert.That(h.PrimaryPlayback.SetPausedCalls, Is.EqualTo(new[] { false }));
        Assert.That(h.SecondaryPlayback!.SetPausedCalls, Is.EqualTo(new[] { false }));
    }

    [Test]
    public void PerVideoCommandsTargetSingleTarget()
    {
        // Volume / Mute always target SingleTarget (Selected ?? Primary), regardless of selection-vs-broadcast state.
        using var h = new Harness();
        h.EnablePip();
        Assert.That(h.Vm.SelectedSlot, Is.Null);

        h.Vm.SetVolume(50);
        Assert.That(h.PrimaryPlayback.SetVolumeCalls, Is.EqualTo(new[] { 50.0 }));
        Assert.That(h.SecondaryPlayback!.SetVolumeCalls, Is.Empty);

        h.Vm.ToggleMute();
        Assert.That(h.PrimaryPlayback.ToggleMuteCalls, Is.EqualTo(1));
        Assert.That(h.SecondaryPlayback!.ToggleMuteCalls, Is.EqualTo(0));

        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.Vm.SetVolume(25);
        Assert.That(h.PrimaryPlayback.SetVolumeCalls, Is.EqualTo(new[] { 50.0 }), "primary unaffected after selection swap");
        Assert.That(h.SecondaryPlayback!.SetVolumeCalls, Is.EqualTo(new[] { 25.0 }));

        h.Vm.ToggleMute();
        Assert.That(h.PrimaryPlayback.ToggleMuteCalls, Is.EqualTo(1));
        Assert.That(h.SecondaryPlayback!.ToggleMuteCalls, Is.EqualTo(1));
    }

    [Test]
    public void OpenFileTargetsSingleTarget()
    {
        using var h = new Harness();
        h.EnablePip();
        h.Vm.OpenFile("/primary-only.mp4");
        Assert.That(h.PrimaryPlayback.LoadedFiles, Is.EqualTo(new[] { "/primary-only.mp4" }));
        Assert.That(h.SecondaryPlayback!.LoadedFiles, Is.Empty);

        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.Vm.OpenFile("/secondary-only.mp4");
        Assert.That(h.PrimaryPlayback.LoadedFiles, Is.EqualTo(new[] { "/primary-only.mp4" }));
        Assert.That(h.SecondaryPlayback!.LoadedFiles, Is.EqualTo(new[] { "/secondary-only.mp4" }));
    }

    [Test]
    public void DisablePipDisposesSecondaryAndResumesPrimaryAutoAdvance()
    {
        using var h = new Harness();
        h.Vm.LoadPaths(new[] { "/a1.mp4", "/a2.mp4" }, replace: true);
        h.EnablePip();
        Assert.That(h.Vm.Primary.AutoAdvanceEnabled, Is.False, "primary auto-advance suppressed during PiP");

        h.Vm.DisablePip();
        Assert.That(h.Vm.Secondary, Is.Null);
        Assert.That(h.Vm.IsPipEnabled, Is.False);
        Assert.That(h.Vm.Primary.AutoAdvanceEnabled, Is.True, "primary auto-advance restored");

        // Primary's auto-advance must work again — drive it to file EOF and watch the per-context handler advance.
        Harness.ReachFileEof(h.Vm.Primary, h.PrimaryPlayback);
        Assert.That(h.Vm.Primary.Playlist.CurrentIndex, Is.EqualTo(1));
    }

    [Test]
    public void EnableDisableEnableCyclePreservesNoState()
    {
        using var h = new Harness();
        h.EnablePip();
        h.Vm.Secondary!.LoadPaths(new[] { "/old.mp4" }, replace: true);
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);

        h.Vm.DisablePip();
        Assert.That(h.Vm.SelectedSlot, Is.Null, "DisablePip resets selection to broadcast/sync");

        // Re-enable: must be a fresh secondary (different instance, empty playlist, no preserved selection state).
        h.EnablePip();
        Assert.That(h.Vm.Secondary!.Playlist.Items, Is.Empty, "fresh secondary has empty playlist");
        Assert.That(h.Vm.SelectedSlot, Is.Null, "selection stays null on re-enable");
    }

    [Test]
    public void SetSelectedSecondaryWhileSecondaryNullSnapsToNull()
    {
        using var h = new Harness();
        // PiP off — Secondary doesn't exist; SetSelected(Secondary) coerces to null.
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        Assert.That(h.Vm.SelectedSlot, Is.Null);
    }

    [Test]
    public void SelectionSwapRefiresProxyPropertiesSoViewReadsTarget()
    {
        using var h = new Harness();
        h.PrimaryPlayback.Volume = 80;
        h.EnablePip();
        h.SecondaryPlayback!.Volume = 50;
        // Capture VM-level PropertyChanged for Volume and the read-back via the proxy property.
        var observedVolumes = new List<double>();
        h.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModelMain.Volume))
            {
                observedVolumes.Add(h.Vm.Volume);
            }
        };

        // Selection swap to Secondary should fire Volume PropertyChanged so the view re-reads — and reads Secondary's 50, not Primary's 80.
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        Assert.That(observedVolumes, Does.Contain(50.0), "view received Secondary's volume on selection swap");
        Assert.That(h.Vm.Volume, Is.EqualTo(50.0), "proxy reads from selected target");

        // Swap back to null (broadcast/sync, default target = Primary): view must see Primary's value again.
        observedVolumes.Clear();
        h.Vm.SetSelected(null);
        Assert.That(observedVolumes, Does.Contain(80.0));
        Assert.That(h.Vm.Volume, Is.EqualTo(80.0));
    }

    [Test]
    public void OffTargetContextChangesAreNotForwardedToView()
    {
        // Volume change on Secondary while no selection (target=Primary) must NOT fire VM PropertyChanged for Volume — the view shouldn't see the off-target context's churn.
        using var h = new Harness();
        h.EnablePip();
        // SelectedSlot=null ⇒ target=Primary.
        var observedVolumeChanges = 0;
        h.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModelMain.Volume))
            {
                observedVolumeChanges++;
            }
        };

        // Mutate Secondary's volume — view should not react.
        h.SecondaryPlayback!.Volume = 25;
        Assert.That(observedVolumeChanges, Is.EqualTo(0), "off-target volume change is filtered");
    }

    [Test]
    public void DisablePipWithPrimaryAtEofAdvancesPrimary()
    {
        // Scenario: in PiP mode the pair has been parked at file-EOF (lockstep). User toggles PiP off. Primary should immediately advance to its next item rather than getting stuck at EOF.
        using var h = new Harness();
        h.Vm.LoadPaths(new[] { "/a1.mp4", "/a2.mp4" }, replace: true);
        h.EnablePip();
        h.Vm.Secondary!.LoadPaths(new[] { "/b.mp4" }, replace: true);  // Secondary has no next.
        Harness.ReachFileEof(h.Vm.Primary, h.PrimaryPlayback);
        Harness.ReachFileEof(h.Vm.Secondary!, h.SecondaryPlayback!);
        // Lockstep halts the pair (Secondary at end) — Primary is parked at EOF.
        Assert.That(h.Vm.Primary.Playlist.CurrentIndex, Is.EqualTo(0));

        h.Vm.DisablePip();
        Assert.That(h.Vm.Primary.Playlist.CurrentIndex, Is.EqualTo(1), "Primary advances on PiP-off when at EOF + has next");
        Assert.That(h.PrimaryPlayback.LoadedFiles, Is.EqualTo(new[] { "/a1.mp4", "/a2.mp4" }));
    }

    [Test]
    public void SelectionOnShorterPlaylistDoesNotChangeLockstepHaltDecision()
    {
        // Coordinator's lockstep gate looks at HasNextItem on both contexts independent of which is selected. Verify swapping selection between Primary (shorter) and Secondary (longer) doesn't change the outcome.
        using var h = new Harness();
        h.Vm.LoadPaths(new[] { "/a.mp4" }, replace: true);              // shorter
        h.EnablePip();
        h.Vm.Secondary!.LoadPaths(new[] { "/b1.mp4", "/b2.mp4" }, replace: true);  // longer

        // No selection. Both reach EOF → no advance.
        Harness.ReachFileEof(h.Vm.Primary, h.PrimaryPlayback);
        Harness.ReachFileEof(h.Vm.Secondary!, h.SecondaryPlayback!);
        Assert.That(h.Vm.Primary.Playlist.CurrentIndex, Is.EqualTo(0));
        Assert.That(h.Vm.Secondary!.Playlist.CurrentIndex, Is.EqualTo(0));

        // Swap selection; same outcome.
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        // Re-fire IsEofReached to re-trigger the lockstep gate.
        h.PrimaryPlayback.IsEofReached = false;
        h.PrimaryPlayback.IsEofReached = true;
        Assert.That(h.Vm.Primary.Playlist.CurrentIndex, Is.EqualTo(0));
    }
}
