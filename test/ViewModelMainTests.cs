using System;
using System.Collections.Generic;
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
        private IReadOnlyList<SubtitleTrack> subtitleTracks = Array.Empty<SubtitleTrack>();

        [ObservableProperty]
        private int? currentSubtitleId;

        public event Action? FileLoaded;
        public event Action<int>? FileEnded;

        public int InitializeCalls { get; private set; }
        public int TogglePauseCalls { get; private set; }
        public int LoadFileCalls { get; private set; }
        public string? LastLoadedFile { get; private set; }
        public double? LastSeekSeconds { get; private set; }
        public string? LastLoadedSubtitle { get; private set; }
        public List<int?> SubtitleSelections { get; } = new();

        public void Initialize()
        {
            InitializeCalls++;
        }

        public void LoadFile(string path)
        {
            LoadFileCalls++;
            LastLoadedFile = path;
        }

        // Real Playback.TogglePause writes to mpv asynchronously — IsPaused only changes when mpv echoes it back via OnMpvPropertyChanged. The fake elides that latency intentionally; tests asserting on rapid-toggle race behavior would need to add an async-ish fake.
        public void TogglePause()
        {
            TogglePauseCalls++;
            IsPaused = !IsPaused;
        }

        public void Seek(double seconds)
        {
            LastSeekSeconds = seconds;
        }

        public void LoadSubtitle(string path)
        {
            LastLoadedSubtitle = path;
        }

        public void SetSubtitle(int? trackId)
        {
            SubtitleSelections.Add(trackId);
        }

        public void RaiseFileLoaded()
        {
            FileLoaded?.Invoke();
        }

        public void RaiseFileEnded(int reason)
        {
            FileEnded?.Invoke(reason);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeFilePicker : IFilePicker
    {
        public string? NextResult { get; set; }
        public string? NextSubtitleResult { get; set; }
        public int Calls { get; private set; }
        public int SubtitleCalls { get; private set; }

        public Task<string?> PickVideoFileAsync(string title)
        {
            Calls++;
            return Task.FromResult(NextResult);
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

        public void Record(string pathOrUri)
        {
            RecordedPaths.Add(pathOrUri);
        }

        public IReadOnlyList<RecentFileEntry> GetMostRecent(int limit)
        {
            return Array.Empty<RecentFileEntry>();
        }
    }

    [Test]
    public void NullPlaybackThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new ViewModelMain(null!, new FakeFilePicker(), new FakeRecentFiles()));
    }

    [Test]
    public void NullFilePickerThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new ViewModelMain(new FakePlayback(), null!, new FakeRecentFiles()));
    }

    [Test]
    public void NullRecentFilesThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new ViewModelMain(new FakePlayback(), new FakeFilePicker(), null!));
    }

    [Test]
    public void PositionMirrorsPlaybackPositionSeconds()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles());
        pb.PositionSeconds = 12.5;
        Assert.That(vm.Position, Is.EqualTo(TimeSpan.FromSeconds(12.5)));
    }

    [Test]
    public void DurationMirrorsPlaybackDurationSeconds()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles());
        pb.DurationSeconds = 60;
        Assert.That(vm.Duration, Is.EqualTo(TimeSpan.FromSeconds(60)));
    }

    [Test]
    public void IsPausedMirrorsPlayback()
    {
        var pb = new FakePlayback { IsPaused = true };
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles());
        pb.IsPaused = false;
        Assert.That(vm.IsPaused, Is.False);
    }

    [Test]
    public void SeekValueTracksPlaybackWhenNotDragging()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles());
        pb.DurationSeconds = 100;
        pb.PositionSeconds = 25;
        Assert.That(vm.SeekValue, Is.EqualTo(0.25));
    }

    [Test]
    public void SeekToSeeksToNormalizedPositionScaledByDuration()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles());
        pb.DurationSeconds = 100;

        vm.SeekTo(0.4);

        Assert.That(pb.LastSeekSeconds, Is.EqualTo(40));
    }

    [Test]
    public void SeekValueTracksPlaybackPosition()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles());
        pb.DurationSeconds = 100;

        pb.PositionSeconds = 75;

        Assert.That(vm.SeekValue, Is.EqualTo(0.75));
    }

    [Test]
    public void PlayPauseCommandTogglesPlayback()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles());
        pb.DurationSeconds = 60;
        vm.PlayPauseCommand.Execute(null);
        Assert.That(pb.TogglePauseCalls, Is.EqualTo(1));
    }

    [Test]
    public void PlayPauseCommandIsNoopBeforeFileLoad()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles());
        vm.PlayPauseCommand.Execute(null);
        Assert.That(pb.TogglePauseCalls, Is.EqualTo(0));
    }

    [Test]
    public async Task OpenCommandLoadsPickedFile()
    {
        var pb = new FakePlayback();
        var picker = new FakeFilePicker { NextResult = "/path/to/video.mp4" };
        var vm = new ViewModelMain(pb, picker, new FakeRecentFiles());
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
        var vm = new ViewModelMain(pb, picker, recents);
        await vm.OpenCommand.ExecuteAsync(null);
        Assert.That(recents.RecordedPaths, Is.EqualTo(new[] { "/path/to/video.mp4" }));
    }

    [Test]
    public async Task OpenCommandIgnoresCancelledPicker()
    {
        var pb = new FakePlayback();
        var picker = new FakeFilePicker { NextResult = null };
        var recents = new FakeRecentFiles();
        var vm = new ViewModelMain(pb, picker, recents);
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
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles());
        vm.OpenFile("/path/to/dropped.mp4");
        Assert.That(pb.LoadFileCalls, Is.EqualTo(1));
        Assert.That(pb.LastLoadedFile, Is.EqualTo("/path/to/dropped.mp4"));
    }

    [Test]
    public void OpenFileRecordsTheTarget()
    {
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), recents);
        vm.OpenFile("/path/to/dropped.mp4");
        Assert.That(recents.RecordedPaths, Is.EqualTo(new[] { "/path/to/dropped.mp4" }));
    }

    [Test]
    public void OpenFileAcceptsRemoteUris()
    {
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), recents);
        vm.OpenFile("https://example.com/stream.m3u8");
        Assert.That(pb.LastLoadedFile, Is.EqualTo("https://example.com/stream.m3u8"));
        Assert.That(recents.RecordedPaths, Is.EqualTo(new[] { "https://example.com/stream.m3u8" }));
    }

    [Test]
    public void OnRenderContextReadyLoadsInitialFileOnce()
    {
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), recents)
        {
            InitialFile = "/path/to/initial.mp4",
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
    public void OnRenderContextReadyWithNoInitialFileNoOps()
    {
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), recents);
        vm.OnRenderContextReady();
        Assert.That(pb.LastLoadedFile, Is.Null);
        Assert.That(recents.RecordedPaths, Is.Empty);
    }

    [Test]
    public void DisposeUnsubscribesFromPlayback()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles());
        pb.PositionSeconds = 5;
        var beforeDispose = vm.Position;

        vm.Dispose();
        pb.PositionSeconds = 99;
        Assert.That(vm.Position, Is.EqualTo(beforeDispose));
    }

    [Test]
    public void SubtitleTracksMirrorsPlayback()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles());
        var tracks = new[] { new SubtitleTrack(1, "English", "eng", false) };
        pb.SubtitleTracks = tracks;
        Assert.That(vm.SubtitleTracks, Is.SameAs(tracks));
    }

    [Test]
    public void CurrentSubtitleIdMirrorsPlayback()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles());
        pb.CurrentSubtitleId = 2;
        Assert.That(vm.CurrentSubtitleId, Is.EqualTo(2));
        pb.CurrentSubtitleId = null;
        Assert.That(vm.CurrentSubtitleId, Is.Null);
    }

    [Test]
    public void SelectSubtitlePassesIdToPlayback()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles());
        vm.SelectSubtitle(3);
        vm.SelectSubtitle(null);
        Assert.That(pb.SubtitleSelections, Is.EqualTo(new int?[] { 3, null }));
    }

    [Test]
    public async Task LoadSubtitleCommandLoadsPickedFile()
    {
        var pb = new FakePlayback();
        var picker = new FakeFilePicker { NextSubtitleResult = "/path/to/track.srt" };
        var vm = new ViewModelMain(pb, picker, new FakeRecentFiles());
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
        var vm = new ViewModelMain(pb, picker, recents);
        await vm.LoadSubtitleCommand.ExecuteAsync(null);
        Assert.That(picker.SubtitleCalls, Is.EqualTo(1));
        Assert.That(pb.LastLoadedSubtitle, Is.Null);
        // Subtitles aren't user-opened media; cancellation must not record into recents.
        Assert.That(recents.RecordedPaths, Is.Empty);
    }
}
