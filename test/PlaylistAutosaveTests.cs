using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Vomplayer.Playback;
using Vomplayer.Services;
using Vomplayer.UserData;
using Vomplayer.ViewModels;

namespace Vomplayer.Tests;

[TestFixture]
public partial class PlaylistAutosaveTests
{
    // Minimal in-memory ISavedPlaylists. Records every Save / Touch call (in order) so tests can assert mint-vs-reuse and recompute behavior. Backing dict supports GetById / GetMostRecent.
    private sealed class FakeSavedPlaylists : ISavedPlaylists
    {
        private readonly Dictionary<Guid, SavedPlaylistEntry> entries = new();
        public List<(Guid Guid, string Title, IReadOnlyList<SavedPlaylistStream> Streams)> SaveCalls { get; } = new();
        public List<Guid> TouchCalls { get; } = new();
        private long tickCounter = 1;

        public void Save(Guid guid, string title, IReadOnlyList<SavedPlaylistStream> streams)
        {
            // Mirror the real implementation's empty-stream filter so tests don't depend on the autosave service to do it before delegating.
            var nonEmpty = streams.Where(s => s.Items.Count > 0).ToList();
            if (nonEmpty.Count == 0)
            {
                return;
            }
            SaveCalls.Add((guid, title, nonEmpty));
            entries[guid] = new SavedPlaylistEntry(
                guid, title,
                new DateTimeOffset(tickCounter++, TimeSpan.Zero),
                nonEmpty.Count,
                nonEmpty);
        }

        public void Touch(Guid guid)
        {
            TouchCalls.Add(guid);
            if (entries.TryGetValue(guid, out var e))
            {
                entries[guid] = e with { LastUsedAt = new DateTimeOffset(tickCounter++, TimeSpan.Zero) };
            }
        }

        public SavedPlaylistEntry? GetById(Guid guid)
        {
            return entries.TryGetValue(guid, out var e) ? e : null;
        }

        public SavedPlaylistEntry? GetMostRecent()
        {
            return entries.Values.OrderByDescending(e => e.LastUsedAt).FirstOrDefault();
        }

        public IReadOnlyList<SavedPlaylistEntry> GetMostRecent(int limit)
        {
            return entries.Values.OrderByDescending(e => e.LastUsedAt).Take(limit).ToList();
        }
    }

    private sealed partial class FakePlayback : ObservableObject, IPlayback
    {
        [ObservableProperty] private double positionSeconds;
        [ObservableProperty] private double durationSeconds;
        [ObservableProperty] private bool isPaused = true;
        [ObservableProperty] private bool isSeeking;
        [ObservableProperty] private bool isCoreIdle = true;
        [ObservableProperty] private bool isEofReached;
        [ObservableProperty] private double volume = 100;
        [ObservableProperty] private bool isMuted;
        [ObservableProperty] private IReadOnlyList<MediaTrack> videoTracks = Array.Empty<MediaTrack>();
        [ObservableProperty] private IReadOnlyList<MediaTrack> audioTracks = Array.Empty<MediaTrack>();
        [ObservableProperty] private IReadOnlyList<MediaTrack> subtitleTracks = Array.Empty<MediaTrack>();
        [ObservableProperty] private IReadOnlyList<MediaChapter> chapters = Array.Empty<MediaChapter>();
        [ObservableProperty] private int? currentVideoId;
        [ObservableProperty] private int? currentAudioId;
        [ObservableProperty] private int? currentSubtitleId;
        [ObservableProperty] private double? videoAspect;
        [ObservableProperty] private double? videoFps;
        [ObservableProperty] private string? mediaTitle;
        [ObservableProperty] private double? estimatedVfFps;

#pragma warning disable CS0067 // events declared to satisfy IPlayback; tests don't fire them
        public event Action? FileLoaded;
        public event Action? TracksReloaded;
        public event Action<bool>? SourceHdrChanged;
        public event Action<bool>? IsSourceFpsTrustedChanged;
#pragma warning restore CS0067

        public bool IsSourceHdr { get; set; }
        public string? HwdecCurrent { get; set; }
        public IReadOnlyList<string> HwdecTranscript { get; set; } = Array.Empty<string>();
        public bool IsSourceFpsTrusted { get; set; } = true;
        public string FpsTrustReason { get; set; } = "";

        public List<(string path, bool startPaused)> LoadFileCalls { get; } = new();

        public void Initialize() { }
        public void LoadFile(string path, bool startPaused)
        {
            LoadFileCalls.Add((path, startPaused));
        }
        public void TogglePause() { IsPaused = !IsPaused; }
        public void SetPaused(bool paused) { IsPaused = paused; }
        public void Seek(double seconds) { }
        public void SeekRelative(double seconds) { }
        public void StepFrameForward() { }
        public void StepFrameBack() { }
        public void LoadAudio(string path) { }
        public void LoadSubtitle(string path) { }
        public void SetVideo(int? trackId) { }
        public void SetAudio(int? trackId) { }
        public void SetSubtitle(int? trackId) { }
        public void SetVolume(double percent) { Volume = percent; }
        public void SetSpeed(double rate) { }
        public void AdjustVolume(double deltaPercent) { }
        public void ToggleMute() { IsMuted = !IsMuted; }
        public void EnableHdrOutput() { }
        public void DisableHdrOutput() { }
        public void SetFrameMultiplier(double outputFps) { }
        public void ClearFrameMultiplier() { }
        public void Dispose() { }

        // Trigger MediaTitle PropertyChanged from outside — autosave subscribes to this.
        public void RaiseMediaTitle(string? title) { MediaTitle = title; }
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

    private static VideoContext NewContext()
    {
        return new VideoContext(new FakePlayback(), new StubFilePicker(), new StubRecentFiles(), new StubTrackPreferences(), new StubUrlDownloader(), new StubUrlPrompt());
    }

    [Test]
    public void NullRepoThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new PlaylistAutosave(null!));
    }

    [Test]
    public void BindPrimaryReplaceFiresSave()
    {
        var repo = new FakeSavedPlaylists();
        var autosave = new PlaylistAutosave(repo);
        using var primary = NewContext();
        autosave.BindPrimary(primary);

        primary.Playlist.Replace(new[] { "/a.mp4" });
        Assert.That(repo.SaveCalls.Count, Is.EqualTo(1));
        Assert.That(repo.SaveCalls[0].Streams[0].Items, Is.EqualTo(new[] { "/a.mp4" }));
    }

    [Test]
    public void ReplaceOnPrimaryWithoutSecondaryMintsNewGuid()
    {
        var repo = new FakeSavedPlaylists();
        var autosave = new PlaylistAutosave(repo);
        using var primary = NewContext();
        autosave.BindPrimary(primary);

        primary.Playlist.Replace(new[] { "/a.mp4" });
        var firstGuid = autosave.CurrentGuid;
        Assert.That(firstGuid, Is.Not.EqualTo(Guid.Empty));

        primary.Playlist.Replace(new[] { "/b.mp4" });
        var secondGuid = autosave.CurrentGuid;
        Assert.That(secondGuid, Is.Not.EqualTo(firstGuid), "fresh Replace with no Secondary should mint a new GUID");
        // Both rows persist — old playlist not clobbered.
        Assert.That(repo.GetById(firstGuid), Is.Not.Null);
        Assert.That(repo.GetById(secondGuid), Is.Not.Null);
    }

    [Test]
    public void ReplaceOnPrimaryWithBoundEmptySecondaryMintsNewGuid()
    {
        var repo = new FakeSavedPlaylists();
        var autosave = new PlaylistAutosave(repo);
        using var primary = NewContext();
        using var secondary = NewContext();
        autosave.BindPrimary(primary);
        autosave.BindSecondary(secondary);

        primary.Playlist.Replace(new[] { "/a.mp4" });
        var first = autosave.CurrentGuid;
        primary.Playlist.Replace(new[] { "/b.mp4" });
        Assert.That(autosave.CurrentGuid, Is.Not.EqualTo(first), "bound-but-empty Secondary doesn't preserve GUID");
    }

    [Test]
    public void ReplaceOnPrimaryWithSecondaryItemsKeepsGuid()
    {
        var repo = new FakeSavedPlaylists();
        var autosave = new PlaylistAutosave(repo);
        using var primary = NewContext();
        using var secondary = NewContext();
        autosave.BindPrimary(primary);
        autosave.BindSecondary(secondary);

        primary.Playlist.Replace(new[] { "/a.mp4" });
        secondary.Playlist.Replace(new[] { "/sec.mp4" });
        var pipGuid = autosave.CurrentGuid;
        primary.Playlist.Replace(new[] { "/replaced.mp4" });
        Assert.That(autosave.CurrentGuid, Is.EqualTo(pipGuid), "PiP partial replace must reuse GUID");
    }

    [Test]
    public void ReplaceOnSecondaryAlwaysKeepsGuid()
    {
        var repo = new FakeSavedPlaylists();
        var autosave = new PlaylistAutosave(repo);
        using var primary = NewContext();
        using var secondary = NewContext();
        autosave.BindPrimary(primary);
        autosave.BindSecondary(secondary);

        primary.Playlist.Replace(new[] { "/a.mp4" });
        var initial = autosave.CurrentGuid;
        secondary.Playlist.Replace(new[] { "/sec.mp4" });
        Assert.That(autosave.CurrentGuid, Is.EqualTo(initial));
    }

    [Test]
    public void NonReplaceMutationsKeepGuid()
    {
        var repo = new FakeSavedPlaylists();
        var autosave = new PlaylistAutosave(repo);
        using var primary = NewContext();
        autosave.BindPrimary(primary);

        primary.Playlist.Replace(new[] { "/a.mp4", "/b.mp4", "/c.mp4" });
        var initial = autosave.CurrentGuid;

        primary.Playlist.Append(new[] { "/d.mp4" });
        Assert.That(autosave.CurrentGuid, Is.EqualTo(initial), "Append");

        primary.Playlist.SetCurrent(2);
        Assert.That(autosave.CurrentGuid, Is.EqualTo(initial), "SetCurrent");

        primary.Playlist.MoveMany(new[] { 0 }, 2);
        Assert.That(autosave.CurrentGuid, Is.EqualTo(initial), "Move");

        primary.Playlist.Advance();
        Assert.That(autosave.CurrentGuid, Is.EqualTo(initial), "Advance");
    }

    [Test]
    public void BeginRestoreSuppressesSaves()
    {
        var repo = new FakeSavedPlaylists();
        var autosave = new PlaylistAutosave(repo);
        using var primary = NewContext();
        autosave.BindPrimary(primary);

        autosave.BeginRestore();
        primary.Playlist.Replace(new[] { "/a.mp4" });
        primary.Playlist.SetCurrent(0);
        Assert.That(repo.SaveCalls, Is.Empty, "writes during loading must be suppressed");
        autosave.EndRestore();
        // EndRestore itself doesn't trigger a save — confirm.
        Assert.That(repo.SaveCalls, Is.Empty);
    }

    [Test]
    public void TitleFallsBackToFilename()
    {
        var repo = new FakeSavedPlaylists();
        var autosave = new PlaylistAutosave(repo);
        using var primary = NewContext();
        autosave.BindPrimary(primary);

        primary.Playlist.Replace(new[] { "/some/dir/Movie.mp4" });
        var saved = repo.GetById(autosave.CurrentGuid);
        Assert.That(saved!.Title, Is.EqualTo("Movie.mp4"));
    }

    [Test]
    public void MediaTitlePropertyChangeRewritesTitle()
    {
        var repo = new FakeSavedPlaylists();
        var autosave = new PlaylistAutosave(repo);
        var fakePlayback = new FakePlayback();
        using var primary = new VideoContext(fakePlayback, new StubFilePicker(), new StubRecentFiles(), new StubTrackPreferences(), new StubUrlDownloader(), new StubUrlPrompt());
        autosave.BindPrimary(primary);

        primary.Playlist.Replace(new[] { "/some/dir/Movie.mp4" });
        Assert.That(repo.GetById(autosave.CurrentGuid)!.Title, Is.EqualTo("Movie.mp4"));

        // Now mpv reports the real media-title; VideoContext mirrors it via its PropertyChanged handler.
        fakePlayback.RaiseMediaTitle("The Real Movie Title");
        Assert.That(repo.GetById(autosave.CurrentGuid)!.Title, Is.EqualTo("The Real Movie Title"));
    }

    [Test]
    public void MultiStreamTitleIsPlusJoined()
    {
        var repo = new FakeSavedPlaylists();
        var autosave = new PlaylistAutosave(repo);
        var primaryPb = new FakePlayback();
        var secondaryPb = new FakePlayback();
        using var primary = new VideoContext(primaryPb, new StubFilePicker(), new StubRecentFiles(), new StubTrackPreferences(), new StubUrlDownloader(), new StubUrlPrompt());
        using var secondary = new VideoContext(secondaryPb, new StubFilePicker(), new StubRecentFiles(), new StubTrackPreferences(), new StubUrlDownloader(), new StubUrlPrompt());
        autosave.BindPrimary(primary);
        autosave.BindSecondary(secondary);

        primary.Playlist.Replace(new[] { "/a.mp4" });
        secondary.Playlist.Replace(new[] { "/b.mp4" });
        primaryPb.RaiseMediaTitle("Alpha");
        secondaryPb.RaiseMediaTitle("Beta");
        Assert.That(repo.GetById(autosave.CurrentGuid)!.Title, Is.EqualTo("Alpha + Beta"));
    }

    [Test]
    public void UnbindSecondaryDropsSecondaryFromPersist()
    {
        var repo = new FakeSavedPlaylists();
        var autosave = new PlaylistAutosave(repo);
        using var primary = NewContext();
        using var secondary = NewContext();
        autosave.BindPrimary(primary);
        autosave.BindSecondary(secondary);

        primary.Playlist.Replace(new[] { "/p.mp4" });
        secondary.Playlist.Replace(new[] { "/s.mp4" });
        Assert.That(repo.GetById(autosave.CurrentGuid)!.StreamCount, Is.EqualTo(2));

        autosave.UnbindSecondary();
        // After unbind, the row is rewritten without slot 1.
        Assert.That(repo.GetById(autosave.CurrentGuid)!.StreamCount, Is.EqualTo(1));
    }

    [Test]
    public void EmptyPrimaryProducesNoSave()
    {
        var repo = new FakeSavedPlaylists();
        var autosave = new PlaylistAutosave(repo);
        using var primary = NewContext();
        autosave.BindPrimary(primary);
        // No mutation happened — no Changed event, no save. Defensive: explicitly verify we don't try to mint a GUID against nothing.
        Assert.That(repo.SaveCalls, Is.Empty);
        Assert.That(autosave.CurrentGuid, Is.EqualTo(Guid.Empty));
    }

    [Test]
    public void DisablePipPersistDuringRestoreIsSuppressed()
    {
        // UnbindSecondary's Persist call must respect `loading`. Without the gate, a DisablePip-during-restore would write through with the wrong title (the restore hadn't computed it yet).
        var repo = new FakeSavedPlaylists();
        var autosave = new PlaylistAutosave(repo);
        using var primary = NewContext();
        using var secondary = NewContext();
        autosave.BindPrimary(primary);
        autosave.BindSecondary(secondary);

        primary.Playlist.Replace(new[] { "/p.mp4" });
        secondary.Playlist.Replace(new[] { "/s.mp4" });
        int saveCountBefore = repo.SaveCalls.Count;

        autosave.BeginRestore();
        autosave.UnbindSecondary();
        Assert.That(repo.SaveCalls.Count, Is.EqualTo(saveCountBefore), "UnbindSecondary's Persist must be gated by loading");
        autosave.EndRestore();
    }

    [Test]
    public void DetachStopsUnbindSecondaryFromPersisting()
    {
        // Pins the shutdown-clobber regression: a two-stream row must survive the shutdown teardown chain (DisablePip → UnbindSecondary) when the autosave has been Detached first.
        var repo = new FakeSavedPlaylists();
        var autosave = new PlaylistAutosave(repo);
        var primaryPb = new FakePlayback();
        var secondaryPb = new FakePlayback();
        using var primary = new VideoContext(primaryPb, new StubFilePicker(), new StubRecentFiles(), new StubTrackPreferences(), new StubUrlDownloader(), new StubUrlPrompt());
        using var secondary = new VideoContext(secondaryPb, new StubFilePicker(), new StubRecentFiles(), new StubTrackPreferences(), new StubUrlDownloader(), new StubUrlPrompt());
        autosave.BindPrimary(primary);
        autosave.BindSecondary(secondary);

        primary.Playlist.Replace(new[] { "/p.mp4" });
        secondary.Playlist.Replace(new[] { "/s.mp4" });
        primaryPb.RaiseMediaTitle("P");
        secondaryPb.RaiseMediaTitle("S");
        var guid = autosave.CurrentGuid;
        Assert.That(repo.GetById(guid)!.StreamCount, Is.EqualTo(2));
        int saveCountBefore = repo.SaveCalls.Count;

        autosave.Detach();
        autosave.UnbindSecondary();

        Assert.That(repo.SaveCalls.Count, Is.EqualTo(saveCountBefore), "UnbindSecondary after Detach must not write");
        Assert.That(repo.GetById(guid)!.StreamCount, Is.EqualTo(2), "two-stream row must still be intact");
    }

    [Test]
    public void DetachStopsPersistOnSubsequentMutations()
    {
        // After Detach, the autosave is inert: no Replace, SetCurrent, Move, or MediaTitle change can produce a write.
        var repo = new FakeSavedPlaylists();
        var autosave = new PlaylistAutosave(repo);
        var primaryPb = new FakePlayback();
        using var primary = new VideoContext(primaryPb, new StubFilePicker(), new StubRecentFiles(), new StubTrackPreferences(), new StubUrlDownloader(), new StubUrlPrompt());
        autosave.BindPrimary(primary);

        primary.Playlist.Replace(new[] { "/a.mp4", "/b.mp4" });
        int saveCountBefore = repo.SaveCalls.Count;

        autosave.Detach();

        primary.Playlist.Replace(new[] { "/x.mp4" });
        primary.Playlist.SetCurrent(0);
        primary.Playlist.Append(new[] { "/y.mp4" });
        primary.Playlist.MoveMany(new[] { 0 }, 2);
        primaryPb.RaiseMediaTitle("Title After Detach");

        Assert.That(repo.SaveCalls.Count, Is.EqualTo(saveCountBefore), "No Persist after Detach");
    }

    [Test]
    public void DetachIsIdempotent()
    {
        var repo = new FakeSavedPlaylists();
        var autosave = new PlaylistAutosave(repo);
        using var primary = NewContext();
        autosave.BindPrimary(primary);
        primary.Playlist.Replace(new[] { "/a.mp4" });

        autosave.Detach();
        Assert.DoesNotThrow(() => autosave.Detach());
    }

    [Test]
    public void DetachBeforeAnyBindIsSafe()
    {
        // Defensive: a VM that constructed an autosave but never bound primary should still tolerate Detach (e.g., a failed attach path).
        var repo = new FakeSavedPlaylists();
        var autosave = new PlaylistAutosave(repo);
        Assert.DoesNotThrow(() => autosave.Detach());
    }

    [Test]
    public void BindAfterDetachThrows()
    {
        // The autosave is dead after Detach. Re-binding is a programming error.
        var repo = new FakeSavedPlaylists();
        var autosave = new PlaylistAutosave(repo);
        using var primary = NewContext();
        using var secondary = NewContext();
        autosave.Detach();
        Assert.Throws<InvalidOperationException>(() => autosave.BindPrimary(primary));
        Assert.Throws<InvalidOperationException>(() => autosave.BindSecondary(secondary));
    }

    [Test]
    public void ViewModelDisposeWithPipActivePreservesMultiStreamRow()
    {
        // Pins the end-to-end shutdown bug: with PiP active and both slots populated, vm.Dispose must NOT clobber the saved row's secondary slot. Reproduces the production-observed corruption where a stream_count=2 row became stream_count=1 on app close.
        var vm = NewViewModelWithAutosave(out _, out var repo);
        var secondaryPb = new FakePlayback();
        var secondary = new VideoContext(secondaryPb, new StubFilePicker(), new StubRecentFiles(), new StubTrackPreferences(), new StubUrlDownloader(), new StubUrlPrompt());
        vm.EnablePip(secondary);

        vm.Primary.Playlist.Replace(new[] { "/p.mp4" });
        secondary.Playlist.Replace(new[] { "/s.mp4" });
        var guid = vm.Autosave!.CurrentGuid;
        Assert.That(repo.GetById(guid)!.StreamCount, Is.EqualTo(2));

        vm.Dispose();

        Assert.That(repo.GetById(guid)!.StreamCount, Is.EqualTo(2), "shutdown must not overwrite the two-stream row");
        var streams = repo.GetById(guid)!.Streams;
        Assert.That(streams.Any(s => s.SlotIndex == 0 && s.Items.SequenceEqual(new[] { "/p.mp4" })), Is.True);
        Assert.That(streams.Any(s => s.SlotIndex == 1 && s.Items.SequenceEqual(new[] { "/s.mp4" })), Is.True);
    }

    // --- LoadFromSaved integration tests (exercises ViewModelMain → PlaylistAutosave + repo together) ---

    private static ViewModelMain NewViewModelWithAutosave(out FakePlayback playback, out FakeSavedPlaylists repo)
    {
        playback = new FakePlayback();
        repo = new FakeSavedPlaylists();
        var vm = new ViewModelMain(playback, new StubFilePicker(), new StubRecentFiles(), new StubTrackPreferences(), new StubUrlDownloader(), new StubUrlPrompt());
        vm.AttachAutosave(repo);
        return vm;
    }

    [Test]
    public void LoadFromSavedRestoresItemsAndCurrentIndex()
    {
        var vm = NewViewModelWithAutosave(out _, out var repo);
        var guid = Guid.NewGuid();
        // Pre-populate the fake repo. Use Save (not direct dict access) so the shape mirrors what PlaylistAutosave would write.
        repo.Save(guid, "saved", new[] { new SavedPlaylistStream(0, 2, new[] { "/a.mp4", "/b.mp4", "/c.mp4" }) });

        vm.LoadFromSaved(guid, startPaused: true);

        Assert.That(vm.Primary.Playlist.Items, Is.EqualTo(new[] { "/a.mp4", "/b.mp4", "/c.mp4" }));
        Assert.That(vm.Primary.Playlist.CurrentIndex, Is.EqualTo(2));
        Assert.That(vm.Autosave!.CurrentGuid, Is.EqualTo(guid));
    }

    [Test]
    public void LoadFromSavedThreadsStartPausedThroughToLoadFile()
    {
        // Regression: app-startup auto-restore was auto-playing. RestorePlaylist previously called playback.SetPaused(true) BEFORE LoadFile, but the dispatcher in real Playback issues pause=no AFTER loadfile, so the pre-call was overridden. The fix routes startPaused as a parameter on LoadFile so the dispatch is atomic. Pin the contract: a startPaused=true restore must produce a single LoadFile call with startPaused=true (so the fake's IsPaused mirror lands true — same way real mpv's pause property lands true after the dispatched pause=yes).
        var vm = NewViewModelWithAutosave(out var pb, out var repo);
        var guid = Guid.NewGuid();
        repo.Save(guid, "saved", new[] { new SavedPlaylistStream(0, 0, new[] { "/a.mp4" }) });

        vm.LoadFromSaved(guid, startPaused: true);

        // The Playback contract for LoadFile(_, startPaused: true) is "dispatch pause=yes atomically with loadfile so mpv loads paused". Verifying the call shape here is the closest the test layer can get without a real mpv handle; the production dispatch ordering in Playback.LoadFile is short enough to verify by reading.
        Assert.That(pb.LoadFileCalls, Is.EqualTo(new[] { ("/a.mp4", true) }));
    }

    [Test]
    public void LoadFromSavedDoesNotFireRedundantSavesDuringRestore()
    {
        var vm = NewViewModelWithAutosave(out _, out var repo);
        var guid = Guid.NewGuid();
        repo.Save(guid, "saved", new[] { new SavedPlaylistStream(0, 1, new[] { "/a.mp4", "/b.mp4" }) });
        int saveCountBefore = repo.SaveCalls.Count;

        vm.LoadFromSaved(guid, startPaused: true);

        // The Replace + SetCurrent inside RestorePlaylist would each fire Changed → Persist → Save without the loading gate. With it, no extra Saves.
        Assert.That(repo.SaveCalls.Count, Is.EqualTo(saveCountBefore));
    }

    [Test]
    public void LoadFromSavedTouchesEntryToBumpRecency()
    {
        var vm = NewViewModelWithAutosave(out _, out var repo);
        var guid = Guid.NewGuid();
        repo.Save(guid, "saved", new[] { new SavedPlaylistStream(0, 0, new[] { "/a.mp4" }) });
        int touchCountBefore = repo.TouchCalls.Count;

        vm.LoadFromSaved(guid, startPaused: true);

        Assert.That(repo.TouchCalls.Count, Is.EqualTo(touchCountBefore + 1));
        Assert.That(repo.TouchCalls[^1], Is.EqualTo(guid));
    }

    [Test]
    public void LoadFromSavedFiresSavedEventForMenuRebuild()
    {
        var vm = NewViewModelWithAutosave(out _, out var repo);
        var guid = Guid.NewGuid();
        repo.Save(guid, "saved", new[] { new SavedPlaylistStream(0, 0, new[] { "/a.mp4" }) });
        int savedFires = 0;
        vm.Autosave!.Saved += () => savedFires++;

        vm.LoadFromSaved(guid, startPaused: true);

        Assert.That(savedFires, Is.GreaterThanOrEqualTo(1), "Saved event must fire so the Recent menu rebuilds with the new ordering");
    }

    [Test]
    public void LoadFromSavedOnUnknownGuidIsNoOp()
    {
        var vm = NewViewModelWithAutosave(out _, out var repo);
        // No items in repo. LoadFromSaved must not throw and must not touch Primary's playlist.
        vm.LoadFromSaved(Guid.NewGuid(), startPaused: true);
        Assert.That(vm.Primary.Playlist.Items, Is.Empty);
    }

    [Test]
    public void LoadFromSavedThrowsWithoutAttachAutosave()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new StubFilePicker(), new StubRecentFiles(), new StubTrackPreferences(), new StubUrlDownloader(), new StubUrlPrompt());
        Assert.Throws<InvalidOperationException>(() => vm.LoadFromSaved(Guid.NewGuid(), startPaused: true));
    }

    [Test]
    public void LoadFromSavedSubsequentReplaceMintsNewGuidAndPreservesOldEntry()
    {
        // The post-load behavior: if user does a fresh Replace after loading a saved entry, a new GUID is minted and the loaded entry stays in the repo.
        var vm = NewViewModelWithAutosave(out _, out var repo);
        var loadedGuid = Guid.NewGuid();
        repo.Save(loadedGuid, "loaded", new[] { new SavedPlaylistStream(0, 0, new[] { "/loaded.mp4" }) });
        vm.LoadFromSaved(loadedGuid, startPaused: true);
        Assert.That(vm.Autosave!.CurrentGuid, Is.EqualTo(loadedGuid));

        vm.Primary.Playlist.Replace(new[] { "/fresh.mp4" });

        var newGuid = vm.Autosave!.CurrentGuid;
        Assert.That(newGuid, Is.Not.EqualTo(loadedGuid), "fresh Replace mints new GUID");
        Assert.That(repo.GetById(loadedGuid), Is.Not.Null, "loaded entry preserved");
        Assert.That(repo.GetById(newGuid), Is.Not.Null, "new entry written");
    }

    [Test]
    public void RestorePlaylistDoesNotBumpRecentsRecord()
    {
        // The system, not the user, opened the restored file — so recents.last_opened must NOT be touched. (The position-resume flow still works because position_seconds is independent.)
        var pb = new FakePlayback();
        var recents = new RecordingRecentFiles();
        using var ctx = new VideoContext(pb, new StubFilePicker(), recents, new StubTrackPreferences(), new StubUrlDownloader(), new StubUrlPrompt());

        ctx.RestorePlaylist(new[] { "/restored.mp4" }, currentIndex: 0, startPaused: true);

        Assert.That(recents.RecordedPaths, Is.Empty, "RestorePlaylist must not call recents.Record");
    }

    [Test]
    public void PlayPlaylistItemAfterRestoreDoesBumpRecents()
    {
        // After restore, the user clicks a row → that's a deliberate open → recents IS bumped.
        var pb = new FakePlayback();
        var recents = new RecordingRecentFiles();
        using var ctx = new VideoContext(pb, new StubFilePicker(), recents, new StubTrackPreferences(), new StubUrlDownloader(), new StubUrlPrompt());

        ctx.RestorePlaylist(new[] { "/a.mp4", "/b.mp4" }, currentIndex: 0, startPaused: true);
        ctx.PlayPlaylistItem(1);

        Assert.That(recents.RecordedPaths, Is.EqualTo(new[] { "/b.mp4" }));
    }

    private sealed class RecordingRecentFiles : IRecentFiles
    {
        public List<string> RecordedPaths { get; } = new();
        public void Record(string pathOrUri) { RecordedPaths.Add(pathOrUri); }
        public IReadOnlyList<RecentFileEntry> GetMostRecent(int limit) { return Array.Empty<RecentFileEntry>(); }
        public void RecordPosition(string pathOrUri, double positionSeconds) { }
        public double? GetPosition(string pathOrUri) { return null; }
    }
}
