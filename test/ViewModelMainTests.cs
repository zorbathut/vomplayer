using System;
using System.Collections.Generic;
using System.IO;
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
        private IReadOnlyList<MediaTrack> videoTracks = Array.Empty<MediaTrack>();

        [ObservableProperty]
        private IReadOnlyList<MediaTrack> audioTracks = Array.Empty<MediaTrack>();

        [ObservableProperty]
        private IReadOnlyList<MediaTrack> subtitleTracks = Array.Empty<MediaTrack>();

        [ObservableProperty]
        private int? currentVideoId;

        [ObservableProperty]
        private int? currentAudioId;

        [ObservableProperty]
        private int? currentSubtitleId;

        public event Action? FileLoaded;
        public event Action<int>? FileEnded;
        public event Action? TracksReloaded;

        public int InitializeCalls { get; private set; }
        public int TogglePauseCalls { get; private set; }
        public int LoadFileCalls { get; private set; }
        public string? LastLoadedFile { get; private set; }
        public double? LastSeekSeconds { get; private set; }
        public string? LastLoadedAudio { get; private set; }
        public string? LastLoadedSubtitle { get; private set; }
        public List<int?> VideoSelections { get; } = new();
        public List<int?> AudioSelections { get; } = new();
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

        public void RaiseFileLoaded()
        {
            FileLoaded?.Invoke();
        }

        public void RaiseFileEnded(int reason)
        {
            FileEnded?.Invoke(reason);
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

        public IReadOnlyList<RecentFileEntry> GetMostRecent(int limit)
        {
            return Array.Empty<RecentFileEntry>();
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

    [Test]
    public void NullPlaybackThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new ViewModelMain(null!, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences()));
    }

    [Test]
    public void NullFilePickerThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new ViewModelMain(new FakePlayback(), null!, new FakeRecentFiles(), new FakeTrackPreferences()));
    }

    [Test]
    public void NullRecentFilesThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new ViewModelMain(new FakePlayback(), new FakeFilePicker(), null!, new FakeTrackPreferences()));
    }

    [Test]
    public void PositionMirrorsPlaybackPositionSeconds()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences());
        pb.PositionSeconds = 12.5;
        Assert.That(vm.Position, Is.EqualTo(TimeSpan.FromSeconds(12.5)));
    }

    [Test]
    public void DurationMirrorsPlaybackDurationSeconds()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences());
        pb.DurationSeconds = 60;
        Assert.That(vm.Duration, Is.EqualTo(TimeSpan.FromSeconds(60)));
    }

    [Test]
    public void IsPausedMirrorsPlayback()
    {
        var pb = new FakePlayback { IsPaused = true };
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences());
        pb.IsPaused = false;
        Assert.That(vm.IsPaused, Is.False);
    }

    [Test]
    public void SeekValueTracksPlaybackWhenNotDragging()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences());
        pb.DurationSeconds = 100;
        pb.PositionSeconds = 25;
        Assert.That(vm.SeekValue, Is.EqualTo(0.25));
    }

    [Test]
    public void SeekToSeeksToNormalizedPositionScaledByDuration()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences());
        pb.DurationSeconds = 100;

        vm.SeekTo(0.4);

        Assert.That(pb.LastSeekSeconds, Is.EqualTo(40));
    }

    [Test]
    public void SeekValueTracksPlaybackPosition()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences());
        pb.DurationSeconds = 100;

        pb.PositionSeconds = 75;

        Assert.That(vm.SeekValue, Is.EqualTo(0.75));
    }

    [Test]
    public void PlayPauseCommandTogglesPlayback()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences());
        pb.DurationSeconds = 60;
        vm.PlayPauseCommand.Execute(null);
        Assert.That(pb.TogglePauseCalls, Is.EqualTo(1));
    }

    [Test]
    public void PlayPauseCommandIsNoopBeforeFileLoad()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences());
        vm.PlayPauseCommand.Execute(null);
        Assert.That(pb.TogglePauseCalls, Is.EqualTo(0));
    }

    [Test]
    public async Task OpenCommandLoadsPickedFile()
    {
        var pb = new FakePlayback();
        var picker = new FakeFilePicker { NextResult = "/path/to/video.mp4" };
        var vm = new ViewModelMain(pb, picker, new FakeRecentFiles(), new FakeTrackPreferences());
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
        var vm = new ViewModelMain(pb, picker, recents, new FakeTrackPreferences());
        await vm.OpenCommand.ExecuteAsync(null);
        Assert.That(recents.RecordedPaths, Is.EqualTo(new[] { "/path/to/video.mp4" }));
    }

    [Test]
    public async Task OpenCommandIgnoresCancelledPicker()
    {
        var pb = new FakePlayback();
        var picker = new FakeFilePicker { NextResult = null };
        var recents = new FakeRecentFiles();
        var vm = new ViewModelMain(pb, picker, recents, new FakeTrackPreferences());
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
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences());
        vm.OpenFile("/path/to/dropped.mp4");
        Assert.That(pb.LoadFileCalls, Is.EqualTo(1));
        Assert.That(pb.LastLoadedFile, Is.EqualTo("/path/to/dropped.mp4"));
    }

    [Test]
    public void OpenFileRecordsTheTarget()
    {
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences());
        vm.OpenFile("/path/to/dropped.mp4");
        Assert.That(recents.RecordedPaths, Is.EqualTo(new[] { "/path/to/dropped.mp4" }));
    }

    [Test]
    public void OpenFileAcceptsRemoteUris()
    {
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences());
        vm.OpenFile("https://example.com/stream.m3u8");
        Assert.That(pb.LastLoadedFile, Is.EqualTo("https://example.com/stream.m3u8"));
        Assert.That(recents.RecordedPaths, Is.EqualTo(new[] { "https://example.com/stream.m3u8" }));
    }

    [Test]
    public void OnRenderContextReadyLoadsInitialFileOnce()
    {
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences())
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
        var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences());
        vm.OnRenderContextReady();
        Assert.That(pb.LastLoadedFile, Is.Null);
        Assert.That(recents.RecordedPaths, Is.Empty);
    }

    [Test]
    public void DisposeUnsubscribesFromPlayback()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences());
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
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences());
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
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences());
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
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences());
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
        var vm = new ViewModelMain(pb, picker, new FakeRecentFiles(), new FakeTrackPreferences());
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
        var vm = new ViewModelMain(pb, picker, recents, new FakeTrackPreferences());
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
        var vm = new ViewModelMain(pb, picker, new FakeRecentFiles(), new FakeTrackPreferences());
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
        var vm = new ViewModelMain(pb, picker, recents, new FakeTrackPreferences());
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
        var vm = new ViewModelMain(pb, picker, new FakeRecentFiles(), prefs);
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
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs);
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
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs);
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
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs);
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
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs);
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
            var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs);
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
            var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs);
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
            var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs);
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
            var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs);
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
            var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs);
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
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs);
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
            var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences());
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
            var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences());
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
            var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences());
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
            var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences());
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
            var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences());
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
            var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences());
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
            var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences());
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
            var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences());
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
            var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences());
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
            var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences());
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
            var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences());
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
            var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences());
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
            var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences());
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
    public void UriSourceDoesNotPersistPosition()
    {
        // URIs (http/smb/…) skip both the apply lookup AND every save trigger. Position persistence is for local paths only.
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), recents, new FakeTrackPreferences());
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
}
