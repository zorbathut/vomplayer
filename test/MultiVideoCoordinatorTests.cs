using System;
using System.Collections.Generic;
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
        private bool isCoreIdle;

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
        private double? estimatedVfFps;

        [ObservableProperty]
        private string? mediaTitle;

        public event Action? FileLoaded;
        public event Action? TracksReloaded;
        public event Action<bool>? SourceHdrChanged;
        public event Action<bool>? IsSourceFpsTrustedChanged;

        public bool IsSourceHdr { get; set; }
        public string? HwdecCurrent { get; set; }
        public IReadOnlyList<string> HwdecTranscript { get; set; } = Array.Empty<string>();

        public bool IsSourceFpsTrusted { get; set; } = true;
        public string FpsTrustReason { get; set; } = "";

        public List<string> LoadedFiles { get; } = new();
        public int TogglePauseCalls { get; private set; }
        public List<bool> SetPausedCalls { get; } = new();
        public List<double> SeekCalls { get; } = new();
        public List<double> SeekRelativeCalls { get; } = new();
        public int StepFrameForwardCalls { get; private set; }
        public int StepFrameBackCalls { get; private set; }
        public List<double> SetVolumeCalls { get; } = new();
        public List<double> SetSpeedCalls { get; } = new();
        public int ToggleMuteCalls { get; private set; }
        public int EnableHdrOutputCalls { get; private set; }
        public int DisableHdrOutputCalls { get; private set; }
        public List<double> SetFrameMultiplierCalls { get; } = new();
        public int ClearFrameMultiplierCalls { get; private set; }

        public void Initialize() { }
        public void LoadFile(string path, bool startPaused) { LoadedFiles.Add(path); }
        public void TogglePause() { TogglePauseCalls++; IsPaused = !IsPaused; }
        public void SetPaused(bool paused) { SetPausedCalls.Add(paused); IsPaused = paused; }
        public void Seek(double seconds) { SeekCalls.Add(seconds); }
        public void SeekRelative(double seconds) { SeekRelativeCalls.Add(seconds); }
        public void StepFrameForward() { StepFrameForwardCalls++; }
        public void StepFrameBack() { StepFrameBackCalls++; }
        public void LoadAudio(string path) { }
        public void LoadSubtitle(string path) { }
        public void SetVideo(int? trackId) { }
        public void SetAudio(int? trackId) { }
        public void SetSubtitle(int? trackId) { }
        public void SetVolume(double percent) { SetVolumeCalls.Add(percent); Volume = percent; }
        public void SetSpeed(double rate) { SetSpeedCalls.Add(rate); }
        public void AdjustVolume(double deltaPercent) { }
        public void ToggleMute() { ToggleMuteCalls++; IsMuted = !IsMuted; }
        public void EnableHdrOutput() { EnableHdrOutputCalls++; }
        public void DisableHdrOutput() { DisableHdrOutputCalls++; }
        public void SetFrameMultiplier(double outputFps) { SetFrameMultiplierCalls.Add(outputFps); }
        public void ClearFrameMultiplier() { ClearFrameMultiplierCalls++; }

        public void RaiseFileLoaded() { FileLoaded?.Invoke(); }
        public void RaiseTracksReloaded() { TracksReloaded?.Invoke(); }
        public void RaiseSourceHdrChanged(bool isHdr) { IsSourceHdr = isHdr; SourceHdrChanged?.Invoke(isHdr); }
        public void RaiseIsSourceFpsTrustedChanged(bool trusted) { IsSourceFpsTrusted = trusted; IsSourceFpsTrustedChanged?.Invoke(trusted); }

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
        public Task<bool> IsAvailableAsync(CancellationToken ct) { return Task.FromResult(false); }
        public Task<IReadOnlyList<string>> ProbeAsync(string url, CancellationToken ct) { return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()); }
        public Task<UrlLoadKind> ClassifyAsync(string url, CancellationToken ct) { return Task.FromResult(UrlLoadKind.MpvDirect); }
        public Task<string> DownloadAsync(string url, IProgress<UrlDownloadProgress>? progress, CancellationToken ct) { return Task.FromResult(url); }
    }

    private sealed class StubUrlPrompt : IUrlPrompt
    {
        public Task<string?> PromptForUrlAsync(string title) { return Task.FromResult<string?>(null); }
        public void ShowError(string title, string message) { }
        public IUrlStatusHandle ShowUrlStatus(string statusText, Action? onCancel) { return new NoopStatus(); }
        private sealed class NoopStatus : IUrlStatusHandle
        {
            public IProgress<UrlDownloadProgress> Progress { get; } = new Progress<UrlDownloadProgress>(_ => { });
            public void Dispose() { }
        }
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
    public void NextTrackInSyncModeActsOnPrimaryOnly()
    {
        // Prev/Next track is a per-video command (like PlayPlaylistItem / Open*), NOT a transport command — so in sync mode (PiP on, no selection) it targets Primary only and does NOT fan out to Secondary.
        using var h = new Harness();
        h.Vm.LoadPaths(new[] { "/a1.mp4", "/a2.mp4" }, replace: true);
        h.EnablePip();
        h.Vm.Secondary!.LoadPaths(new[] { "/b1.mp4", "/b2.mp4" }, replace: true);
        Assert.That(h.Vm.SelectedSlot, Is.Null, "broadcast/sync mode");

        h.Vm.NextTrack();

        Assert.That(h.Vm.Primary.Playlist.CurrentIndex, Is.EqualTo(1), "primary advanced");
        Assert.That(h.Vm.Secondary!.Playlist.CurrentIndex, Is.EqualTo(0), "secondary untouched");
        Assert.That(h.PrimaryPlayback.LoadedFiles, Is.EqualTo(new[] { "/a1.mp4", "/a2.mp4" }));
        Assert.That(h.SecondaryPlayback!.LoadedFiles, Is.EqualTo(new[] { "/b1.mp4" }), "secondary did not load a new file");
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
        // User paused one half (Primary) while waiting for the longer half (Secondary) to finish. Lockstep advance must NOT unpause the half the user deliberately paused. AdvanceAndLoadIfPossible calls Playback.LoadFile with startPaused=false (auto-advance treats the next file as "keep playing"); the coordinator captures the pre-advance pause state and re-applies pause=true via SetPaused after the advance so the deliberately-paused context lands paused on the next track.
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
        // Secondary was running → coordinator must not touch it at all. LoadFile(startPaused=false) issues pause=no on the real Playback dispatcher, which carries through unmodified.
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

        // Sync-mode SeekTo: Primary takes the absolute target from the normalized scrubber value; Secondary is absolute-pinned to primaryTarget + offset. EnablePip established offset = 0, so a 0.5 click puts both at 30. The asymmetric-offset test below pins the offset-preserving behavior.
        h.Vm.SeekTo(0.5);
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 30.0 }));
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 30.0 }));

        // SeekRelative stays a relative fan-out (offset-preserving by construction) — both move by the same delta.
        h.Vm.SeekRelative(5);
        Assert.That(h.PrimaryPlayback.SeekRelativeCalls, Is.EqualTo(new[] { 5.0 }));
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls, Is.EqualTo(new[] { 5.0 }));

        h.Vm.StepFrameForward();
        h.Vm.StepFrameBack();
        Assert.That(h.PrimaryPlayback.StepFrameForwardCalls, Is.EqualTo(1));
        Assert.That(h.PrimaryPlayback.StepFrameBackCalls, Is.EqualTo(1));
        Assert.That(h.SecondaryPlayback!.StepFrameForwardCalls, Is.EqualTo(1));
        Assert.That(h.SecondaryPlayback!.StepFrameBackCalls, Is.EqualTo(1));

        // StepChapter fans out too: Primary takes an absolute seek to the chapter target (cue, preroll 0), Secondary is absolute-pinned to target + offset. Primary at position 0 with a chapter at 45s and offset 0 → both seek to 45.
        h.PrimaryPlayback.Chapters = new[] { new MediaChapter(0, "a", 0), new MediaChapter(1, "b", 45) };
        h.Vm.StepChapter(1);
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 30.0, 45.0 }));
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 30.0, 45.0 }));
    }

    [Test]
    public void SyncSeekToAppliesAbsoluteDeltaToSecondary()
    {
        // When the videos have different durations and/or a non-zero established offset, a scrubber click that takes Primary "back N seconds" must take Secondary back the same N seconds — not "back N% of its own duration". The new model pins Secondary to the absolute target primaryTarget + offset, which is offset-preserving by construction.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 600;   // 10:00
        h.SecondaryPlayback!.DurationSeconds = 900; // 15:00, asymmetric on purpose
        h.PrimaryPlayback.PositionSeconds = 480;   // Primary at 8:00
        h.SecondaryPlayback!.PositionSeconds = 700; // Secondary at 11:40

        // Establish offset = +220 (Secondary 220s ahead) via the authorized select round-trip.
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.Vm.SetSelected(null);  // captures offset = 700 - 480 = 220

        // Click takes Primary from 8:00 (480s) to 5:00 (300s). Secondary is absolute-pinned to 300 + 220 = 520 — which is 700 - 180, i.e. the same -180s net move Primary made, NOT 0.5 * 900 = 450.
        h.Vm.SeekTo(0.5);
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 300.0 }));
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 520.0 }), "Secondary absolute-pinned to primaryTarget + offset");
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls, Is.Empty, "Secondary is absolute-pinned, not relatively moved");
    }

    [Test]
    public void SyncStepChapterFromBeforeFirstChapter()
    {
        // Primary's position is BEFORE chapter 0's time (rare — chapters that don't start at 0). ChapterStep handles it: current = -1, +1 → chapter 0's landing (its cue, preroll 0). Primary seeks there; Secondary mirrors the delta.
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

        h.Vm.StepChapter(1);   // lands at chapter 0 (time 30). Secondary absolute-pinned to 30 + offset(0) = 30.
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 30.0 }));
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 30.0 }));

        // Step back while still before chapter 0 (position unchanged at 10): nothing earlier exists → true no-op, no seeks added.
        h.Vm.StepChapter(-1);
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 30.0 }), "previous before the first chapter is a no-op");
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 30.0 }));
    }

    [Test]
    public void SyncSeekToBaselinesOffsetOnFirstUseAfterLoad()
    {
        // A file load clears the offset to "pending" (null); the first sync transport that needs it baselines ONCE from the current divergence, then locks it. Here Primary loads a new file (offset cleared) and the streams sit at a +12s divergence; the first SeekTo must adopt offset=12 and absolute-pin Secondary to primaryTarget + 12 — never SeekRelative, never re-read on later seeks.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 100;
        h.SecondaryPlayback!.DurationSeconds = 100;

        // FileLoaded clears the offset to pending.
        h.PrimaryPlayback.RaiseFileLoaded();
        // Streams diverge by +12 at the moment of the first seek.
        h.PrimaryPlayback.PositionSeconds = 8;
        h.SecondaryPlayback!.PositionSeconds = 20;  // offset to be baselined = 20 - 8 = 12

        h.Vm.SeekTo(0.5);  // primaryTarget = 50; Secondary absolute = 50 + 12 = 62
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 50.0 }));
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 62.0 }));
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls, Is.Empty, "Secondary must be absolute-pinned, not relatively moved");

        // Offset is now locked at 12: a second seek pins against the SAME offset, independent of the (unechoed) positions.
        h.Vm.SeekTo(0.3);  // primaryTarget = 30; Secondary absolute = 30 + 12 = 42
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 50.0, 30.0 }));
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 62.0, 42.0 }));
    }

    [Test]
    public void SyncTransportNeverChangesEstablishedOffset()
    {
        // The headline invariant: once an offset is established, NO sync-mode transport (SeekTo, SeekRelative, StepChapter, StepFrame) may change it. Only single-track selection does. We establish offset = 5 via a select round-trip, run every transport kind, then prove the offset survived by checking a follow-up correction still defends 5.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 200;
        h.SecondaryPlayback!.DurationSeconds = 200;
        h.PrimaryPlayback.VideoFps = 30;
        h.SecondaryPlayback!.VideoFps = 30;
        h.PrimaryPlayback.Chapters = new[] { new MediaChapter(0, "a", 0), new MediaChapter(1, "b", 80) };

        // Establish offset = 5 (Secondary 5s ahead) via the authorized select→deselect adjustment.
        h.PrimaryPlayback.PositionSeconds = 0;
        h.SecondaryPlayback!.PositionSeconds = 5;
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.Vm.SetSelected(null);  // captures offset = 5

        // Now simulate the streams drifting during playback so the LIVE divergence (12) differs from the STORED offset (5). If any seek re-derived the offset from live positions instead of reading the stored value, the assertions below would see primaryTarget + 12, not + 5 — this is what pins "stored, not re-derived".
        h.PrimaryPlayback.PositionSeconds = 40;
        h.SecondaryPlayback!.PositionSeconds = 52;  // live divergence = 12 ≠ stored offset 5

        // Every sync transport kind. Each absolute seek must pin Secondary to primaryTarget + 5 (the stored offset), never primaryTarget + 12 (the live divergence).
        h.Vm.SeekTo(0.25);  // primaryTarget = 50; Secondary = 55, NOT 62
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 55.0 }), "absolute pin uses the stored offset (5), not the live divergence (12)");
        h.Vm.SeekRelative(10);    // relative fan-out, offset-preserving
        h.Vm.StepChapter(1);      // chapter 1 at 80; Secondary = 85, NOT 92
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 55.0, 85.0 }), "chapter pin uses the stored offset (5), not the live divergence (12)");
        h.Vm.StepFrameForward();  // frame-step both, equal fps → no correction

        // The offset must still be 5. Introduce a > 1 s drift so the controller hard-resyncs and confirm it defends 5 (seek to Primary.Pos + 5), not some shifted value.
        h.PrimaryPlayback.IsPaused = false;
        h.SecondaryPlayback!.IsPaused = false;
        h.PrimaryPlayback.PositionSeconds = 100;
        h.SecondaryPlayback!.PositionSeconds = 106.5;  // drift = (106.5 - 100) - 5 = 1.5 > hard-resync threshold
        h.SecondaryPlayback!.SeekCalls.Clear();
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 105.0 }), "offset unchanged at 5 → hard resync targets Primary.Pos + 5 = 105");
    }

    [Test]
    public void SyncStepChapterAppliesAbsoluteDeltaToSecondary()
    {
        // Per-context StepChapter would advance each video to its own next chapter — chapter timestamps differ wildly between videos (one per scene vs. one per act, etc.) so the streams drift apart. Rule: seek Primary to its chapter target and absolute-pin Secondary to target + offset, so Secondary tracks Primary's move rather than its own chapter grid.
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
        h.SecondaryPlayback!.PositionSeconds = 95; // 65s ahead of Primary

        // Establish offset = +65 via the authorized select round-trip.
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.Vm.SetSelected(null);  // captures offset = 95 - 30 = 65

        // Step +1 → Primary's target is chapter 1 at 60s; Secondary is absolute-pinned to 60 + 65 = 125 (= 95 + Primary's +30 move), NOT to its own chapter 1.
        h.Vm.StepChapter(1);
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 60.0 }));
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 125.0 }), "Secondary absolute-pinned to target + offset, not its own chapter step");
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls, Is.Empty);
    }

    // SeekToChapter routes through SeekTo's normalized contract (divide-by-then-multiply-by the same duration), so a tiny FP tolerance is used; the real seek is F3-formatted at the mpv boundary anyway.

    [Test]
    public void SeekToChapterLandsOnCueWithDefaultPreroll()
    {
        using var h = new Harness();
        h.PrimaryPlayback.DurationSeconds = 300;
        h.PrimaryPlayback.Chapters = new[] { new MediaChapter(0, "a", 0), new MediaChapter(1, "b", 120) };
        h.Vm.SeekToChapter(120);
        Assert.That(h.PrimaryPlayback.SeekCalls.Count, Is.EqualTo(1));
        Assert.That(h.PrimaryPlayback.SeekCalls[0], Is.EqualTo(120.0).Within(1e-6));
    }

    [Test]
    public void SeekToChapterAppliesPreroll()
    {
        using var h = new Harness();
        h.Vm.ChapterSeekPrerollSeconds = 5;
        h.PrimaryPlayback.DurationSeconds = 300;
        h.PrimaryPlayback.Chapters = new[] { new MediaChapter(0, "a", 0), new MediaChapter(1, "b", 120) };
        h.Vm.SeekToChapter(120);   // 120 - 5 = 115 (previous-cue floor of 0 doesn't bind)
        Assert.That(h.PrimaryPlayback.SeekCalls[0], Is.EqualTo(115.0).Within(1e-6));
    }

    [Test]
    public void SeekToChapterClampsPrerollAtZeroForEarlyChapter()
    {
        using var h = new Harness();
        h.Vm.ChapterSeekPrerollSeconds = 10;
        h.PrimaryPlayback.DurationSeconds = 300;
        h.PrimaryPlayback.Chapters = new[] { new MediaChapter(0, "a", 5), new MediaChapter(1, "b", 120) };
        h.Vm.SeekToChapter(5);     // 5 - 10 < 0 → floored at 0
        Assert.That(h.PrimaryPlayback.SeekCalls[0], Is.EqualTo(0.0).Within(1e-6));
    }

    [Test]
    public void SeekToChapterInSyncModeAppliesPrerollAndFansOutToSecondary()
    {
        // Marker click in PiP sync mode routes through SeekTo: Primary takes the prerolled absolute target, Secondary is absolute-pinned to target + offset — same offset-preserving contract as any sync-mode absolute seek.
        using var h = new Harness();
        h.EnablePip();
        h.Vm.ChapterSeekPrerollSeconds = 5;
        h.PrimaryPlayback.DurationSeconds = 300;
        h.SecondaryPlayback!.DurationSeconds = 300;
        h.PrimaryPlayback.Chapters = new[] { new MediaChapter(0, "a", 0), new MediaChapter(1, "b", 120) };
        h.PrimaryPlayback.PositionSeconds = 20;
        h.SecondaryPlayback!.PositionSeconds = 0;  // 20s behind Primary

        // Establish offset = -20 via the authorized select round-trip.
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.Vm.SetSelected(null);  // captures offset = 0 - 20 = -20

        h.Vm.SeekToChapter(120);   // target 120 - 5 = 115; Secondary absolute-pinned to 115 + (-20) = 95
        Assert.That(h.PrimaryPlayback.SeekCalls.Count, Is.EqualTo(1));
        Assert.That(h.PrimaryPlayback.SeekCalls[0], Is.EqualTo(115.0).Within(1e-6));
        Assert.That(h.SecondaryPlayback!.SeekCalls.Count, Is.EqualTo(1));
        Assert.That(h.SecondaryPlayback!.SeekCalls[0], Is.EqualTo(95.0).Within(1e-6));
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls, Is.Empty);
    }

    [Test]
    public void StepChapterSingleVideoAppliesPreroll()
    {
        using var h = new Harness();
        h.Vm.ChapterSeekPrerollSeconds = 5;
        h.PrimaryPlayback.DurationSeconds = 300;
        h.PrimaryPlayback.Chapters = new[] { new MediaChapter(0, "a", 0), new MediaChapter(1, "b", 60), new MediaChapter(2, "c", 180) };
        h.PrimaryPlayback.PositionSeconds = 30;   // in chapter 0
        h.Vm.StepChapter(1);   // → chapter 1 at 60, preroll 5 → 55 (seeks the computed target directly, no round-trip)
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 55.0 }));
    }

    [Test]
    public void StepChapterAdvancesFromSubFrameLandingWhilePaused()
    {
        // Regression for the paused "forward-forward sticks" bug: mpv's exact seek to a cue lands on the frame at-or-just-below it, so the echoed position is a sub-frame below the target. A follow-up "next" must still advance rather than recompute (and re-cue) the same chapter. (While playing this never showed because playback advances past the cue first.)
        using var h = new Harness();
        h.PrimaryPlayback.DurationSeconds = 300;
        h.PrimaryPlayback.Chapters = new[] { new MediaChapter(0, "a", 0), new MediaChapter(1, "b", 60), new MediaChapter(2, "c", 120) };
        h.PrimaryPlayback.PositionSeconds = 10;
        h.Vm.StepChapter(1);                        // → 60
        h.PrimaryPlayback.PositionSeconds = 59.96;  // mpv's exact-seek landing, echoed back while paused
        h.Vm.StepChapter(1);                        // must advance to 120, not re-cue 60
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 60.0, 120.0 }));
    }

    [Test]
    public void SyncStepChapterAppliesPreroll()
    {
        using var h = new Harness();
        h.EnablePip();
        h.Vm.ChapterSeekPrerollSeconds = 5;
        h.PrimaryPlayback.DurationSeconds = 300;
        h.SecondaryPlayback!.DurationSeconds = 300;
        h.PrimaryPlayback.Chapters = new[] { new MediaChapter(0, "a", 0), new MediaChapter(1, "b", 60), new MediaChapter(2, "c", 180) };
        h.PrimaryPlayback.PositionSeconds = 30;   // in chapter 0 (offset 0 from EnablePip, both aligned)
        h.Vm.StepChapter(1);   // Primary target chapter 1 (60) - preroll 5 = 55; Secondary absolute-pinned to 55 + 0 = 55
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 55.0 }));
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 55.0 }));
    }

    [Test]
    public void SyncStepChapterPastLastChapterIsNoOp()
    {
        // Stepping past the last chapter has nowhere to go: ChapterStep returns null → true no-op. Neither context moves (no mpv-clamp fan-out), and the sync offset is left untouched.
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

        h.Vm.StepChapter(1);   // past the end
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
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
    public void PlayPauseConvergingDifferedStreamsSetsSyncPoint()
    {
        // Repro: user plays one track alone, switches to all-track mode, then hits Space to start all. The already-playing track drifted between the mode switch and the Space press, so the offset captured at deselect is now stale. Bringing the two streams to a common play state via Space only actually STARTS one of them (the other was already playing) — that's a fresh sync point: capture the offset from the live divergence.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 60;
        h.SecondaryPlayback!.DurationSeconds = 60;

        // A stale offset of 0 from an earlier select round-trip (both were at 0 then).
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.Vm.SetSelected(null);  // offset = 0

        // Streams now in DIFFERENT play states: Secondary has been playing and drifted to 40, Primary is paused at 10.
        h.PrimaryPlayback.IsPaused = true;
        h.PrimaryPlayback.PositionSeconds = 10;
        h.SecondaryPlayback!.IsPaused = false;
        h.SecondaryPlayback!.PositionSeconds = 40;

        h.Vm.PlayPauseCommand.Execute(null);  // target = !Primary.IsPaused = play; differed states → sync point

        // Both converge to playing.
        Assert.That(h.PrimaryPlayback.SetPausedCalls, Is.EqualTo(new[] { false }));
        Assert.That(h.SecondaryPlayback!.SetPausedCalls, Is.EqualTo(new[] { false }));

        // The sync point is now the live divergence (40 - 10 = 30), not the stale 0. Prove it: a > 1 s drift hard-resyncs against offset 30.
        h.PrimaryPlayback.PositionSeconds = 20;
        h.SecondaryPlayback!.PositionSeconds = 51.5;  // divergence 31.5 vs offset 30 → drift 1.5 > hard-resync threshold
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 50.0 }), "fresh sync point = 30 → hard resync targets Primary.Pos + 30 = 50");
    }

    [Test]
    public void PlayPauseStartingBothFromSameStateDefendsExistingOffset()
    {
        // Counterpart to the differed-state test: when both streams start in the SAME play state (both paused) and Space starts them together, that's a genuine both-track action — it must NOT recapture the offset (which would bake in prior drift). The continuous controller absorbs startup skew against the established offset.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 60;
        h.SecondaryPlayback!.DurationSeconds = 60;

        // Establish offset = 5 via a select round-trip, both paused.
        h.PrimaryPlayback.PositionSeconds = 0;
        h.SecondaryPlayback!.PositionSeconds = 5;
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.Vm.SetSelected(null);  // offset = 5
        h.PrimaryPlayback.IsPaused = true;
        h.SecondaryPlayback!.IsPaused = true;

        h.Vm.PlayPauseCommand.Execute(null);  // both paused (same state) → play together, offset defended

        // Offset must still be the established 5 (not recaptured). A > 1 s drift hard-resyncs to Primary.Pos + 5.
        h.PrimaryPlayback.IsPaused = false;
        h.SecondaryPlayback!.IsPaused = false;
        h.PrimaryPlayback.PositionSeconds = 30;
        h.SecondaryPlayback!.PositionSeconds = 36.5;  // drift = (36.5 - 30) - 5 = 1.5 > hard-resync threshold
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 35.0 }), "offset unchanged at 5 → hard resync targets 30 + 5 = 35");
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

    // === Drift controller tests (PiP Sync — continuous drift correction) ===

    // Drive both contexts past the drift controller's gate prerequisites: a positive duration on each, both actually advancing (IsCoreIdle=false — the gate the controller reads), and IsPaused=false for the transport paths that still read it. Used by the correction tests below to focus on the offset/drift logic without re-asserting gate plumbing in every test. CorrectionGateBlocksWhenSeeking deliberately overrides individual gates.
    private static void ReadySyncMode(Harness h)
    {
        h.PrimaryPlayback.DurationSeconds = 60;
        h.SecondaryPlayback!.DurationSeconds = 60;
        h.PrimaryPlayback.IsPaused = false;
        h.SecondaryPlayback!.IsPaused = false;
        h.PrimaryPlayback.IsCoreIdle = false;
        h.SecondaryPlayback!.IsCoreIdle = false;
    }

    [Test]
    public void EnablePipCapturesZeroOffset()
    {
        // EnablePip lands with both contexts at content time 0 → captured offset is 0 by construction. Distinguish "captured 0" from "captured null/pending": a 0.2 s divergence against offset=0 is a coarse catch-up → SetSpeed(0.95). If EnablePip had left targetOffset null, the first tick would lazy-capture offset=0.2 → drift 0 → deadband idle → NO speed nudge.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.PrimaryPlayback.PositionSeconds = 10;
        h.SecondaryPlayback!.PositionSeconds = 10.2;  // Secondary ahead by 0.2 vs offset 0 → coarse
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 0.95 }).Within(1e-9), "EnablePip captured offset=0; a 0.2 s ahead-drift slows Secondary to 0.95");
    }

    [Test]
    public void FileLoadedOnPrimaryClearsOffset()
    {
        // FileLoaded resets one context's position to 0; the previous offset is no longer meaningful. After clear, the next tick takes the lazy-fallback path: baseline from current positions (drift 0 → idle), then defend the fresh offset.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        // Establish a non-trivial offset by transitioning out of selected mode.
        h.PrimaryPlayback.PositionSeconds = 5;
        h.SecondaryPlayback!.PositionSeconds = 12;  // offset = 7
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.Vm.SetSelected(null);  // capture: offset = 12 - 5 = 7

        // Sanity: an immediate tick with the same positions sees drift 0 → idle (no seek, no nudge).
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.Empty);

        // FileLoaded clears the offset to pending.
        h.PrimaryPlayback.RaiseFileLoaded();
        // First post-clear tick lazy-captures offset = 13 - 5 = 8 (drift 0 → idle). If the clear hadn't happened, offset would still be 7 → drift = (13 - 5) - 7 = 1 → we'd see a nudge/seek. Empty confirms the lazy-capture path.
        h.SecondaryPlayback!.PositionSeconds = 13;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty, "first post-clear tick should lazy-capture, not correct");
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.Empty);

        // Against the fresh offset (8), a 0.3 s ahead-drift is a coarse catch-up (not a >1 s hard resync — which is what a stale offset of 7 would have produced: drift (13.3-5)-7 = 1.3).
        h.SecondaryPlayback!.PositionSeconds = 13.3;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty, "fresh offset 8 → 0.3 s drift stays in coarse, no hard resync");
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 0.95 }).Within(1e-9));
    }

    [Test]
    public void FileLoadedOnSecondaryClearsOffset()
    {
        // Symmetric: Secondary FileLoaded must also clear (Secondary's position got reset to 0).
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.PrimaryPlayback.PositionSeconds = 5;
        h.SecondaryPlayback!.PositionSeconds = 12;
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.Vm.SetSelected(null);
        // Offset captured = 7.

        h.SecondaryPlayback!.RaiseFileLoaded();
        // Lazy-capture round: no correction even with what would be drift against the OLD offset.
        h.PrimaryPlayback.PositionSeconds = 6;
        h.SecondaryPlayback!.PositionSeconds = 14;  // delta = 8, would be drift=1 against old offset 7
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty, "FileLoaded cleared offset → lazy-capture takes the round");
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.Empty);
    }

    [Test]
    public void SelectedToSyncCapturesNewOffset()
    {
        // The selected→null transition is the user's "I just established this offset deliberately" signal. After capture, an in-sync advance with the same offset is a no-op; introducing drift produces a speed nudge in the catch-up direction.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.PrimaryPlayback.PositionSeconds = 0;
        h.SecondaryPlayback!.PositionSeconds = 0;

        // User selects Secondary, frame-steps to align it +5s ahead.
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.SecondaryPlayback!.PositionSeconds = 5;
        // Returns to sync. Capture: offset = 5 - 0 = 5.
        h.Vm.SetSelected(null);

        // No drift: both advance by 10s, offset preserved → idle.
        h.PrimaryPlayback.PositionSeconds = 10;
        h.SecondaryPlayback!.PositionSeconds = 15;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.Empty);

        // Drift introduced: Secondary 0.1 s behind expected → coarse catch-up speeds it up to 1.05.
        h.SecondaryPlayback!.PositionSeconds = 14.9;  // drift = (14.9 - 10) - 5 = -0.1
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 1.05 }).Within(1e-9), "Secondary behind → speed up to 1.05");
    }

    [Test]
    public void SyncToSelectedClearsOffsetAndGatesCorrection()
    {
        // Entering selected mode disables the slave: ApplyDriftCorrection must early-out on a non-null SelectedSlot regardless of drift size or offset state — no seek, no speed nudge.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.PrimaryPlayback.PositionSeconds = 0;
        h.SecondaryPlayback!.PositionSeconds = 0;
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Primary);

        // Even with a huge implied drift, no correction.
        h.SecondaryPlayback!.PositionSeconds = 30;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.Empty);
    }

    [Test]
    public void SyncStepChapterNoChaptersIsNoOpAndPreservesOffset()
    {
        // No chapters on Primary → ChapterStep returns null → true no-op. Nothing moves and the captured offset stays valid. Verify the offset survives: a subsequent > 1 s drift hard-resyncs against offset = 5.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        // Establish a non-zero offset.
        h.PrimaryPlayback.PositionSeconds = 0;
        h.SecondaryPlayback!.PositionSeconds = 5;
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.Vm.SetSelected(null);  // captures offset = 5

        h.Vm.StepChapter(1);  // Primary has no chapters → no-op

        // Offset preserved: a subsequent > 1 s drift hard-resyncs to Primary.Pos + 5, it does NOT lazy-capture.
        h.PrimaryPlayback.PositionSeconds = 30;
        h.SecondaryPlayback!.PositionSeconds = 99;  // drift (99 - 30) - 5 = 64 → hard resync to 30 + 5 = 35
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 35.0 }), "offset preserved → hard resync still defends the invariant");
    }

    [Test]
    public void SyncStepChapterPastLastIsNoOpAndPreservesOffset()
    {
        // Same no-op semantics when the target is out of range (stepping past the last chapter): nothing moves, offset preserved.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.PrimaryPlayback.Chapters = new[]
        {
            new MediaChapter(0, "Intro", 0),
            new MediaChapter(1, "Outro", 200),
        };
        h.PrimaryPlayback.PositionSeconds = 0;
        h.SecondaryPlayback!.PositionSeconds = 5;
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.Vm.SetSelected(null);  // captures offset = 5

        h.PrimaryPlayback.PositionSeconds = 250;  // in the last chapter
        h.Vm.StepChapter(1);  // past the end → no-op

        h.PrimaryPlayback.PositionSeconds = 300;
        h.SecondaryPlayback!.PositionSeconds = 999;  // drift → hard resync to 300 + 5 = 305
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 305.0 }), "offset preserved → hard resync still defends the invariant");
    }

    [Test]
    public void HardResyncAboveOneSecondSeeksRegardlessOfSign()
    {
        // > 1 s drift → snap Secondary to Primary.Pos + offset with a hard seek, sign-agnostic.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.Vm.SetSelected(null);  // offset = 0

        // Secondary far ahead.
        h.PrimaryPlayback.PositionSeconds = 10;
        h.SecondaryPlayback!.PositionSeconds = 11.5;  // drift +1.5
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 10.0 }));

        // Secondary far behind — same absolute target.
        h.SecondaryPlayback!.SeekCalls.Clear();
        h.SecondaryPlayback!.PositionSeconds = 8.5;  // drift -1.5
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 10.0 }));
    }

    [Test]
    public void CoarseCatchupNudgesSpeedInCatchupDirection()
    {
        // 50 ms < |drift| ≤ 1 s → full ±5% nudge, no seek. Ahead → slow (0.95); behind → speed up (1.05).
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.Vm.SetSelected(null);  // offset = 0

        h.PrimaryPlayback.PositionSeconds = 10;
        h.SecondaryPlayback!.PositionSeconds = 10.2;  // ahead 0.2
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 0.95 }).Within(1e-9));
    }

    [Test]
    public void CoarseCatchupBehindSpeedsUp()
    {
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.Vm.SetSelected(null);  // offset = 0

        h.PrimaryPlayback.PositionSeconds = 10;
        h.SecondaryPlayback!.PositionSeconds = 9.8;  // behind 0.2
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 1.05 }).Within(1e-9));
    }

    [Test]
    public void CoarseCatchupLatchesUntilOvershoot()
    {
        // Enter CatchUp behind → 1.05. Even after drift drops below 50 ms (same side), hold full 1.05 (no new SetSpeed). Only an overshoot past zero releases to the fine approach.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.Vm.SetSelected(null);  // offset = 0
        h.PrimaryPlayback.PositionSeconds = 10;

        // Behind by 0.2 → CatchUp, 1.05.
        h.SecondaryPlayback!.PositionSeconds = 9.8;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 1.05 }).Within(1e-9));

        // Still behind, now only 0.03 (< 50 ms) but same sign → latched, hold 1.05 (no redundant SetSpeed).
        h.SecondaryPlayback!.PositionSeconds = 9.97;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 1.05 }).Within(1e-9), "latch holds full rate through the 50 ms boundary");

        // Overshoot: now 0.005 ahead → sign flip → release to Approach → within deadband → back to 1.0.
        h.SecondaryPlayback!.PositionSeconds = 10.005;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 1.05, 1.0 }).Within(1e-9), "overshoot releases the latch and the deadband settles to 1.0");
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
    }

    [Test]
    public void FineProportionalSettleScalesWithDrift()
    {
        // In Approach mode, deadband < |drift| ≤ 50 ms → proportional nudge = 1 - drift (5% at 50 ms, 0% at the deadband edge).
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.Vm.SetSelected(null);  // offset = 0
        h.PrimaryPlayback.PositionSeconds = 10;

        // Ahead 0.035 → 0.965.
        h.SecondaryPlayback!.PositionSeconds = 10.035;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Has.Count.EqualTo(1));
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls[0], Is.EqualTo(0.965).Within(1e-9));

        // Behind 0.035 → 1.035.
        h.SecondaryPlayback!.PositionSeconds = 9.965;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Has.Count.EqualTo(2));
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls[1], Is.EqualTo(1.035).Within(1e-9));
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
    }

    [Test]
    public void DeadbandIdleIssuesNoCorrection()
    {
        // 10 ms drift is within the 20 ms deadband → command 1.0. Speed already 1.0 → no SetSpeed post, no seek.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.Vm.SetSelected(null);  // offset = 0

        h.PrimaryPlayback.PositionSeconds = 10;
        h.SecondaryPlayback!.PositionSeconds = 10.010;  // drift +10 ms
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.Empty);
    }

    [Test]
    public void HardResyncFromCatchupResetsSpeedFirst()
    {
        // A blow-out past 1 s while latched in CatchUp must reset speed to 1.0 (before the seek) and drop the latch.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.Vm.SetSelected(null);  // offset = 0
        h.PrimaryPlayback.PositionSeconds = 10;

        // Enter CatchUp behind → 1.05.
        h.SecondaryPlayback!.PositionSeconds = 9.8;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 1.05 }).Within(1e-9));

        // Now 1.5 s behind → hard resync: SetSpeed(1.0) then Seek to Primary.Pos + 0.
        h.SecondaryPlayback!.PositionSeconds = 8.5;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 1.05, 1.0 }).Within(1e-9));
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 10.0 }));
    }

    [Test]
    public void GateFromCatchupResetsSpeedToOne()
    {
        // A gate hit (here core-idle) while nudging must release the speed back to 1.0 so a paused/stalled Secondary doesn't sit at 1.05.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.Vm.SetSelected(null);  // offset = 0
        h.PrimaryPlayback.PositionSeconds = 10;

        h.SecondaryPlayback!.PositionSeconds = 9.8;  // behind → 1.05
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 1.05 }).Within(1e-9));

        // Secondary starts buffering (core-idle) → gate → speed released to 1.0.
        h.SecondaryPlayback!.IsCoreIdle = true;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 1.05, 1.0 }).Within(1e-9));
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
    }

    [Test]
    public void CorrectionGatedWhenSeeking()
    {
        // IsSeeking on either context blocks correction (a user seek — or the just-issued hard resync — is in flight). No seek, no nudge.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.Vm.SetSelected(null);  // capture offset = 0

        h.SecondaryPlayback!.PositionSeconds = 5;  // would be huge drift
        h.SecondaryPlayback!.IsSeeking = true;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.Empty);

        h.SecondaryPlayback!.IsSeeking = false;
        h.PrimaryPlayback.IsSeeking = true;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.Empty);
    }

    [Test]
    public void CorrectionGatedWhenCoreIdle()
    {
        // Either stream core-idle (paused / cache stall / EOF) blocks correction — nothing advancing to measure.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.Vm.SetSelected(null);  // offset = 0

        h.PrimaryPlayback.PositionSeconds = 10;
        h.SecondaryPlayback!.PositionSeconds = 10.5;  // would be coarse
        h.SecondaryPlayback!.IsCoreIdle = true;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.Empty);
    }

    [Test]
    public void CorrectionWithPipOffIsNoOp()
    {
        // No secondary → the tick must early-out cleanly (the timer keeps firing across enable/disable churn).
        using var h = new Harness();
        h.PrimaryPlayback.DurationSeconds = 60;
        h.PrimaryPlayback.IsCoreIdle = false;
        Assert.DoesNotThrow(() => h.Vm.ApplyDriftCorrection());
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.Empty);
    }

    [Test]
    public void DisablePipClearsOffsetThenReEnableStartsFresh()
    {
        // After DisablePip, the offset must be cleared. Re-EnablePip starts at offset=0 again — verify by establishing a non-zero offset, disabling, re-enabling, and confirming a correction with equal positions is a no-op (offset back to 0) while a real drift nudges against the fresh 0.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);

        h.PrimaryPlayback.PositionSeconds = 0;
        h.SecondaryPlayback!.PositionSeconds = 5;
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.Vm.SetSelected(null);  // captures offset = 5

        h.Vm.DisablePip();
        h.EnablePip();             // Vm.Secondary is a fresh context; offset reset to 0.
        ReadySyncMode(h);

        // With offset=0, equal positions → no drift, idle.
        h.PrimaryPlayback.PositionSeconds = 10;
        h.SecondaryPlayback!.PositionSeconds = 10;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty, "fresh EnablePip must not have inherited offset=5 from prior session");
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.Empty);

        // And drift against the fresh offset=0 nudges correctly.
        h.SecondaryPlayback!.PositionSeconds = 10.2;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 0.95 }).Within(1e-9), "coarse catch-up against fresh offset 0");
    }

    [Test]
    public void CorrectionLazyCapturesWhenOffsetIsNull()
    {
        // The lazy-fallback path: file-load auto-play / lockstep advance / EnablePip-while-already-playing all leave targetOffset null when the first tick fires. That tick baselines from current positions (drift 0 → idle), then later drift nudges.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.PrimaryPlayback.RaiseFileLoaded();  // clears offset

        h.PrimaryPlayback.PositionSeconds = 10;
        h.SecondaryPlayback!.PositionSeconds = 13;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty, "first tick lazy-captures");
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.Empty);

        // Now drift against the captured offset (3) is detectable → coarse nudge.
        h.SecondaryPlayback!.PositionSeconds = 13.3;  // drift = (13.3 - 10) - 3 = +0.3
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 0.95 }).Within(1e-9));
    }

    [Test]
    public void GetSyncDiagnosticReportsOffsetDriftAndSpeed()
    {
        using var h = new Harness();

        // PiP off → disabled snapshot.
        var off = h.Vm.GetSyncDiagnostic();
        Assert.That(off.Enabled, Is.False);
        Assert.That(off.Mode, Is.EqualTo("off"));

        h.EnablePip();
        ReadySyncMode(h);
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.Vm.SetSelected(null);  // offset = 0
        h.PrimaryPlayback.PositionSeconds = 10;
        h.SecondaryPlayback!.PositionSeconds = 10.2;  // ahead 0.2

        h.Vm.ApplyDriftCorrection();  // coarse → 0.95, mode=catchup
        var d = h.Vm.GetSyncDiagnostic();
        Assert.That(d.Enabled, Is.True);
        Assert.That(d.TargetOffsetSeconds!.Value, Is.EqualTo(0.0).Within(1e-9));
        Assert.That(d.CurrentOffsetSeconds, Is.EqualTo(0.2).Within(1e-9));
        Assert.That(d.DriftSeconds!.Value, Is.EqualTo(0.2).Within(1e-9));
        Assert.That(d.Speed, Is.EqualTo(0.95).Within(1e-9));
        Assert.That(d.Mode, Is.EqualTo("catchup"));
    }

    [Test]
    public void SyncPlayPauseWithFilelessPrimaryDrivesFromLoadedSecondary()
    {
        // Files dropped only onto the PiP secondary is a supported flow. In sync mode, Space must be able to both start AND pause the loaded stream: the flip target must derive from a stream whose play state can actually change. A fileless Primary's IsPaused never flips (SetPaused's Duration gate is a no-op), so deriving the target blindly from Primary computes the same value forever — Space could start the Secondary but never pause it.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.IsPaused = true;   // fileless: Duration stays 0, so this can never change
        h.SecondaryPlayback!.DurationSeconds = 60;
        h.SecondaryPlayback!.IsPaused = true;

        h.Vm.PlayPauseCommand.Execute(null);
        Assert.That(h.SecondaryPlayback!.IsPaused, Is.False, "first Space starts the loaded secondary");

        h.Vm.PlayPauseCommand.Execute(null);
        Assert.That(h.SecondaryPlayback!.IsPaused, Is.True, "second Space pauses it again");
    }

    [Test]
    public void SyncPlayPauseDoesNotCaptureOffsetAgainstFilelessStream()
    {
        // The differed-pre-state "join" capture assumes exactly one stream actually changed state. A fileless stream's SetPaused is a gated no-op — it can't join anything, and capturing Secondary.Position − Primary.Position against a stream with no position would store garbage. The EnablePip-established offset must survive.
        using var h = new Harness();
        h.EnablePip();  // offset = 0
        h.PrimaryPlayback.IsPaused = true;  // fileless
        h.SecondaryPlayback!.DurationSeconds = 100;
        h.SecondaryPlayback!.PositionSeconds = 30;
        h.SecondaryPlayback!.IsPaused = false;  // playing → pre-states differ

        h.Vm.PlayPauseCommand.Execute(null);

        var d = h.Vm.GetSyncDiagnostic();
        Assert.That(d.TargetOffsetSeconds!.Value, Is.EqualTo(0.0).Within(1e-9), "join capture must not fire against a fileless stream");
    }
}
