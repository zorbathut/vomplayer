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
        private string? mediaTitle;

        public event Action? FileLoaded;
        public event Action<int>? FileEnded;
        public event Action? TracksReloaded;
        public event Action<bool>? SourceHdrChanged;

        public bool IsSourceHdr { get; set; }
        public string? HwdecCurrent { get; set; }

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
    public void NoSelectionTransportFansOutToBoth()
    {
        using var h = new Harness();
        h.EnablePip();
        Assert.That(h.Vm.SelectedSlot, Is.Null, "default after EnablePip is broadcast/sync");

        // Both contexts need a duration > 0 for SeekTo to compute a normalized time.
        h.PrimaryPlayback.DurationSeconds = 60;
        h.SecondaryPlayback!.DurationSeconds = 60;

        h.Vm.SeekTo(0.5);
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 30.0 }));
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 30.0 }));

        h.Vm.SeekRelative(5);
        Assert.That(h.PrimaryPlayback.SeekRelativeCalls, Is.EqualTo(new[] { 5.0 }));
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls, Is.EqualTo(new[] { 5.0 }));

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
