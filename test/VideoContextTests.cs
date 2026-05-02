using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Vomplayer.Playback;
using Vomplayer.Services;
using Vomplayer.UserData;
using Vomplayer.ViewModels;
using Vomplayer.Wayland;

namespace Vomplayer.Tests;

[TestFixture]
public partial class VideoContextTests
{
    // Minimal FakePlayback for VideoContext tests. Mirrors the surface VideoContext consumes; intentionally not shared with ViewModelMainTests' richer fake because these tests only exercise EOF / playlist / advance — keeping the fake focused makes the tests easier to read.
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
        public int EnableHdrOutputCalls { get; private set; }
        public int DisableHdrOutputCalls { get; private set; }

        public List<string> LoadedFiles { get; } = new();

        public void Initialize() { }
        public void LoadFile(string path) { LoadedFiles.Add(path); }
        public void TogglePause() { IsPaused = !IsPaused; }
        public void SetPaused(bool paused) { IsPaused = paused; }
        public void Seek(double seconds) { }
        public void SeekRelative(double seconds) { }
        public void StepFrameForward() { }
        public void StepFrameBack() { }
        public void StepChapter(int delta) { }
        public void LoadAudio(string path) { }
        public void LoadSubtitle(string path) { }
        public void SetVideo(int? trackId) { }
        public void SetAudio(int? trackId) { }
        public void SetSubtitle(int? trackId) { }
        public void SetVolume(double percent) { Volume = percent; }
        public void AdjustVolume(double deltaPercent) { }
        public void ToggleMute() { IsMuted = !IsMuted; }
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

    // Minimal IHdrSink for HDR-policy tests. Records every SetHdr call (in order) so tests can assert pre-stage / enable / disable sequences. CurrentOutputHdrChanged firing simulates compositor-side output transitions; tests use it to drive ApplyHdrPolicy on output-side change.
    private sealed class FakeHdrSink : IHdrSink
    {
        public List<bool> SetHdrCalls { get; } = new();
        // SetHdr return value — 0 = success, -1 = compositor refused. Tests configure this to simulate the failed-sink case.
        public int NextRc { get; set; }
        public bool? CurrentOutputIsHdrValue { get; set; }
        public bool? CurrentOutputIsHdr { get { return CurrentOutputIsHdrValue; } }
        public event Action? CurrentOutputHdrChanged;

        public int SetHdr(bool enable)
        {
            SetHdrCalls.Add(enable);
            return NextRc;
        }

        public void RaiseOutputHdrChanged() { CurrentOutputHdrChanged?.Invoke(); }
    }

    private static VideoContext NewContext(out FakePlayback playback)
    {
        playback = new FakePlayback();
        return new VideoContext(playback, new StubFilePicker(), new StubRecentFiles(), new StubTrackPreferences(), new StubUrlDownloader(), new StubUrlPrompt());
    }

    [Test]
    public void HasNextItemReflectsPlaylistPosition()
    {
        using var ctx = NewContext(out _);
        Assert.That(ctx.HasNextItem, Is.False, "empty playlist");

        ctx.LoadPaths(new[] { "/a.mp4" }, replace: true);
        Assert.That(ctx.HasNextItem, Is.False, "single-item playlist at index 0");

        ctx.LoadPaths(new[] { "/a.mp4", "/b.mp4", "/c.mp4" }, replace: true);
        Assert.That(ctx.HasNextItem, Is.True, "three-item at index 0");

        ctx.PlayPlaylistItem(2);
        Assert.That(ctx.HasNextItem, Is.False, "three-item at last index");
    }

    [Test]
    public void AdvanceAndLoadIfPossibleReturnsFalseAtEnd()
    {
        using var ctx = NewContext(out var pb);
        ctx.LoadPaths(new[] { "/a.mp4" }, replace: true);
        // Single item — at index 0, no next.
        Assert.That(ctx.AdvanceAndLoadIfPossible(), Is.False);
        // No additional LoadFile beyond the initial replace.
        Assert.That(pb.LoadedFiles, Is.EqualTo(new[] { "/a.mp4" }));
    }

    [Test]
    public void MediaTitleMirrorsPlayback()
    {
        using var ctx = NewContext(out var pb);
        var fires = new List<string?>();
        ctx.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(VideoContext.MediaTitle))
            {
                fires.Add(ctx.MediaTitle);
            }
        };
        Assert.That(ctx.MediaTitle, Is.Null, "no source loaded ⇒ MediaTitle is null");

        pb.MediaTitle = "Big Buck Bunny";
        Assert.That(ctx.MediaTitle, Is.EqualTo("Big Buck Bunny"));

        pb.MediaTitle = "Sintel";
        Assert.That(ctx.MediaTitle, Is.EqualTo("Sintel"));

        pb.MediaTitle = null;
        Assert.That(ctx.MediaTitle, Is.Null, "unload clears the title");

        Assert.That(fires, Is.EqualTo(new string?[] { "Big Buck Bunny", "Sintel", null }));
    }

    [Test]
    public void VideoAspectMirrorsPlayback()
    {
        using var ctx = NewContext(out var pb);
        var fires = new List<double?>();
        ctx.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(VideoContext.VideoAspect))
            {
                fires.Add(ctx.VideoAspect);
            }
        };
        Assert.That(ctx.VideoAspect, Is.Null, "no source loaded ⇒ aspect is null");

        pb.VideoAspect = 16.0 / 9.0;
        Assert.That(ctx.VideoAspect, Is.EqualTo(16.0 / 9.0).Within(1e-9));

        pb.VideoAspect = 4.0 / 3.0;
        Assert.That(ctx.VideoAspect, Is.EqualTo(4.0 / 3.0).Within(1e-9));

        pb.VideoAspect = null;
        Assert.That(ctx.VideoAspect, Is.Null, "unload clears the aspect");

        Assert.That(fires, Is.EqualTo(new double?[] { 16.0 / 9.0, 4.0 / 3.0, null }));
    }

    [Test]
    public void AdvanceAndLoadIfPossibleAdvancesAndLoadsNext()
    {
        using var ctx = NewContext(out var pb);
        ctx.LoadPaths(new[] { "/a.mp4", "/b.mp4" }, replace: true);
        Assert.That(ctx.AdvanceAndLoadIfPossible(), Is.True);
        Assert.That(ctx.Playlist.CurrentIndex, Is.EqualTo(1));
        Assert.That(pb.LoadedFiles, Is.EqualTo(new[] { "/a.mp4", "/b.mp4" }));
    }

    [Test]
    public void AutoAdvanceDisabledSuppressesEofRisingEdgeAdvance()
    {
        using var ctx = NewContext(out var pb);
        ctx.AutoAdvanceEnabled = false;
        ctx.LoadPaths(new[] { "/a.mp4", "/b.mp4" }, replace: true);
        // FileLoaded primes currentFileLoaded; duration arrives; then EOF rises. With AutoAdvanceEnabled=false the per-context handler must NOT call AdvanceAndLoadIfPossible.
        pb.RaiseFileLoaded();
        pb.DurationSeconds = 60;
        pb.IsEofReached = true;
        Assert.That(ctx.Playlist.CurrentIndex, Is.EqualTo(0), "current must not advance when auto-advance is off");
        Assert.That(pb.LoadedFiles, Is.EqualTo(new[] { "/a.mp4" }), "no second LoadFile");
        // Coordinator-driven path still works manually.
        Assert.That(ctx.IsAtPlayableEof, Is.True, "IsAtPlayableEof signals to the coordinator that this context is ready");
    }

    [Test]
    public void AutoAdvanceEnabledFiresOnEofRisingEdgeAsBefore()
    {
        using var ctx = NewContext(out var pb);
        // Default: AutoAdvanceEnabled = true.
        ctx.LoadPaths(new[] { "/a.mp4", "/b.mp4" }, replace: true);
        pb.RaiseFileLoaded();
        pb.DurationSeconds = 60;
        pb.IsEofReached = true;
        Assert.That(ctx.Playlist.CurrentIndex, Is.EqualTo(1), "single-context EOF advances by default");
        Assert.That(pb.LoadedFiles, Is.EqualTo(new[] { "/a.mp4", "/b.mp4" }));
    }

    [Test]
    public void IsAtPlayableEofTracksThreeGates()
    {
        using var ctx = NewContext(out var pb);
        var transitions = new List<bool>();
        ctx.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(VideoContext.IsAtPlayableEof))
            {
                transitions.Add(ctx.IsAtPlayableEof);
            }
        };

        // No file loaded: IsAtPlayableEof must be false even with EOF=true (mirrors the currentFileLoaded gate).
        pb.IsEofReached = true;
        Assert.That(ctx.IsAtPlayableEof, Is.False);

        // Reset for the load-and-advance flow.
        pb.IsEofReached = false;
        ctx.LoadPaths(new[] { "/a.mp4" }, replace: true);
        pb.RaiseFileLoaded();
        // Duration still 0 — IsAtPlayableEof gates on > 0.
        pb.IsEofReached = true;
        Assert.That(ctx.IsAtPlayableEof, Is.False, "duration=0 must keep IsAtPlayableEof false");

        // Duration arrives — now all three gates met (currentFileLoaded, IsEofReached, Duration > 0). Should rise.
        pb.DurationSeconds = 30;
        Assert.That(ctx.IsAtPlayableEof, Is.True, "all three gates met → true");

        // Falling edge: EOF clears.
        pb.IsEofReached = false;
        Assert.That(ctx.IsAtPlayableEof, Is.False);

        // PropertyChanged fired exactly twice (false→true, true→false).
        Assert.That(transitions, Is.EqualTo(new[] { true, false }));
    }

    [Test]
    public void TwoContextsAreIndependent()
    {
        using var a = NewContext(out var pbA);
        using var b = NewContext(out var pbB);
        a.AutoAdvanceEnabled = false;
        b.AutoAdvanceEnabled = false;

        a.LoadPaths(new[] { "/a1.mp4", "/a2.mp4" }, replace: true);
        b.LoadPaths(new[] { "/b1.mp4", "/b2.mp4", "/b3.mp4" }, replace: true);

        Assert.That(a.Playlist.Items, Is.EqualTo(new[] { "/a1.mp4", "/a2.mp4" }));
        Assert.That(b.Playlist.Items, Is.EqualTo(new[] { "/b1.mp4", "/b2.mp4", "/b3.mp4" }));
        Assert.That(a.Playlist.CurrentIndex, Is.EqualTo(0));
        Assert.That(b.Playlist.CurrentIndex, Is.EqualTo(0));

        // Coordinator-style advance: both IndependentLockstep dictates b advances independently when called.
        b.AdvanceAndLoadIfPossible();
        Assert.That(a.Playlist.CurrentIndex, Is.EqualTo(0), "a unaffected");
        Assert.That(b.Playlist.CurrentIndex, Is.EqualTo(1), "b advanced");

        // Property updates on pbA do not propagate to context b (each has its own playback subscription).
        pbA.DurationSeconds = 100;
        pbA.PositionSeconds = 50;
        Assert.That(a.Duration, Is.EqualTo(TimeSpan.FromSeconds(100)));
        Assert.That(b.Duration, Is.EqualTo(TimeSpan.Zero), "b's duration is independent");
    }

    [Test]
    public void SourceHdrTrueWithSuccessfulSinkEnablesHdr()
    {
        using var ctx = NewContext(out var pb);
        var sink = new FakeHdrSink { NextRc = 0 };
        ctx.AttachHdrSink(sink);
        sink.SetHdrCalls.Clear();
        pb.RaiseSourceHdrChanged(true);
        Assert.That(sink.SetHdrCalls, Is.EqualTo(new[] { true }), "SetHdr(true) must run");
        Assert.That(pb.EnableHdrOutputCalls, Is.EqualTo(1), "mpv advanced to PQ targets");
        Assert.That(ctx.ActiveHdrState, Is.EqualTo(VideoContext.HdrActiveState.Hdr));
    }

    [Test]
    public void SourceHdrTrueWithFailingSinkStaysSdrAndDoesNotEnableMpvHdr()
    {
        using var ctx = NewContext(out var pb);
        var sink = new FakeHdrSink { NextRc = -1 };
        ctx.AttachHdrSink(sink);
        sink.SetHdrCalls.Clear();
        pb.RaiseSourceHdrChanged(true);
        Assert.That(sink.SetHdrCalls, Is.EqualTo(new[] { true }), "SetHdr(true) attempted");
        Assert.That(pb.EnableHdrOutputCalls, Is.EqualTo(0), "mpv stays SDR when shim refused — no PQ-encoded pixels into untagged surface");
        Assert.That(ctx.ActiveHdrState, Is.EqualTo(VideoContext.HdrActiveState.Failed));
    }

    [Test]
    public void SourceHdrFalseAttachesSdrAndDisablesMpvHdr()
    {
        using var ctx = NewContext(out var pb);
        var sink = new FakeHdrSink { NextRc = 0 };
        ctx.AttachHdrSink(sink);
        // Drive HDR first to give DisableHdrOutput somewhere to step down from.
        pb.RaiseSourceHdrChanged(true);
        sink.SetHdrCalls.Clear();
        pb.RaiseSourceHdrChanged(false);
        Assert.That(sink.SetHdrCalls, Is.EqualTo(new[] { false }));
        Assert.That(pb.DisableHdrOutputCalls, Is.GreaterThanOrEqualTo(1));
        Assert.That(ctx.ActiveHdrState, Is.EqualTo(VideoContext.HdrActiveState.Sdr));
    }

    [Test]
    public void DetachHdrSinkUnsubscribesOutputChange()
    {
        using var ctx = NewContext(out var pb);
        var sink = new FakeHdrSink { NextRc = 0 };
        ctx.AttachHdrSink(sink);
        // Source is HDR; sink attaches → ApplyHdrPolicy ran during AttachHdrSink and lastSourceHdr is false (no SourceHdrChanged fired yet).
        pb.RaiseSourceHdrChanged(true);
        sink.SetHdrCalls.Clear();

        ctx.DetachHdrSink();
        // Output transition AFTER detach: ApplyHdrPolicy must NOT run (sink was the source of subscription).
        sink.RaiseOutputHdrChanged();
        Assert.That(sink.SetHdrCalls, Is.Empty);
    }

    [Test]
    public void IsAtPlayableEofResetsOnLoadCurrentItem()
    {
        // Scenario: at EOF, then user clicks a different playlist item — IsAtPlayableEof must drop to false synchronously when the new load starts (currentFileLoaded resets to false), so the coordinator doesn't see a stale "at EOF" signal across the load boundary.
        using var ctx = NewContext(out var pb);
        ctx.LoadPaths(new[] { "/a.mp4", "/b.mp4" }, replace: true);
        pb.RaiseFileLoaded();
        pb.DurationSeconds = 60;
        pb.IsEofReached = true;
        // Auto-advance fires by default; observe the post-advance state.
        Assert.That(ctx.Playlist.CurrentIndex, Is.EqualTo(1));
        Assert.That(ctx.IsAtPlayableEof, Is.False, "post-advance LoadCurrentItem reset currentFileLoaded → IsAtPlayableEof drops to false");
    }
}
