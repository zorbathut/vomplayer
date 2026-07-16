using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Vomplayer.Playback;
using Vomplayer.ViewModels;

namespace Vomplayer.Tests;

[TestFixture]
public class ViewModelMainTests
{
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
        Assert.That(pb.SeekRelativeCalls, Is.EqualTo(new[] { -5.0, 10.0 }));
    }

    [Test]
    public void StepFrameForwardsToPlayback()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        vm.StepFrameForward();
        vm.StepFrameForward();
        vm.StepFrameBack();
        Assert.That(pb.StepFrameForwardCalls, Is.EqualTo(2));
        Assert.That(pb.StepFrameBackCalls, Is.EqualTo(1));
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
        Assert.That(pb.SetVolumeCalls, Is.EqualTo(new[] { 75.0 }));
    }

    [Test]
    public void AdjustVolumeForwardsToPlayback()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        vm.AdjustVolume(-5);
        vm.AdjustVolume(10);
        Assert.That(pb.AdjustVolumeCalls, Is.EqualTo(new[] { -5.0, 10.0 }));
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
}
