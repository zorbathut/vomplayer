using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Vomplayer.Playback;
using Vomplayer.Services;
using Vomplayer.UserData;
using Vomplayer.ViewModels;

namespace Vomplayer.Tests;

[TestFixture]
public partial class ViewModelMainTests
{
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
        public int EnableHdrOutputCalls { get; private set; }
        public int DisableHdrOutputCalls { get; private set; }
        public bool IsSourceFpsTrusted { get; set; } = true;
        public string FpsTrustReason { get; set; } = "";
        public List<double> SetFrameMultiplierCalls { get; } = new();
        public int ClearFrameMultiplierCalls { get; private set; }

        public int InitializeCalls { get; private set; }
        public int TogglePauseCalls { get; private set; }
        // Mutable from tests so post-load assertions can isolate per-step deltas (the playlist tests reset these between LoadPaths and a follow-up auto-advance to assert "this trigger ALONE produced one new load").
        public int LoadFileCalls { get; set; }
        public string? LastLoadedFile { get; set; }
        public List<string> LoadedFiles { get; } = new();
        public double? LastSeekSeconds { get; private set; }
        public string? LastLoadedAudio { get; private set; }
        public string? LastLoadedSubtitle { get; private set; }
        public List<int?> VideoSelections { get; } = new();
        public List<int?> AudioSelections { get; } = new();
        public List<int?> SubtitleSelections { get; } = new();
        public List<double> RelativeSeeks { get; } = new();
        public int FrameStepForwardCalls { get; private set; }
        public int FrameStepBackCalls { get; private set; }
        public List<double> VolumeWrites { get; } = new();
        public List<double> VolumeAdjustments { get; } = new();
        public int ToggleMuteCalls { get; private set; }

        public void Initialize()
        {
            InitializeCalls++;
        }

        public void LoadFile(string path, bool startPaused)
        {
            LoadFileCalls++;
            LastLoadedFile = path;
            LoadedFiles.Add(path);
        }

        // Real Playback.TogglePause writes to mpv asynchronously — IsPaused only changes when mpv echoes it back via OnMpvPropertyChanged. The fake elides that latency intentionally; tests asserting on rapid-toggle race behavior would need to add an async-ish fake.
        public void TogglePause()
        {
            TogglePauseCalls++;
            IsPaused = !IsPaused;
        }

        public List<bool> SetPausedCalls { get; } = new();
        public void SetPaused(bool paused)
        {
            SetPausedCalls.Add(paused);
            IsPaused = paused;
        }

        public void Seek(double seconds)
        {
            LastSeekSeconds = seconds;
        }

        public void SeekRelative(double seconds)
        {
            RelativeSeeks.Add(seconds);
        }

        public void StepFrameForward()
        {
            FrameStepForwardCalls++;
        }

        public void StepFrameBack()
        {
            FrameStepBackCalls++;
        }

        public void LoadAudio(string path)
        {
            LastLoadedAudio = path;
        }

        public void LoadSubtitle(string path)
        {
            LastLoadedSubtitle = path;
        }

        public void SetVideo(int? trackId)
        {
            VideoSelections.Add(trackId);
        }

        public void SetAudio(int? trackId)
        {
            AudioSelections.Add(trackId);
        }

        public void SetSubtitle(int? trackId)
        {
            SubtitleSelections.Add(trackId);
        }

        // Mirror real Playback's behavior at the fake's seam: SetVolume tracks the request and applies it to the observable so VM mirrors update synchronously. AdjustVolume logs the delta separately so tests can distinguish "user dragged the slider" (SetVolume) from "user pressed VolumeUp" (AdjustVolume).
        public void SetVolume(double percent)
        {
            VolumeWrites.Add(percent);
            Volume = percent;
        }

        // Real Playback.AdjustVolume issues mpv's `add volume <delta>` command — no local read-modify-write, so the cached `Volume` doesn't change here either. Tests that want to observe the post-adjust value should set `Volume` themselves to simulate the mpv echo.
        public void AdjustVolume(double deltaPercent)
        {
            VolumeAdjustments.Add(deltaPercent);
        }

        public void SetSpeed(double rate)
        {
        }

        public void ToggleMute()
        {
            ToggleMuteCalls++;
            IsMuted = !IsMuted;
        }

        public void EnableHdrOutput()
        {
            EnableHdrOutputCalls++;
        }

        public void DisableHdrOutput()
        {
            DisableHdrOutputCalls++;
        }

        public void SetFrameMultiplier(double outputFps) { SetFrameMultiplierCalls.Add(outputFps); }
        public void ClearFrameMultiplier() { ClearFrameMultiplierCalls++; }

        public void RaiseSourceHdrChanged(bool isHdr)
        {
            IsSourceHdr = isHdr;
            SourceHdrChanged?.Invoke(isHdr);
        }

        public void RaiseIsSourceFpsTrustedChanged(bool trusted)
        {
            IsSourceFpsTrusted = trusted;
            IsSourceFpsTrustedChanged?.Invoke(trusted);
        }

        public void RaiseFileLoaded()
        {
            FileLoaded?.Invoke();
        }


        public void RaiseTracksReloaded()
        {
            TracksReloaded?.Invoke();
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeFilePicker : IFilePicker
    {
        public string? NextResult { get; set; }
        public string? NextAudioResult { get; set; }
        public string? NextSubtitleResult { get; set; }
        public int Calls { get; private set; }
        public int AudioCalls { get; private set; }
        public int SubtitleCalls { get; private set; }

        public Task<string?> PickVideoFileAsync(string title)
        {
            Calls++;
            return Task.FromResult(NextResult);
        }

        public Task<string?> PickAudioFileAsync(string title)
        {
            AudioCalls++;
            return Task.FromResult(NextAudioResult);
        }

        public Task<string?> PickSubtitleFileAsync(string title)
        {
            SubtitleCalls++;
            return Task.FromResult(NextSubtitleResult);
        }
    }

    private sealed class FakeRecentFiles : IRecentFiles
    {
        public List<string> RecordedPaths { get; } = new();
        // Pre-seed via the dictionary to simulate "this file already has a saved position"; mutated by RecordPosition for save-side assertions.
        public Dictionary<string, double> Positions { get; } = new();
        public List<(string Path, double Position)> RecordedPositions { get; } = new();
        public List<string> GetPositionCalls { get; } = new();

        public void Record(string pathOrUri)
        {
            RecordedPaths.Add(pathOrUri);
        }

        public void RecordPosition(string pathOrUri, double positionSeconds)
        {
            RecordedPositions.Add((pathOrUri, positionSeconds));
            Positions[pathOrUri] = positionSeconds;
        }

        public double? GetPosition(string pathOrUri)
        {
            GetPositionCalls.Add(pathOrUri);
            return Positions.TryGetValue(pathOrUri, out var p) ? p : null;
        }
    }

    private sealed class FakeTrackPreferences : ITrackPreferences
    {
        // Order-preserving log of (directory, kind, preference) to make assertion-by-equality easy in save tests.
        public List<(string Dir, MediaKind Kind, TrackPreference Pref)> RecordedPrefs { get; } = new();
        // Pre-seeded responses for Get; tests put a value here to simulate "this directory has a saved preference for this kind".
        public Dictionary<(string Dir, MediaKind Kind), TrackPreference> Stored { get; } = new();
        public List<(string Dir, MediaKind Kind)> GetCalls { get; } = new();

        public void Record(string directory, MediaKind kind, TrackPreference preference)
        {
            RecordedPrefs.Add((directory, kind, preference));
            Stored[(directory, kind)] = preference;
        }

        public TrackPreference? Get(string directory, MediaKind kind)
        {
            GetCalls.Add((directory, kind));
            return Stored.TryGetValue((directory, kind), out var p) ? p : null;
        }
    }

    private sealed class FakeUrlDownloader : Vomplayer.Services.IUrlDownloader
    {
        public bool Available { get; set; } = true;
        public IReadOnlyList<string> NextProbeResult { get; set; } = Array.Empty<string>();
        // Default to YtDlpDownload so legacy tests written before the probe step still exercise the download path. Tests that want to validate the mpv-direct branch set this to MpvDirect explicitly.
        public Vomplayer.Services.UrlLoadKind ClassifyResult { get; set; } = Vomplayer.Services.UrlLoadKind.YtDlpDownload;
        public Exception? ClassifyException { get; set; }
        public Func<string, string>? DownloadResolver { get; set; }
        public TaskCompletionSource<string>? PendingDownload { get; set; }
        public List<string> ProbeCalls { get; } = new();
        public List<string> ClassifyCalls { get; } = new();
        public List<string> DownloadCalls { get; } = new();
        public List<CancellationToken> ObservedTokens { get; } = new();

        public Task<bool> IsAvailableAsync(CancellationToken ct)
        {
            return Task.FromResult(Available);
        }

        public Task<IReadOnlyList<string>> ProbeAsync(string url, CancellationToken ct)
        {
            ProbeCalls.Add(url);
            return Task.FromResult(NextProbeResult);
        }

        public Task<Vomplayer.Services.UrlLoadKind> ClassifyAsync(string url, CancellationToken ct)
        {
            ClassifyCalls.Add(url);
            if (ClassifyException != null)
            {
                throw ClassifyException;
            }
            return Task.FromResult(ClassifyResult);
        }

        public Task<string> DownloadAsync(string url, IProgress<Vomplayer.Services.UrlDownloadProgress>? progress, CancellationToken ct)
        {
            DownloadCalls.Add(url);
            ObservedTokens.Add(ct);
            if (PendingDownload != null)
            {
                // Capture the TCS at call time so a later test mutation of PendingDownload (e.g., setting it to null before triggering the next download) doesn't NRE the cancellation callback.
                var tcs = PendingDownload;
                ct.Register(() => tcs.TrySetCanceled(ct));
                return tcs.Task;
            }
            if (DownloadResolver != null)
            {
                return Task.FromResult(DownloadResolver(url));
            }
            return Task.FromResult($"/fake/{url}");
        }
    }

    private sealed class FakeUrlPrompt : Vomplayer.Services.IUrlPrompt
    {
        public string? NextUrl { get; set; }
        public List<(string Title, string Message)> Errors { get; } = new();
        public int PromptCalls { get; private set; }
        public int StatusShown { get; private set; }
        public int StatusDisposed { get; private set; }

        public Task<string?> PromptForUrlAsync(string title)
        {
            PromptCalls++;
            return Task.FromResult(NextUrl);
        }

        public void ShowError(string title, string message)
        {
            Errors.Add((title, message));
        }

        public Vomplayer.Services.IUrlStatusHandle ShowUrlStatus(string statusText, Action? onCancel)
        {
            StatusShown++;
            return new TrackingStatus(this);
        }

        private sealed class TrackingStatus : Vomplayer.Services.IUrlStatusHandle
        {
            private readonly FakeUrlPrompt owner;
            private bool disposed;
            public TrackingStatus(FakeUrlPrompt owner) { this.owner = owner; }
            public IProgress<Vomplayer.Services.UrlDownloadProgress> Progress { get; } = new Progress<Vomplayer.Services.UrlDownloadProgress>(_ => { });
            public void Dispose()
            {
                if (disposed) { return; }
                disposed = true;
                owner.StatusDisposed++;
            }
        }
    }

    [Test]
    public void NullPlaybackThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new ViewModelMain(null!, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt()));
    }

    [Test]
    public void NullFilePickerThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new ViewModelMain(new FakePlayback(), null!, new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt()));
    }

    [Test]
    public void NullRecentFilesThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new ViewModelMain(new FakePlayback(), new FakeFilePicker(), null!, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt()));
    }

    [Test]
    public void PositionMirrorsPlaybackPositionSeconds()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        pb.PositionSeconds = 12.5;
        Assert.That(vm.Position, Is.EqualTo(TimeSpan.FromSeconds(12.5)));
    }

    [Test]
    public void DurationMirrorsPlaybackDurationSeconds()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        pb.DurationSeconds = 60;
        Assert.That(vm.Duration, Is.EqualTo(TimeSpan.FromSeconds(60)));
    }

    [Test]
    public void IsPausedMirrorsPlayback()
    {
        var pb = new FakePlayback { IsPaused = true };
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        pb.IsPaused = false;
        Assert.That(vm.IsPaused, Is.False);
    }

    [Test]
    public void ChaptersMirrorPlayback()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        var snap = new[] { new MediaChapter(0, "Intro", 0.0), new MediaChapter(1, "Act 1", 60.0) };
        pb.Chapters = snap;
        Assert.That(vm.Chapters, Is.EqualTo(snap));
    }

    [Test]
    public void SeekValueTracksPlaybackWhenNotDragging()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        pb.DurationSeconds = 100;
        pb.PositionSeconds = 25;
        Assert.That(vm.SeekValue, Is.EqualTo(0.25));
    }

    [Test]
    public void SeekToSeeksToNormalizedPositionScaledByDuration()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        pb.DurationSeconds = 100;

        vm.SeekTo(0.4);

        Assert.That(pb.LastSeekSeconds, Is.EqualTo(40));
    }

    [Test]
    public void SeekRelativeForwardsToPlayback()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        vm.SeekRelative(-5);
        vm.SeekRelative(10);
        Assert.That(pb.RelativeSeeks, Is.EqualTo(new[] { -5.0, 10.0 }));
    }

    [Test]
    public void StepFrameForwardsToPlayback()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        vm.StepFrameForward();
        vm.StepFrameForward();
        vm.StepFrameBack();
        Assert.That(pb.FrameStepForwardCalls, Is.EqualTo(2));
        Assert.That(pb.FrameStepBackCalls, Is.EqualTo(1));
    }

    [Test]
    public void StepChapterSeeksToComputedChapterTarget()
    {
        // Single-video chapter nav computes the target from the mirror Chapters + Position (no mpv add-chapter) and seeks there. preroll defaults to 0, so it lands on the exact cue.
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        pb.Chapters = new[]
        {
            new MediaChapter(0, "a", 0),
            new MediaChapter(1, "b", 60),
            new MediaChapter(2, "c", 120),
        };
        pb.PositionSeconds = 70;          // in chapter 1
        vm.StepChapter(1);                // → chapter 2 (120)
        Assert.That(pb.LastSeekSeconds, Is.EqualTo(120));
        vm.StepChapter(-1);               // position still 70 (fake Seek doesn't advance) → chapter 0 (0)
        Assert.That(pb.LastSeekSeconds, Is.EqualTo(0));
    }

    [Test]
    public void NextAndPreviousTrackRouteToPrimaryWithNoSelection()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        // Fake paths exercise only the middle-of-playlist branch (no filesystem touch).
        vm.LoadPaths(new[] { "/a.mp4", "/b.mp4", "/c.mp4" }, replace: true);

        vm.NextTrack();
        Assert.That(vm.Primary.Playlist.CurrentIndex, Is.EqualTo(1));
        Assert.That(pb.LoadedFiles, Is.EqualTo(new[] { "/a.mp4", "/b.mp4" }));

        vm.PreviousTrack();
        Assert.That(vm.Primary.Playlist.CurrentIndex, Is.EqualTo(0));
        Assert.That(pb.LoadedFiles, Is.EqualTo(new[] { "/a.mp4", "/b.mp4", "/a.mp4" }));
    }

    [Test]
    public void SeekValueTracksPlaybackPosition()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        pb.DurationSeconds = 100;

        pb.PositionSeconds = 75;

        Assert.That(vm.SeekValue, Is.EqualTo(0.75));
    }

    [Test]
    public void PlayPauseCommandSyncBroadcastsToggleAsSetPaused()
    {
        // Single-video case (Secondary == null) means no selection and the PlayPause sync-broadcast path. Fires SetPaused with target = !Primary.IsPaused; not TogglePause. The behavior is observably identical to the user (still flips state) — the difference is the underlying call shape, which matters when PiP is on and two streams have drifted.
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        pb.DurationSeconds = 60;
        // Primary starts IsPaused=true (default); flip target is false.
        vm.PlayPauseCommand.Execute(null);
        Assert.That(pb.SetPausedCalls, Is.EqualTo(new[] { false }));
        Assert.That(pb.IsPaused, Is.False);
    }

    [Test]
    public void PlayPauseCommandIsNoopBeforeFileLoad()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        vm.PlayPauseCommand.Execute(null);
        Assert.That(pb.TogglePauseCalls, Is.EqualTo(0));
        Assert.That(pb.SetPausedCalls, Is.Empty);
    }

    [Test]
    public async Task OpenCommandLoadsPickedFile()
    {
        var pb = new FakePlayback();
        var picker = new FakeFilePicker { NextResult = "/path/to/video.mp4" };
        var vm = new ViewModelMain(pb, picker, new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        await vm.OpenCommand.ExecuteAsync(null);
        Assert.That(picker.Calls, Is.EqualTo(1));
        Assert.That(pb.LastLoadedFile, Is.EqualTo("/path/to/video.mp4"));
    }

    [Test]
    public async Task OpenCommandRecordsBeforeLoading()
    {
        var pb = new FakePlayback();
        var picker = new FakeFilePicker { NextResult = "/path/to/video.mp4" };
        var recents = new FakeRecentFiles();
        var vm = new ViewModelMain(pb, picker, recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        await vm.OpenCommand.ExecuteAsync(null);
        Assert.That(recents.RecordedPaths, Is.EqualTo(new[] { "/path/to/video.mp4" }));
    }

    [Test]
    public async Task OpenCommandIgnoresCancelledPicker()
    {
        var pb = new FakePlayback();
        var picker = new FakeFilePicker { NextResult = null };
        var recents = new FakeRecentFiles();
        var vm = new ViewModelMain(pb, picker, recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        await vm.OpenCommand.ExecuteAsync(null);
        Assert.That(picker.Calls, Is.EqualTo(1));
        Assert.That(pb.LastLoadedFile, Is.Null);
        // Cancellation must not record — recents are user-opened files, not user-attempts.
        Assert.That(recents.RecordedPaths, Is.Empty);
    }

    [Test]
    public void OpenFileLoadsTheTarget()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        vm.OpenFile("/path/to/dropped.mp4");
        Assert.That(pb.LoadFileCalls, Is.EqualTo(1));
        Assert.That(pb.LastLoadedFile, Is.EqualTo("/path/to/dropped.mp4"));
    }

    [Test]
    public void OpenFileRecordsTheTarget()
    {
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        vm.OpenFile("/path/to/dropped.mp4");
        Assert.That(recents.RecordedPaths, Is.EqualTo(new[] { "/path/to/dropped.mp4" }));
    }

    [Test]
    public async Task OpenFileAcceptsRemoteUris()
    {
        // A direct-stream URL — yt-dlp's classification step returns MpvDirect (matches the generic extractor), so mpv loads the URL itself rather than going through a download. Recents records the original URI either way.
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var dl = new FakeUrlDownloader { ClassifyResult = Vomplayer.Services.UrlLoadKind.MpvDirect };
        var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), dl, new FakeUrlPrompt());
        vm.OpenFile("https://example.com/stream.m3u8");
        await Task.Yield();
        await Task.Delay(10);
        Assert.That(pb.LastLoadedFile, Is.EqualTo("https://example.com/stream.m3u8"));
        Assert.That(recents.RecordedPaths, Is.EqualTo(new[] { "https://example.com/stream.m3u8" }));
        Assert.That(dl.DownloadCalls, Is.Empty, "mpv-direct branch must not invoke DownloadAsync");
    }

    [Test]
    public void OnRenderContextReadyLoadsInitialFileOnce()
    {
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt())
        {
            InitialFiles = new[] { "/path/to/initial.mp4" },
        };

        vm.OnRenderContextReady();
        Assert.That(pb.LoadFileCalls, Is.EqualTo(1));
        Assert.That(pb.LastLoadedFile, Is.EqualTo("/path/to/initial.mp4"));
        // The command-line file should be in recents the same way drag-drop and picker openings are.
        Assert.That(recents.RecordedPaths, Is.EqualTo(new[] { "/path/to/initial.mp4" }));

        // A second fire (e.g. detach/reattach) must not reload or re-record.
        vm.OnRenderContextReady();
        Assert.That(pb.LoadFileCalls, Is.EqualTo(1));
        Assert.That(recents.RecordedPaths.Count, Is.EqualTo(1));
    }

    [Test]
    public void OnRenderContextReadyLoadsAllInitialFiles()
    {
        // `vomplayer a.mp4 b.mp4` on a cold start must load both — the same invocation forwarded to a running primary already did, and the two paths must agree.
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt())
        {
            InitialFiles = new[] { "/a.mp4", "/b.mp4" },
        };

        vm.OnRenderContextReady();
        Assert.That(vm.Primary.Playlist.Items, Is.EqualTo(new[] { "/a.mp4", "/b.mp4" }));
        Assert.That(pb.LastLoadedFile, Is.EqualTo("/a.mp4"));
    }

    [Test]
    public void OnRenderContextReadyWithNoInitialFileNoOps()
    {
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        vm.OnRenderContextReady();
        Assert.That(pb.LastLoadedFile, Is.Null);
        Assert.That(recents.RecordedPaths, Is.Empty);
    }

    [Test]
    public void DisposeUnsubscribesFromPlayback()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        pb.PositionSeconds = 5;
        var beforeDispose = vm.Position;

        vm.Dispose();
        pb.PositionSeconds = 99;
        Assert.That(vm.Position, Is.EqualTo(beforeDispose));
    }

    [Test]
    public void TrackListsMirrorPlayback()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        var video = new[] { new MediaTrack(1, null, null, false, null) };
        var audio = new[] { new MediaTrack(1, null, "eng", false, null), new MediaTrack(2, null, "fre", false, null) };
        var subs = new[] { new MediaTrack(1, "English", "eng", false, null) };
        pb.VideoTracks = video;
        pb.AudioTracks = audio;
        pb.SubtitleTracks = subs;
        Assert.That(vm.VideoTracks, Is.SameAs(video));
        Assert.That(vm.AudioTracks, Is.SameAs(audio));
        Assert.That(vm.SubtitleTracks, Is.SameAs(subs));
    }

    [Test]
    public void CurrentIdsMirrorPlayback()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        pb.CurrentVideoId = 1;
        pb.CurrentAudioId = 2;
        pb.CurrentSubtitleId = 3;
        Assert.That(vm.CurrentVideoId, Is.EqualTo(1));
        Assert.That(vm.CurrentAudioId, Is.EqualTo(2));
        Assert.That(vm.CurrentSubtitleId, Is.EqualTo(3));
        pb.CurrentVideoId = null;
        pb.CurrentAudioId = null;
        pb.CurrentSubtitleId = null;
        Assert.That(vm.CurrentVideoId, Is.Null);
        Assert.That(vm.CurrentAudioId, Is.Null);
        Assert.That(vm.CurrentSubtitleId, Is.Null);
    }

    [Test]
    public void SelectMethodsRouteToPlayback()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        vm.SelectVideo(7);
        vm.SelectAudio(8);
        vm.SelectSubtitle(9);
        vm.SelectVideo(null);
        vm.SelectAudio(null);
        vm.SelectSubtitle(null);
        Assert.That(pb.VideoSelections, Is.EqualTo(new int?[] { 7, null }));
        Assert.That(pb.AudioSelections, Is.EqualTo(new int?[] { 8, null }));
        Assert.That(pb.SubtitleSelections, Is.EqualTo(new int?[] { 9, null }));
    }

    [Test]
    public async Task LoadAudioCommandLoadsPickedFile()
    {
        var pb = new FakePlayback();
        var picker = new FakeFilePicker { NextAudioResult = "/path/to/track.flac" };
        var vm = new ViewModelMain(pb, picker, new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        await vm.LoadAudioCommand.ExecuteAsync(null);
        Assert.That(picker.AudioCalls, Is.EqualTo(1));
        Assert.That(pb.LastLoadedAudio, Is.EqualTo("/path/to/track.flac"));
    }

    [Test]
    public async Task LoadAudioCommandIgnoresCancelledPicker()
    {
        var pb = new FakePlayback();
        var picker = new FakeFilePicker { NextAudioResult = null };
        var recents = new FakeRecentFiles();
        var vm = new ViewModelMain(pb, picker, recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        await vm.LoadAudioCommand.ExecuteAsync(null);
        Assert.That(picker.AudioCalls, Is.EqualTo(1));
        Assert.That(pb.LastLoadedAudio, Is.Null);
        Assert.That(recents.RecordedPaths, Is.Empty);
    }

    [Test]
    public async Task LoadSubtitleCommandLoadsPickedFile()
    {
        var pb = new FakePlayback();
        var picker = new FakeFilePicker { NextSubtitleResult = "/path/to/track.srt" };
        var vm = new ViewModelMain(pb, picker, new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        await vm.LoadSubtitleCommand.ExecuteAsync(null);
        Assert.That(picker.SubtitleCalls, Is.EqualTo(1));
        Assert.That(pb.LastLoadedSubtitle, Is.EqualTo("/path/to/track.srt"));
    }

    [Test]
    public async Task LoadSubtitleCommandIgnoresCancelledPicker()
    {
        var pb = new FakePlayback();
        var picker = new FakeFilePicker { NextSubtitleResult = null };
        var recents = new FakeRecentFiles();
        var vm = new ViewModelMain(pb, picker, recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        await vm.LoadSubtitleCommand.ExecuteAsync(null);
        Assert.That(picker.SubtitleCalls, Is.EqualTo(1));
        Assert.That(pb.LastLoadedSubtitle, Is.Null);
        // Subtitles aren't user-opened media; cancellation must not record into recents.
        Assert.That(recents.RecordedPaths, Is.Empty);
    }

    // --- Per-directory track preferences: save-on-explicit-choice + apply-on-load ---

    private static string LocalPathInTemp(string filename)
    {
        // Build a path that survives TryGetDirectoryKey (i.e., a real local-filesystem path with a real parent dir).
        var dir = Path.Combine(Path.GetTempPath(), "vompl-vm-prefs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, filename);
    }

    [Test]
    public async Task OpenCommandFromPickerEnablesPreferencePersistence()
    {
        // Regression: the picker's OpenAsync used to bypass OpenFile and load the path directly, leaving currentDirectoryKey unset. As a result, every subsequent SelectXxx hit the SaveTrackPreference early-return (null directory key) and nothing was saved. Drag-and-drop and command-line invocation went through OpenFile and worked fine, but the menu's File → Open… item silently failed to persist anything.
        var pb = new FakePlayback();
        var prefs = new FakeTrackPreferences();
        var path = LocalPathInTemp("movie.mkv");
        var picker = new FakeFilePicker { NextResult = path };
        var vm = new ViewModelMain(pb, picker, new FakeRecentFiles(), prefs, new FakeUrlDownloader(), new FakeUrlPrompt());
        try
        {
            await vm.OpenCommand.ExecuteAsync(null);
            // Set tracks now that the VM has subscribed to the playback mirror.
            pb.AudioTracks = new[] { new MediaTrack(7, "English", "eng", false, null) };

            vm.SelectAudio(7);

            Assert.That(prefs.RecordedPrefs, Has.Count.EqualTo(1));
            Assert.That(prefs.RecordedPrefs[0].Dir, Is.EqualTo(Path.GetDirectoryName(path)));
            Assert.That(prefs.RecordedPrefs[0].Pref.Title, Is.EqualTo("English"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void SelectAudioRecordsPreferenceForLocalFile()
    {
        var pb = new FakePlayback();
        var prefs = new FakeTrackPreferences();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs, new FakeUrlDownloader(), new FakeUrlPrompt());
        // Set tracks AFTER VM construction so the PropertyChanged → mirror flow runs and vm.AudioTracks reflects the test's setup.
        pb.AudioTracks = new[]
        {
            new MediaTrack(1, "First", "eng", false, null),
            new MediaTrack(7, "Commentary", "eng", true, "commentary.ac3"),
        };
        var path = LocalPathInTemp("movie.mkv");
        var dir = Path.GetDirectoryName(path);
        try
        {
            vm.OpenFile(path);
            vm.SelectAudio(7);

            Assert.That(prefs.RecordedPrefs, Has.Count.EqualTo(1));
            var (recordedDir, kind, pref) = prefs.RecordedPrefs[0];
            Assert.That(recordedDir, Is.EqualTo(dir));
            Assert.That(kind, Is.EqualTo(MediaKind.Audio));
            Assert.That(pref.IsNone, Is.False);
            Assert.That(pref.Title, Is.EqualTo("Commentary"));
            Assert.That(pref.Lang, Is.EqualTo("eng"));
            Assert.That(pref.External, Is.True);
            Assert.That(pref.ExternalFilename, Is.EqualTo("commentary.ac3"));
            Assert.That(pref.IndexInKind, Is.EqualTo(1));

            // The forwarding to playback still happens.
            Assert.That(pb.AudioSelections, Is.EqualTo(new int?[] { 7 }));
        }
        finally
        {
            Directory.Delete(dir!, recursive: true);
        }
    }

    [Test]
    public void SelectSubtitleNullRecordsIsNonePreference()
    {
        var pb = new FakePlayback();
        var prefs = new FakeTrackPreferences();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs, new FakeUrlDownloader(), new FakeUrlPrompt());
        pb.SubtitleTracks = new[] { new MediaTrack(1, "English", "eng", false, null) };
        var path = LocalPathInTemp("show.mkv");
        try
        {
            vm.OpenFile(path);
            vm.SelectSubtitle(null);

            Assert.That(prefs.RecordedPrefs, Has.Count.EqualTo(1));
            Assert.That(prefs.RecordedPrefs[0].Pref.IsNone, Is.True);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void SelectIsNoopForRecordingWhenSourceIsUri()
    {
        // URI sources have no useful directory key; preferences must not be saved (and the playback call must still happen so the user's pick takes effect for the current session).
        var pb = new FakePlayback();
        var prefs = new FakeTrackPreferences();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs, new FakeUrlDownloader(), new FakeUrlPrompt());
        pb.AudioTracks = new[] { new MediaTrack(1, "Foo", "eng", false, null) };
        vm.OpenFile("https://example.com/stream.m3u8");

        vm.SelectAudio(1);

        Assert.That(prefs.RecordedPrefs, Is.Empty);
        Assert.That(pb.AudioSelections, Is.EqualTo(new int?[] { 1 }));
    }

    [Test]
    public void SelectIsNoopForRecordingWhenChosenIdNotInTracklist()
    {
        var pb = new FakePlayback();
        var prefs = new FakeTrackPreferences();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs, new FakeUrlDownloader(), new FakeUrlPrompt());
        pb.AudioTracks = new[] { new MediaTrack(1, "Foo", "eng", false, null) };
        var path = LocalPathInTemp("movie.mkv");
        try
        {
            vm.OpenFile(path);
            // Race: user clicks track 99 but the list has changed since the menu was rendered. Skip the save.
            vm.SelectAudio(99);

            Assert.That(prefs.RecordedPrefs, Is.Empty);
            // playback still receives the call — let mpv decide what to do with an unknown id.
            Assert.That(pb.AudioSelections, Is.EqualTo(new int?[] { 99 }));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void ApplyHappensOnFileLoaded()
    {
        // mpv fires `track-list/count` (and we marshal it as TracksReloaded) BEFORE FileLoaded — the lists are populated by the time FileLoaded lands. So apply fires from FileLoaded itself, not from a subsequent TracksReloaded. Verified empirically against real mpv via VOMPL_LOG_PREFS traces.
        var pb = new FakePlayback();
        var prefs = new FakeTrackPreferences();
        var path = LocalPathInTemp("movie.mkv");
        var dir = Path.GetDirectoryName(path)!;
        prefs.Stored[(dir, MediaKind.Audio)] = new TrackPreference(false, null, "fre", false, null, null);
        prefs.Stored[(dir, MediaKind.Subtitle)] = new TrackPreference(true, null, null, false, null, null);

        try
        {
            var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs, new FakeUrlDownloader(), new FakeUrlPrompt());
            // Tracks set after VM construction so the PropertyChanged → mirror path runs and ApplyTrackPreferences sees them via vm.AudioTracks etc. In real mpv, the TracksReloaded fire that produces the populated lists happens before FileLoaded — so by the time FileLoaded fires, mirrors are populated.
            pb.AudioTracks = new[] { new MediaTrack(11, "English", "eng", false, null), new MediaTrack(12, "French", "fre", false, null) };
            pb.SubtitleTracks = new[] { new MediaTrack(21, "English", "eng", false, null) };
            vm.OpenFile(path);

            // Before FileLoaded fires, no apply.
            Assert.That(pb.AudioSelections, Is.Empty);

            pb.RaiseFileLoaded();
            // Apply runs immediately: French audio (lang match) → 12; subtitles → null (IsNone).
            Assert.That(pb.AudioSelections, Is.EqualTo(new int?[] { 12 }));
            Assert.That(pb.SubtitleSelections, Is.EqualTo(new int?[] { null }));

            // A subsequent TracksReloaded must NOT re-apply (e.g., the user adds a sub via Add Subtitle File later in the session). The per-kind applied gate enforces this.
            pb.RaiseTracksReloaded();
            Assert.That(pb.AudioSelections, Has.Count.EqualTo(1));
            Assert.That(pb.SubtitleSelections, Has.Count.EqualTo(1));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void ApplyDoesNotReSavePreference()
    {
        // The apply path goes through playback.SetXxx directly, bypassing vm.SelectXxx — otherwise an immediate read-back-and-save loop would constantly rewrite the same row on every file load. Verify by counting Record calls before and after the apply.
        var pb = new FakePlayback();
        var prefs = new FakeTrackPreferences();
        var path = LocalPathInTemp("movie.mkv");
        var dir = Path.GetDirectoryName(path)!;
        prefs.Stored[(dir, MediaKind.Audio)] = new TrackPreference(false, null, "eng", false, null, null);
        try
        {
            var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs, new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.AudioTracks = new[] { new MediaTrack(1, null, "eng", false, null) };
            vm.OpenFile(path);
            pb.RaiseFileLoaded();
            pb.RaiseTracksReloaded();

            // playback got its SetAudio call for the matched track…
            Assert.That(pb.AudioSelections, Is.EqualTo(new int?[] { 1 }));
            // …but Record was NOT called from the apply path.
            Assert.That(prefs.RecordedPrefs, Is.Empty);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void ApplySkipsWhenNoPreferenceStored()
    {
        var pb = new FakePlayback();
        var prefs = new FakeTrackPreferences();
        var path = LocalPathInTemp("movie.mkv");
        try
        {
            var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs, new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.AudioTracks = new[] { new MediaTrack(1, null, "eng", false, null) };
            vm.OpenFile(path);
            pb.RaiseFileLoaded();
            pb.RaiseTracksReloaded();
            Assert.That(pb.AudioSelections, Is.Empty);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void ApplyRetriesPerKindUntilEachSucceeds()
    {
        // Realistic scenario: subtitle preference is for an external sub mpv hasn't auto-loaded yet. First TracksReloaded only has the embedded set (no match for sub). A second TracksReloaded later includes the lazy external sub. The retry loop applies it.
        var pb = new FakePlayback();
        var prefs = new FakeTrackPreferences();
        var path = LocalPathInTemp("movie.mkv");
        var dir = Path.GetDirectoryName(path)!;
        prefs.Stored[(dir, MediaKind.Subtitle)] = new TrackPreference(false, "Forced", null, false, null, null);
        prefs.Stored[(dir, MediaKind.Audio)] = new TrackPreference(false, null, "eng", false, null, null);
        try
        {
            var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs, new FakeUrlDownloader(), new FakeUrlPrompt());
            // Initial track-list: matches audio (eng) but NOT subtitle (no track titled "Forced").
            pb.AudioTracks = new[] { new MediaTrack(11, null, "eng", false, null) };
            pb.SubtitleTracks = new[] { new MediaTrack(21, "English", "eng", false, null) };
            vm.OpenFile(path);

            pb.RaiseFileLoaded();
            pb.RaiseTracksReloaded();
            // Audio applied (matched by lang). Subtitle did NOT apply (no "Forced" title in the list).
            Assert.That(pb.AudioSelections, Is.EqualTo(new int?[] { 11 }));
            Assert.That(pb.SubtitleSelections, Is.Empty);

            // Second TracksReloaded — mpv has now lazy-loaded the forced-subs sidecar.
            pb.SubtitleTracks = new[]
            {
                new MediaTrack(21, "English", "eng", false, null),
                new MediaTrack(22, "Forced", "eng", true, "forced.srt"),
            };
            pb.RaiseTracksReloaded();
            // Audio is NOT re-applied (already done on the first pass).
            Assert.That(pb.AudioSelections, Is.EqualTo(new int?[] { 11 }));
            // Subtitle IS now applied (the matching track appeared).
            Assert.That(pb.SubtitleSelections, Is.EqualTo(new int?[] { 22 }));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void ApplyDoesNotReFireOnPostApplyTracksReloaded()
    {
        // Once a kind has been applied, a later TracksReloaded (e.g., user adds an external sub via menu) must NOT re-apply the same preference and clobber the user's just-added sub.
        var pb = new FakePlayback();
        var prefs = new FakeTrackPreferences();
        var path = LocalPathInTemp("movie.mkv");
        var dir = Path.GetDirectoryName(path)!;
        prefs.Stored[(dir, MediaKind.Subtitle)] = new TrackPreference(false, null, "eng", false, null, null);
        try
        {
            var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs, new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.SubtitleTracks = new[] { new MediaTrack(21, null, "eng", false, null) };
            vm.OpenFile(path);
            pb.RaiseFileLoaded();
            pb.RaiseTracksReloaded();

            Assert.That(pb.SubtitleSelections, Is.EqualTo(new int?[] { 21 }));

            // Simulate user adding an external sub: track-list grows, TracksReloaded fires again.
            pb.SubtitleTracks = new[]
            {
                new MediaTrack(21, null, "eng", false, null),
                new MediaTrack(22, "External", "eng", true, "added.srt"),
            };
            pb.RaiseTracksReloaded();

            // Critical: NO re-apply. Subtitle stays at the user's now-current pick (which would be 22 in the real flow; the fake doesn't auto-select).
            Assert.That(pb.SubtitleSelections, Is.EqualTo(new int?[] { 21 }));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void ApplySkipsForUriSources()
    {
        var pb = new FakePlayback();
        var prefs = new FakeTrackPreferences();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs, new FakeUrlDownloader(), new FakeUrlPrompt());
        pb.AudioTracks = new[] { new MediaTrack(1, null, "eng", false, null) };
        vm.OpenFile("https://example.com/stream.m3u8");
        pb.RaiseFileLoaded();
        pb.RaiseTracksReloaded();
        Assert.That(pb.AudioSelections, Is.Empty);
        Assert.That(prefs.GetCalls, Is.Empty);
    }

    // --- Per-file play position: save triggers + apply on reload ---

    [Test]
    public void OpenFileWithSavedPositionSeeksAfterFileLoaded()
    {
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var path = LocalPathInTemp("movie.mkv");
        recents.Positions[path] = 60;
        try
        {
            var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.DurationSeconds = 600;
            vm.OpenFile(path);
            // Apply happens on FileLoaded (which is when duration is reliably populated in the real flow), not on OpenFile itself.
            Assert.That(pb.LastSeekSeconds, Is.Null);
            pb.RaiseFileLoaded();
            Assert.That(pb.LastSeekSeconds, Is.EqualTo(60));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void OpenFileWithSavedPositionAppliesWhenDurationArrivesAfterFileLoaded()
    {
        // mpv's emission order between MPV_EVENT_FILE_LOADED and the synthesized property-change for `duration` is not contractually guaranteed: track lists land before FileLoaded (per the existing comment at OnPlaybackFileLoaded), but duration may land after on some versions / formats. The apply must work regardless of which arrives first.
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var path = LocalPathInTemp("movie.mkv");
        recents.Positions[path] = 60;
        try
        {
            var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
            // Simulate "FileLoaded fires before duration is known" — DurationSeconds defaults to 0 on FakePlayback.
            vm.OpenFile(path);
            pb.RaiseFileLoaded();
            Assert.That(pb.LastSeekSeconds, Is.Null, "duration not yet known, no seek possible");

            // Now duration arrives.
            pb.DurationSeconds = 600;
            Assert.That(pb.LastSeekSeconds, Is.EqualTo(60), "apply must retry once duration is known");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void OpenFileWithoutSavedPositionDoesNotSeek()
    {
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var path = LocalPathInTemp("movie.mkv");
        try
        {
            var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.DurationSeconds = 600;
            vm.OpenFile(path);
            pb.RaiseFileLoaded();
            Assert.That(pb.LastSeekSeconds, Is.Null);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void OpenFileWithSavedPositionPastDurationDoesNotSeek()
    {
        // Saved position is within 5 seconds of the end — ignore it and start fresh. Same near-end filter the save side uses; symmetric.
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var path = LocalPathInTemp("movie.mkv");
        recents.Positions[path] = 597;
        try
        {
            var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.DurationSeconds = 600;
            vm.OpenFile(path);
            pb.RaiseFileLoaded();
            Assert.That(pb.LastSeekSeconds, Is.Null);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void OpenFileWithSavedPositionZeroDoesNotSeek()
    {
        // Don't waste a seek round-trip when the saved position is the natural start position.
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var path = LocalPathInTemp("movie.mkv");
        recents.Positions[path] = 0;
        try
        {
            var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.DurationSeconds = 600;
            vm.OpenFile(path);
            pb.RaiseFileLoaded();
            Assert.That(pb.LastSeekSeconds, Is.Null);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void OpenFileSavesOutgoingFilesPosition()
    {
        // The "user moved on from file A to file B" case. Save A's position synchronously inside OpenFile(B), before currentFilePath is overwritten. Replaces the FileEnded subscription idea — see plan notes about the broken evt.Error reason field and the EOF/keep-open=yes incoherence.
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var pathA = LocalPathInTemp("a.mkv");
        var pathB = LocalPathInTemp("b.mkv");
        try
        {
            var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.DurationSeconds = 600;
            vm.OpenFile(pathA);
            pb.RaiseFileLoaded();
            pb.PositionSeconds = 60;
            // Default IsPaused=true means the periodic save is gated off; only the outgoing-file save should appear when we switch to B.
            vm.OpenFile(pathB);
            Assert.That(recents.RecordedPositions, Has.Count.EqualTo(1));
            Assert.That(recents.RecordedPositions[0].Path, Is.EqualTo(pathA));
            Assert.That(recents.RecordedPositions[0].Position, Is.EqualTo(60));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(pathA)!, recursive: true);
            Directory.Delete(Path.GetDirectoryName(pathB)!, recursive: true);
        }
    }

    [Test]
    public void PauseTransitionSavesPosition()
    {
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var path = LocalPathInTemp("movie.mkv");
        try
        {
            var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.DurationSeconds = 600;
            pb.IsPaused = false;
            vm.OpenFile(path);
            pb.RaiseFileLoaded();
            pb.PositionSeconds = 30;
            // The position bump fires a periodic save (|30-0|>=10 with IsPaused=false). Drop it so the assertion isolates the pause-edge save.
            recents.RecordedPositions.Clear();

            pb.IsPaused = true;
            Assert.That(recents.RecordedPositions, Has.Count.EqualTo(1));
            Assert.That(recents.RecordedPositions[0].Path, Is.EqualTo(path));
            Assert.That(recents.RecordedPositions[0].Position, Is.EqualTo(30));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void UnpausingDoesNotSavePosition()
    {
        // Only the rising edge (play→pause) saves; the falling edge (pause→play) carries no new information that the periodic save won't catch later.
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var path = LocalPathInTemp("movie.mkv");
        try
        {
            var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.DurationSeconds = 600;
            pb.IsPaused = true;
            vm.OpenFile(path);
            pb.RaiseFileLoaded();

            pb.IsPaused = false;
            Assert.That(recents.RecordedPositions, Is.Empty);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void PeriodicSaveDuringPlaybackFiresEveryTenSeconds()
    {
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var path = LocalPathInTemp("movie.mkv");
        try
        {
            var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.DurationSeconds = 600;
            pb.IsPaused = false;
            vm.OpenFile(path);
            pb.RaiseFileLoaded();

            for (int i = 1; i <= 25; i++)
            {
                pb.PositionSeconds = i;
            }
            // Saves expected at 10 (|10-0|=10) and 20 (|20-10|=10). 25 is only +5 since the last save.
            Assert.That(recents.RecordedPositions.Select(r => r.Position), Is.EqualTo(new[] { 10.0, 20.0 }));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void PeriodicSaveWhilePausedDoesNotFire()
    {
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var path = LocalPathInTemp("movie.mkv");
        try
        {
            var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.DurationSeconds = 600;
            pb.IsPaused = true;
            vm.OpenFile(path);
            pb.RaiseFileLoaded();

            for (int i = 1; i <= 25; i++)
            {
                pb.PositionSeconds = i;
            }
            Assert.That(recents.RecordedPositions, Is.Empty);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void PositionNearEndIsNotSaved()
    {
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var path = LocalPathInTemp("movie.mkv");
        try
        {
            var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.DurationSeconds = 600;
            vm.OpenFile(path);
            pb.RaiseFileLoaded();
            pb.PositionSeconds = 599;
            recents.RecordedPositions.Clear();
            vm.Dispose();
            Assert.That(recents.RecordedPositions, Is.Empty);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void PeriodicSaveNearEndDoesNotFire()
    {
        // Companion to PositionNearEndIsNotSaved: same near-end filter must elide the periodic-save trigger too, not just the dispose-time save. Important for the EOF-with-keep-open=yes case where mpv keeps firing time-pos at duration with IsPaused=false.
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var path = LocalPathInTemp("movie.mkv");
        try
        {
            var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.DurationSeconds = 600;
            pb.IsPaused = false;
            vm.OpenFile(path);
            pb.RaiseFileLoaded();

            // Walk position from 595 to 600 in 1s steps — all within the (duration - NearEndIgnoreSeconds = 595) skip threshold. Each tick fires PropertyChanged; periodic save would normally trigger (delta from lastSaved=0 is well over 10), but the near-end filter must skip every one of them.
            for (int i = 595; i <= 600; i++)
            {
                pb.PositionSeconds = i;
            }
            Assert.That(recents.RecordedPositions, Is.Empty);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void DisposeSavesFinalPosition()
    {
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var path = LocalPathInTemp("movie.mkv");
        try
        {
            var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.DurationSeconds = 600;
            pb.IsPaused = false;
            vm.OpenFile(path);
            pb.RaiseFileLoaded();
            pb.PositionSeconds = 30;
            recents.RecordedPositions.Clear();
            vm.Dispose();
            Assert.That(recents.RecordedPositions, Has.Count.EqualTo(1));
            Assert.That(recents.RecordedPositions[0].Position, Is.EqualTo(30));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void VolumeMirrorsPlayback()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        pb.Volume = 42;
        Assert.That(vm.Volume, Is.EqualTo(42));
    }

    [Test]
    public void IsMutedMirrorsPlayback()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        pb.IsMuted = true;
        Assert.That(vm.IsMuted, Is.True);
    }

    [Test]
    public void SetVolumeForwardsToPlayback()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        vm.SetVolume(75);
        Assert.That(pb.VolumeWrites, Is.EqualTo(new[] { 75.0 }));
    }

    [Test]
    public void AdjustVolumeForwardsToPlayback()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        vm.AdjustVolume(-5);
        vm.AdjustVolume(10);
        Assert.That(pb.VolumeAdjustments, Is.EqualTo(new[] { -5.0, 10.0 }));
    }

    [Test]
    public void ToggleMuteForwardsToPlayback()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        vm.ToggleMute();
        vm.ToggleMute();
        Assert.That(pb.ToggleMuteCalls, Is.EqualTo(2));
    }

    [Test]
    public void UriSourceDoesNotPersistPosition()
    {
        // URIs (http/smb/…) skip both the apply lookup AND every save trigger. Position persistence is for local paths only.
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        pb.DurationSeconds = 600;
        pb.IsPaused = false;
        vm.OpenFile("https://example.com/stream.m3u8");
        pb.RaiseFileLoaded();
        pb.PositionSeconds = 50;
        pb.IsPaused = true;
        vm.Dispose();
        Assert.That(recents.GetPositionCalls, Is.Empty);
        Assert.That(recents.RecordedPositions, Is.Empty);
    }

    // --- Playlist: LoadPaths, PlayPlaylistItem, OpenFile back-compat ---

    [Test]
    public void OpenFilePublicReplacesPlaylistWithSingleItem()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        vm.OpenFile("/path/to/a.mp4");
        Assert.That(vm.Playlist.Items, Is.EqualTo(new[] { "/path/to/a.mp4" }));
        Assert.That(vm.Playlist.CurrentIndex, Is.EqualTo(0));
        Assert.That(pb.LastLoadedFile, Is.EqualTo("/path/to/a.mp4"));

        vm.OpenFile("/path/to/b.mp4");
        Assert.That(vm.Playlist.Items, Is.EqualTo(new[] { "/path/to/b.mp4" }));
        Assert.That(pb.LastLoadedFile, Is.EqualTo("/path/to/b.mp4"));
    }

    [Test]
    public void LoadPathsReplaceLoadsFirstItem()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        vm.LoadPaths(new[] { "/a", "/b", "/c" }, replace: true);
        Assert.That(vm.Playlist.Items, Is.EqualTo(new[] { "/a", "/b", "/c" }));
        Assert.That(vm.Playlist.CurrentIndex, Is.EqualTo(0));
        Assert.That(pb.LoadFileCalls, Is.EqualTo(1));
        Assert.That(pb.LastLoadedFile, Is.EqualTo("/a"));
    }

    [Test]
    public void LoadPathsReplaceWithEmptyDoesNothing()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        vm.LoadPaths(new[] { "/a" }, replace: true);
        Assert.That(pb.LoadFileCalls, Is.EqualTo(1));

        vm.LoadPaths(Array.Empty<string>(), replace: true);
        // Empty input is a no-op — does not orphan the currently-playing item.
        Assert.That(vm.Playlist.Items, Is.EqualTo(new[] { "/a" }));
        Assert.That(pb.LoadFileCalls, Is.EqualTo(1));
    }

    [Test]
    public void LoadPathsAppendToEmptyKicksOffPlayback()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        vm.LoadPaths(new[] { "/a", "/b" }, replace: false);
        Assert.That(vm.Playlist.Items, Is.EqualTo(new[] { "/a", "/b" }));
        Assert.That(vm.Playlist.CurrentIndex, Is.EqualTo(0));
        Assert.That(pb.LastLoadedFile, Is.EqualTo("/a"));
    }

    [Test]
    public void LoadPathsAppendToNonEmptyDoesNotChangeCurrent()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        vm.LoadPaths(new[] { "/a", "/b" }, replace: true);
        pb.LoadFileCalls = 0;
        pb.LastLoadedFile = null;

        vm.LoadPaths(new[] { "/c", "/d" }, replace: false);
        Assert.That(vm.Playlist.Items, Is.EqualTo(new[] { "/a", "/b", "/c", "/d" }));
        Assert.That(vm.Playlist.CurrentIndex, Is.EqualTo(0));
        // No new load fired — current item keeps playing.
        Assert.That(pb.LoadFileCalls, Is.EqualTo(0));
    }

    [Test]
    public void PlayPlaylistItemSwitchesCurrent()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        vm.LoadPaths(new[] { "/a", "/b", "/c" }, replace: true);
        pb.LoadFileCalls = 0;
        pb.LastLoadedFile = null;

        vm.PlayPlaylistItem(2);
        Assert.That(vm.Playlist.CurrentIndex, Is.EqualTo(2));
        Assert.That(pb.LoadFileCalls, Is.EqualTo(1));
        Assert.That(pb.LastLoadedFile, Is.EqualTo("/c"));
    }

    // --- Playlist: auto-advance ---

    [Test]
    public void EofReachedRisingEdgeAdvances()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        vm.LoadPaths(new[] { "/a", "/b" }, replace: true);
        pb.RaiseFileLoaded();
        pb.DurationSeconds = 60;
        pb.LastLoadedFile = null;

        pb.IsEofReached = true;

        Assert.That(vm.Playlist.CurrentIndex, Is.EqualTo(1));
        Assert.That(pb.LastLoadedFile, Is.EqualTo("/b"));
    }

    [Test]
    public void EofReachedRisingEdgeDoesNotAdvanceWhilePendingLoad()
    {
        // The currentFileLoaded gate. A stale eof-reached=true from the prior file's tail can land in the dispatcher queue after LoadFile dispatches but before the new file's FileLoaded fires. Without the gate, the spurious rising edge would skip the just-loaded file.
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        vm.LoadPaths(new[] { "/a", "/b" }, replace: true);
        // FileLoaded NOT raised — currentFileLoaded is still false.
        pb.DurationSeconds = 60;

        pb.IsEofReached = true;

        Assert.That(vm.Playlist.CurrentIndex, Is.EqualTo(0));
        Assert.That(pb.LastLoadedFile, Is.EqualTo("/a"));
    }

    [Test]
    public void EofReachedRisingEdgeDoesNotAdvanceForLiveStream()
    {
        // The DurationSeconds > 0 gate. Live streams may oscillate eof-reached without a meaningful "next item" semantic.
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        vm.LoadPaths(new[] { "https://example.com/live", "/b" }, replace: true);
        pb.RaiseFileLoaded();
        // DurationSeconds remains 0 — live source.

        pb.IsEofReached = true;

        Assert.That(vm.Playlist.CurrentIndex, Is.EqualTo(0));
    }

    [Test]
    public void EofReachedAtLastItemDoesNotAdvance()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        vm.LoadPaths(new[] { "/a" }, replace: true);
        pb.RaiseFileLoaded();
        pb.DurationSeconds = 60;
        pb.LastLoadedFile = null;
        int loadsBeforeEof = pb.LoadFileCalls;

        pb.IsEofReached = true;

        Assert.That(pb.LoadFileCalls, Is.EqualTo(loadsBeforeEof));
        Assert.That(vm.Playlist.CurrentIndex, Is.EqualTo(0));
    }

    [Test]
    public void EofReachedNoOpsOutsidePlaylist()
    {
        // No LoadPaths call yet — playlist is empty. EOF on whatever happens to be playing (shouldn't happen in practice; defense in depth).
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        pb.DurationSeconds = 60;

        pb.IsEofReached = true;

        Assert.That(vm.Playlist.Items, Is.Empty);
        Assert.That(pb.LoadFileCalls, Is.EqualTo(0));
    }

    [Test]
    public void EofReachedDoesNotReFireOnRepeatTrue()
    {
        // The rising-edge gate. User seeks back from EOF and re-hits it without intervening load — should not re-advance.
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        vm.LoadPaths(new[] { "/a", "/b", "/c" }, replace: true);
        pb.RaiseFileLoaded();
        pb.DurationSeconds = 60;

        pb.IsEofReached = true;     // advances to /b
        // Simulate the auto-advance flow: in real life Playback.LoadFile resets IsEofReached=false synchronously, then mpv's eof-reached=false observation arrives. Mirror that.
        pb.IsEofReached = false;
        pb.RaiseFileLoaded();
        // Now simulate a user seeking back into /b and re-hitting EOF without us resetting wasEofReached: the rising edge should fire normally (this is the "user-driven re-advance" case).
        pb.IsEofReached = true;     // /b → /c

        Assert.That(vm.Playlist.CurrentIndex, Is.EqualTo(2));
        Assert.That(pb.LastLoadedFile, Is.EqualTo("/c"));
    }

    [Test]
    public void LastItemEofThenUserClicksRowDoesNotSpuriouslyAdvance()
    {
        // Sequence: last item ends (auto-advance returns null, no load), user double-clicks an earlier row. The intervening LoadCurrentItem must reset wasEofReached so the row's eventual EOF triggers a real advance, but the row click itself must NOT auto-advance past whatever it landed on.
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        vm.LoadPaths(new[] { "/a", "/b", "/c" }, replace: true);
        pb.RaiseFileLoaded();
        pb.DurationSeconds = 60;

        // Walk through to the last item and EOF.
        pb.IsEofReached = true;     // → /b
        pb.IsEofReached = false;
        pb.RaiseFileLoaded();
        pb.IsEofReached = true;     // → /c
        pb.IsEofReached = false;
        pb.RaiseFileLoaded();
        pb.IsEofReached = true;     // last item; no advance
        Assert.That(vm.Playlist.CurrentIndex, Is.EqualTo(2));

        // User clicks row 0 from the panel.
        pb.LoadFileCalls = 0;
        vm.PlayPlaylistItem(0);
        Assert.That(vm.Playlist.CurrentIndex, Is.EqualTo(0));
        Assert.That(pb.LoadFileCalls, Is.EqualTo(1));
        // No spurious auto-advance from the lingering eof state.
        Assert.That(pb.LastLoadedFile, Is.EqualTo("/a"));
    }

    [Test]
    public async Task AutoAdvanceIntoUriItemWorks()
    {
        // The next item is a URI; auto-advance shouldn't bail just because the entry isn't a local-fs path. The IsLocalFilesystemPath gate inside LoadCurrentItem only affects the recents-position lookup, not the load itself. With ClassifyResult=MpvDirect (matches a real HLS m3u8 that yt-dlp's generic extractor handles), the loaded path is the URL itself.
        var pb = new FakePlayback();
        var dl = new FakeUrlDownloader { ClassifyResult = Vomplayer.Services.UrlLoadKind.MpvDirect };
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, new FakeUrlPrompt());
        vm.LoadPaths(new[] { "/local.mkv", "https://example.com/stream.m3u8" }, replace: true);
        pb.RaiseFileLoaded();
        pb.DurationSeconds = 60;

        pb.IsEofReached = true;
        await Task.Yield();
        await Task.Delay(10);

        Assert.That(vm.Playlist.CurrentIndex, Is.EqualTo(1));
        Assert.That(pb.LastLoadedFile, Is.EqualTo("https://example.com/stream.m3u8"));
    }

    [Test]
    public void AutoAdvanceElidesNearEndOutgoingSave()
    {
        // Pin the no-save-on-natural-EOF behavior. At natural EOF with keep-open=yes, position parks at duration; SaveCurrentPositionIfEligible's near-end gate (position >= duration - 5) elides the save. If a future change inverts the gate, this test catches the regression.
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var path = LocalPathInTemp("a.mkv");
        var pathB = LocalPathInTemp("b.mkv");
        try
        {
            var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
            vm.LoadPaths(new[] { path, pathB }, replace: true);
            pb.RaiseFileLoaded();
            pb.DurationSeconds = 600;
            pb.PositionSeconds = 599;   // within the near-end window
            recents.RecordedPositions.Clear();

            pb.IsEofReached = true;     // auto-advance triggers SaveCurrentPositionIfEligible for the outgoing file

            Assert.That(recents.RecordedPositions, Is.Empty);
            Assert.That(vm.Playlist.CurrentIndex, Is.EqualTo(1));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
            Directory.Delete(Path.GetDirectoryName(pathB)!, recursive: true);
        }
    }

    // ── Open URL flow ─────────────────────────────────────────────────────────

    [Test]
    public async Task OpenUrlShowsErrorWhenYtDlpUnavailable()
    {
        var pb = new FakePlayback();
        var dl = new FakeUrlDownloader { Available = false };
        var prompt = new FakeUrlPrompt();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, prompt);

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.OpenUrlCommand).ExecuteAsync(null);

        Assert.That(prompt.Errors, Has.Count.EqualTo(1));
        Assert.That(prompt.Errors[0].Title, Does.Contain("yt-dlp"));
        Assert.That(prompt.PromptCalls, Is.EqualTo(0), "should not prompt for URL when yt-dlp unavailable");
    }

    [Test]
    public async Task OpenUrlPromptCancelDoesNothing()
    {
        var pb = new FakePlayback();
        var dl = new FakeUrlDownloader();
        var prompt = new FakeUrlPrompt { NextUrl = null };
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, prompt);

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.OpenUrlCommand).ExecuteAsync(null);

        Assert.That(prompt.PromptCalls, Is.EqualTo(1));
        Assert.That(dl.ProbeCalls, Is.Empty);
        Assert.That(vm.Playlist.Items, Is.Empty);
    }

    [Test]
    public async Task OpenUrlSingleVideoLoadsAsSingleItem()
    {
        var pb = new FakePlayback();
        var dl = new FakeUrlDownloader
        {
            NextProbeResult = new[] { "https://youtu.be/abc" },
            DownloadResolver = u => "/cache/abc/video.mp4",
        };
        var prompt = new FakeUrlPrompt { NextUrl = "https://youtu.be/abc" };
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, prompt);

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.OpenUrlCommand).ExecuteAsync(null);

        Assert.That(vm.Playlist.Items, Is.EquivalentTo(new[] { "https://youtu.be/abc" }));
        // The download should have been kicked off by LoadCurrentItem.
        Assert.That(dl.DownloadCalls, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task OpenUrlPlaylistPopulatesWithoutAutoplay()
    {
        var pb = new FakePlayback();
        var dl = new FakeUrlDownloader
        {
            NextProbeResult = new[] { "https://youtu.be/a", "https://youtu.be/b", "https://youtu.be/c" },
            DownloadResolver = u => $"/cache/{u}.mp4",
        };
        var prompt = new FakeUrlPrompt { NextUrl = "https://youtube.com/playlist?list=XYZ" };
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, prompt);

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.OpenUrlCommand).ExecuteAsync(null);

        Assert.That(vm.Playlist.Items, Has.Count.EqualTo(3));
        Assert.That(vm.Playlist.CurrentIndex, Is.EqualTo(0));
        // Multi-entry OpenUrl populates the playlist but does NOT auto-download — the user picks a row to start. (Single-video URLs still autoplay; see OpenUrlSingleVideoLoadsAsSingleItem.)
        Assert.That(dl.DownloadCalls, Is.Empty);
        Assert.That(pb.LoadedFiles, Is.Empty);
    }

    [Test]
    public async Task OpenUrlPlaylistDownloadsOnlyAfterRowClick()
    {
        var pb = new FakePlayback();
        var dl = new FakeUrlDownloader
        {
            NextProbeResult = new[] { "https://youtu.be/a", "https://youtu.be/b", "https://youtu.be/c" },
            DownloadResolver = u => $"/cache/{u}.mp4",
        };
        var prompt = new FakeUrlPrompt { NextUrl = "https://youtube.com/playlist?list=XYZ" };
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, prompt);

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.OpenUrlCommand).ExecuteAsync(null);
        // User clicks the second row.
        vm.PlayPlaylistItem(1);
        await Task.Yield();
        await Task.Delay(10);

        Assert.That(dl.DownloadCalls, Is.EquivalentTo(new[] { "https://youtu.be/b" }));
        Assert.That(pb.LoadedFiles, Is.EquivalentTo(new[] { "/cache/https://youtu.be/b.mp4" }));
    }

    [Test]
    public async Task OpenUrlProbeFailureSurfacesError()
    {
        var pb = new FakePlayback();
        var dl = new FailingProbeDownloader("upstream parse failed");
        var prompt = new FakeUrlPrompt { NextUrl = "https://something" };
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, prompt);

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.OpenUrlCommand).ExecuteAsync(null);

        Assert.That(prompt.Errors, Has.Count.EqualTo(1));
        Assert.That(prompt.Errors[0].Message, Does.Contain("upstream parse failed"));
        Assert.That(vm.Playlist.Items, Is.Empty);
    }

    [Test]
    public async Task LoadCurrentItemUrlRoutesThroughDownloader()
    {
        // Prove that cache-hit DownloadAsync wires through to playback.LoadFile with the LOCAL path, not the URL.
        var pb = new FakePlayback();
        var dl = new FakeUrlDownloader
        {
            NextProbeResult = new[] { "https://youtu.be/X" },
            DownloadResolver = u => "/local/cache/X.mp4",
        };
        var prompt = new FakeUrlPrompt { NextUrl = "https://youtu.be/X" };
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, prompt);

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.OpenUrlCommand).ExecuteAsync(null);
        // Allow the fire-and-forget LoadUrlAsync to complete its synchronous Task.FromResult path.
        await Task.Yield();
        await Task.Delay(10);

        Assert.That(pb.LoadedFiles, Is.EquivalentTo(new[] { "/local/cache/X.mp4" }));
    }

    [Test]
    public async Task LoadCurrentItemLocalPathIsNotDownloaded()
    {
        // Local-filesystem paths bypass the downloader and go straight to mpv. Routing is by URI shape — a "looks like a scheme://..." string heads to yt-dlp, anything else heads to mpv.
        var pb = new FakePlayback();
        var dl = new FakeUrlDownloader();
        var prompt = new FakeUrlPrompt();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, prompt);

        vm.OpenFile("/some/local/file.mkv");

        Assert.That(dl.DownloadCalls, Is.Empty);
        Assert.That(pb.LoadedFiles, Is.EquivalentTo(new[] { "/some/local/file.mkv" }));
    }

    [Test]
    public async Task RapidLoadCancelsPreviousDownload()
    {
        // Race scenario from the design review: download A is in-flight; user clicks playlist row B. The CTS for A must be cancelled and A's late completion must NOT call playback.LoadFile.
        var pb = new FakePlayback();
        var pendingA = new TaskCompletionSource<string>();
        var dl = new FakeUrlDownloader
        {
            NextProbeResult = new[] { "https://youtu.be/A", "https://youtu.be/B" },
            PendingDownload = pendingA,
        };
        var prompt = new FakeUrlPrompt { NextUrl = "https://youtube.com/playlist?list=Z" };
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, prompt);

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.OpenUrlCommand).ExecuteAsync(null);
        // Multi-entry OpenUrl no longer auto-starts; click row 0 to put A in flight.
        vm.PlayPlaylistItem(0);
        await Task.Yield();
        // Now B is in queue. Swap PendingDownload so B's DownloadAsync resolves synchronously (Task.FromResult path).
        dl.PendingDownload = null;
        dl.DownloadResolver = u => $"/cache/{u}.mp4";

        // Trigger LoadCurrentItem for B.
        vm.PlayPlaylistItem(1);
        await Task.Yield();
        await Task.Delay(10);

        // A's TCS was cancelled by the CTS swap.
        Assert.That(pendingA.Task.IsCanceled, Is.True, "previous download CTS should have been cancelled when LoadCurrentItem started B");
        // Only B's local path made it to playback.
        Assert.That(pb.LoadedFiles, Is.EquivalentTo(new[] { "/cache/https://youtu.be/B.mp4" }));
        // Every status overlay shown (the interactive probe plus A's and B's load phases) was balanced by a dispose — no overlay is left stuck visible when a load is cancelled or superseded.
        Assert.That(prompt.StatusShown, Is.GreaterThanOrEqualTo(2), "A and B each showed a load status");
        Assert.That(prompt.StatusDisposed, Is.EqualTo(prompt.StatusShown), "every status shown was hidden (A cancelled, B succeeded)");
    }

    [Test]
    public async Task FailedDownloadShowsErrorOnlyForActiveLoad()
    {
        // Pin the "ShowError only fires for the active load's failure" guard. Stale downloads that fail after the user moved on should NOT pop up an error dialog.
        var pb = new FakePlayback();
        var pendingA = new TaskCompletionSource<string>();
        var dl = new FakeUrlDownloader
        {
            NextProbeResult = new[] { "https://youtu.be/A", "https://youtu.be/B" },
            PendingDownload = pendingA,
        };
        var prompt = new FakeUrlPrompt { NextUrl = "https://youtube.com/playlist?list=Z" };
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, prompt);

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.OpenUrlCommand).ExecuteAsync(null);
        // Multi-entry OpenUrl no longer auto-starts; click row 0 to put A in flight.
        vm.PlayPlaylistItem(0);
        await Task.Yield();
        // User moves on to B before A completes.
        dl.PendingDownload = null;
        dl.DownloadResolver = u => $"/cache/{u}.mp4";
        vm.PlayPlaylistItem(1);
        await Task.Yield();
        await Task.Delay(10);

        // Now A's pending TCS faults — but A is no longer the active load. ShowError must NOT fire for A.
        pendingA.TrySetException(new InvalidOperationException("A failed late"));
        await Task.Yield();
        await Task.Delay(10);

        // No error dialog should have been shown — only B succeeded, A's late failure is suppressed.
        Assert.That(prompt.Errors, Is.Empty, "stale download failure must not surface an error to the user");
    }

    [Test]
    public async Task LoadOpenFileForUrlRoutesThroughDownloader()
    {
        // Drag-drop / CLI / programmatic OpenFile of a URL routes through the downloader the same way Open-URL does. Pre-fix, this path silently bypassed the cache because urlsRequiringDownload was only populated by OpenUrl; the bypass left mpv to stream via its built-in ytdl-hook with nothing landing in url-downloads.
        var pb = new FakePlayback();
        var dl = new FakeUrlDownloader
        {
            DownloadResolver = u => $"/cache/{u}.mp4",
        };
        var prompt = new FakeUrlPrompt();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, prompt);

        vm.OpenFile("https://youtu.be/from-drag-drop");
        await Task.Yield();
        await Task.Delay(10);

        Assert.That(dl.DownloadCalls, Is.EquivalentTo(new[] { "https://youtu.be/from-drag-drop" }));
        Assert.That(pb.LoadedFiles, Is.EquivalentTo(new[] { "/cache/https://youtu.be/from-drag-drop.mp4" }));
    }

    [Test]
    public async Task RestorePlaylistForUrlRoutesThroughDownloader()
    {
        // Recent-menu / app-startup restore for a URL with a specific extractor (YouTube) routes through the downloader. Pre-fix this bypassed the cache because urlsRequiringDownload was in-memory only; URI-shape gating + per-load classification closes that gap.
        var pb = new FakePlayback();
        var dl = new FakeUrlDownloader
        {
            DownloadResolver = u => $"/cache/{u}.mp4",
        };
        var prompt = new FakeUrlPrompt();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, prompt);

        vm.Primary.RestorePlaylist(new[] { "https://youtu.be/from-recent" }, currentIndex: 0, startPaused: false);
        await Task.Yield();
        await Task.Delay(10);

        Assert.That(dl.ClassifyCalls, Is.EquivalentTo(new[] { "https://youtu.be/from-recent" }));
        Assert.That(dl.DownloadCalls, Is.EquivalentTo(new[] { "https://youtu.be/from-recent" }));
        Assert.That(pb.LoadedFiles, Is.EquivalentTo(new[] { "/cache/https://youtu.be/from-recent.mp4" }));
    }

    [TestCase("dvd://1")]
    [TestCase("bd://")]
    [TestCase("smb://server/share/file.mkv")]
    [TestCase("sftp://user@host/file.mp4")]
    [TestCase("rtsp://host/stream")]
    [TestCase("rtmp://host/live")]
    [TestCase("file:///home/zorba/movie.mp4")]
    [TestCase("ftp://host/file.mp4")]
    [TestCase("magnet:?xt=urn:btih:abc")]
    public async Task LoadCurrentItemMpvNativeSchemeBypassesClassification(string nonHttpUri)
    {
        // mpv-native protocols (dvd, bd, smb, sftp, rtsp, rtmp, file, ftp) and magnet links go straight to mpv. Routing them through yt-dlp's classifier would waste a process spawn and end with "Download failed" — yt-dlp has no extractor for any of these. The URI-shape gate (ShouldProbe = http(s) only) keeps them on the mpv-direct path.
        var pb = new FakePlayback();
        var dl = new FakeUrlDownloader();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, new FakeUrlPrompt());

        vm.OpenFile(nonHttpUri);
        await Task.Yield();
        await Task.Delay(10);

        Assert.That(pb.LastLoadedFile, Is.EqualTo(nonHttpUri));
        Assert.That(dl.ClassifyCalls, Is.Empty, "non-http(s) URIs must skip classification");
        Assert.That(dl.DownloadCalls, Is.Empty);
    }

    [Test]
    public async Task LoadCurrentItemProbeFailureFallsBackToMpvDirect()
    {
        // yt-dlp not on PATH, or any other classify failure — fall back to handing the URL to mpv. mpv's built-in ytdl-hook will surface its own error if the URL really required yt-dlp; a direct-stream URL just plays.
        var pb = new FakePlayback();
        var dl = new FakeUrlDownloader { ClassifyException = new InvalidOperationException("yt-dlp not found") };
        var prompt = new FakeUrlPrompt();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, prompt);

        vm.OpenFile("https://youtu.be/probe-fails");
        await Task.Yield();
        await Task.Delay(10);

        Assert.That(pb.LastLoadedFile, Is.EqualTo("https://youtu.be/probe-fails"), "probe-failed URL should still reach mpv (mpv's own ytdl-hook may handle it)");
        Assert.That(dl.DownloadCalls, Is.Empty);
        Assert.That(prompt.Errors, Is.Empty, "probe failure is silent — mpv's own error path is the user-facing channel");
    }

    private sealed class FailingProbeDownloader : Vomplayer.Services.IUrlDownloader
    {
        private readonly string message;

        public FailingProbeDownloader(string message)
        {
            this.message = message;
        }

        public Task<bool> IsAvailableAsync(CancellationToken ct) { return Task.FromResult(true); }

        public Task<IReadOnlyList<string>> ProbeAsync(string url, CancellationToken ct)
        {
            throw new InvalidOperationException(message);
        }

        public Task<Vomplayer.Services.UrlLoadKind> ClassifyAsync(string url, CancellationToken ct)
        {
            throw new NotImplementedException();
        }

        public Task<string> DownloadAsync(string url, IProgress<Vomplayer.Services.UrlDownloadProgress>? progress, CancellationToken ct)
        {
            throw new NotImplementedException();
        }
    }
}
