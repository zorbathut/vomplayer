using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vomplayer.Playback;
using Vomplayer.Services;
using Vomplayer.UserData;

namespace Vomplayer.ViewModels;

public sealed partial class ViewModelMain : ObservableObject, IDisposable
{
    // Set VOMPL_LOG_PREFS=1 to trace per-directory preference save/apply paths to stderr. Useful for debugging "preferences aren't persisting / aren't being applied" reports — pinpoints whether the failure is in save (currentDirectoryKey null, Record never called) or apply (TracksReloaded not firing, Get returning null, Matcher rejecting).
    private static readonly bool LogPrefs = Environment.GetEnvironmentVariable("VOMPL_LOG_PREFS") == "1";
    private static void PrefsLog(string msg)
    {
        if (LogPrefs)
        {
            Console.Error.WriteLine($"[vompl prefs] {msg}");
        }
    }

    // Threshold (in source-content seconds) between periodic saves during playback. Position-delta-based, not wallclock, so 2× playback speed still saves every 10s of *content*. Tuned to the user's "every 5–15s while playing" guidance — 10s is the middle of that band.
    private const double PeriodicSaveThresholdSeconds = 10.0;
    // Don't save (or apply) a resume position within this many seconds of the file's end. Otherwise watching to completion leaves a saved position at ~duration, and the next reload jumps straight to the EOF state.
    private const double NearEndIgnoreSeconds = 5.0;

    private readonly IPlayback playback;
    private readonly IFilePicker filePicker;
    private readonly IRecentFiles recentFiles;
    private readonly ITrackPreferences trackPreferences;
    private bool initialFileLoaded;
    // The directory key (per TrackPreferences.TryGetDirectoryKey) for the most recent OpenFile target. Null when the latest file isn't a local-filesystem path (URI sources don't participate in per-directory preferences). Mutated only on OpenFile and read on Select* (save) and the FileLoaded/TracksReloaded apply path.
    //
    // Known limitation (rapid file swap): OpenFile(B) overwrites this before FileLoaded for A arrives if the user opens two files in quick succession. The first-file's TracksReloaded then matches against B's directory preferences. Rare in practice, self-correcting on the next load. Fixing properly would require correlating mpv's `path` property with the load that triggered it.
    private string? currentDirectoryKey;
    // The full path-or-URI for the most recent OpenFile target. Distinct from currentDirectoryKey because the position layer keys on the file itself, not its directory. Set in OpenFile alongside currentDirectoryKey, used by every save trigger to identify which row to update. Null pre-OpenFile and after the outgoing-file save for a non-local source clears it.
    private string? currentFilePath;
    // False from OpenFile until the matching FileLoaded fires. Gates position saves so that mpv's load-time IsPaused churn doesn't spuriously persist position 0 against the new file. (DurationSeconds > 0 is *almost* the same gate, but DurationSeconds can briefly carry the previous file's value across the unload/reload window before the new file's duration arrives — currentFileLoaded is the unambiguous signal.)
    private bool currentFileLoaded;
    // Last position we successfully persisted for the current file (in source-content seconds). Used by the periodic-save throttle. Reset to 0 on every OpenFile so the new file's first 10s milestone isn't taken to be already saved.
    private double lastSavedPositionSeconds;
    // Saved position to seek to once FileLoaded fires (i.e. once duration is known). Set in OpenFile, cleared in ApplyResumePositionIfPending.
    private double? pendingResumePosition;
    // Previous IsPaused value, for rising-edge detection in OnPlaybackPropertyChanged. Updated only by real PropertyChanged events — never re-snapshotted from playback.IsPaused on OpenFile, because the snapshot would race a queued-but-not-yet-dispatched IsPaused event from the dispatcher worker, leaving wasPaused out of sync with the next edge the handler sees.
    private bool wasPaused = true;
    // Per-kind apply tracking: each kind that has been successfully applied for the current file load is in this set. Reset on FileLoaded so the next file gets a fresh attempt for all three. Each TracksReloaded fire retries any kind not yet in the set, which handles mpv's lazy track discovery (e.g., external sub-auto landing in a later TracksReloaded than the embedded tracks).
    private readonly HashSet<MediaKind> appliedKindsForCurrentFile = new();
    // Set on FileLoaded, cleared on the next FileLoaded. Distinguishes "we're in the apply window" from "no file load is pending" so that user-driven track-list changes (sub-add later in the session) don't trigger spurious applies.
    private bool inApplyWindow;

    [ObservableProperty]
    private TimeSpan position;

    [ObservableProperty]
    private TimeSpan duration;

    [ObservableProperty]
    private bool isPaused = true;

    [ObservableProperty]
    private double seekValue;

    // Mirrors of IPlayback per-kind track state. The view (MainWindow) subscribes to PropertyChanged on these to rebuild the per-kind submenus — the menus themselves aren't widget trees we can data-bind, they're Gio.Menus rebuilt wholesale, so going through normal VM mirrors keeps the cross-thread story uniform with everything else (playback fires PropertyChanged on the main thread, view rebuilds menus on the main thread).
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

    public string? InitialFile { get; set; }

    public ViewModelMain(IPlayback playback, IFilePicker filePicker, IRecentFiles recentFiles, ITrackPreferences trackPreferences)
    {
        if (playback == null)
        {
            throw new ArgumentNullException(nameof(playback));
        }
        if (filePicker == null)
        {
            throw new ArgumentNullException(nameof(filePicker));
        }
        if (recentFiles == null)
        {
            throw new ArgumentNullException(nameof(recentFiles));
        }
        if (trackPreferences == null)
        {
            throw new ArgumentNullException(nameof(trackPreferences));
        }
        this.playback = playback;
        this.filePicker = filePicker;
        this.recentFiles = recentFiles;
        this.trackPreferences = trackPreferences;
        this.playback.PropertyChanged += OnPlaybackPropertyChanged;
        this.playback.FileLoaded += OnPlaybackFileLoaded;
        this.playback.TracksReloaded += OnPlaybackTracksReloaded;
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        var path = await filePicker.PickVideoFileAsync("Open media");
        if (path == null)
        {
            return;
        }
        // Route through OpenFile so the picker path goes through the same recents-record + currentDirectoryKey-resolve dance as drag-and-drop and command-line invocation. Without this, picker-opened files don't get a directory key set, and SaveTrackPreference silently no-ops on every menu pick.
        OpenFile(path);
    }

    [RelayCommand]
    private async Task LoadAudioAsync()
    {
        var path = await filePicker.PickAudioFileAsync("Open audio file");
        if (path == null)
        {
            return;
        }
        playback.LoadAudio(path);
    }

    [RelayCommand]
    private async Task LoadSubtitleAsync()
    {
        var path = await filePicker.PickSubtitleFileAsync("Open subtitle file");
        if (path == null)
        {
            return;
        }
        playback.LoadSubtitle(path);
    }

    // Direct-call entrypoints for the per-kind submenu radio items. RelayCommand-with-parameter would have worked, but the menu's Gio.SimpleAction surface is already wired through ExecuteAction in MainWindow — going through plain methods keeps the action handlers tight.
    //
    // These methods are the SAVE side of the per-directory preferences flow: the user's explicit menu choice is recorded here. The apply-side path (OnPlaybackTracksReloaded) calls playback.SetXxx directly, bypassing these methods, so an automated apply doesn't loop back into a save.
    public void SelectVideo(int? trackId)
    {
        SaveTrackPreference(MediaKind.Video, trackId, VideoTracks);
        playback.SetVideo(trackId);
    }

    public void SelectAudio(int? trackId)
    {
        SaveTrackPreference(MediaKind.Audio, trackId, AudioTracks);
        playback.SetAudio(trackId);
    }

    public void SelectSubtitle(int? trackId)
    {
        SaveTrackPreference(MediaKind.Subtitle, trackId, SubtitleTracks);
        playback.SetSubtitle(trackId);
    }

    private void SaveTrackPreference(MediaKind kind, int? trackId, IReadOnlyList<MediaTrack> availableSameKind)
    {
        // Skip non-local sources — TryGetDirectoryKey returns null for URIs (http/smb/…) where "directory" doesn't have a useful meaning. Skip silently; the user just doesn't get persistence for streaming sources.
        if (currentDirectoryKey == null)
        {
            PrefsLog($"save skipped (no directory key): kind={kind} trackId={trackId?.ToString() ?? "none"}");
            return;
        }
        // Known limitation: identity (title/lang/external/index) is captured from the VM mirror at click time, not re-read from mpv. If mpv mutates track-list between the menu render and this call AND reuses the same id for a different track, we'd save the wrong identity. The user would hear the new track (mpv applies sid=<id>), then re-pick something else; the bad save gets overwritten on the next user choice. Self-correcting and rare; documented rather than fixed because the fix would require a synchronous round-trip to the dispatcher worker.
        var pref = TrackMatcher.FromUserChoice(trackId, availableSameKind);
        if (pref == null)
        {
            // The chosen id no longer exists in the available list — track was removed in the same tick the user clicked. Skip the save rather than persist a malformed preference.
            PrefsLog($"save skipped (id not in current track list): kind={kind} trackId={trackId} availableCount={availableSameKind.Count}");
            return;
        }
        trackPreferences.Record(currentDirectoryKey, kind, pref);
        PrefsLog($"saved: dir={currentDirectoryKey} kind={kind} isNone={pref.IsNone} title={pref.Title ?? "<null>"} lang={pref.Lang ?? "<null>"} external={pref.External} extFile={pref.ExternalFilename ?? "<null>"} index={pref.IndexInKind}");
    }

    // Direct path/URI load, bypassing the file picker. Used by drag-and-drop. Accepts whatever libmpv accepts: a local filesystem path, or a remote URI (http://, https://, smb://, …).
    public void OpenFile(string pathOrUri)
    {
        // Save the OUTGOING file's position synchronously, before mutating any per-file state. This is the primary "user moved on from this file" save trigger and replaces the obvious-but-broken alternative of subscribing to FileEnded: mpv's EndFile event delivers its reason field via a separate struct that the existing MpvClient.Dispatch reads incorrectly (it reads evt.Error, not the mpv_event_end_file.reason behind evt.Data) — and even with that fixed, at EOF with keep-open=yes time-pos parks at duration so the near-end filter would always elide the save. Saving here, on the user's deliberate "open new file" action, dodges both problems.
        SaveCurrentPositionIfEligible();

        recentFiles.Record(pathOrUri);
        // Resolve and cache the directory key NOW so Select* calls between LoadFile and the next OpenFile can reach it. URIs return null and disable persistence for this file.
        currentDirectoryKey = TrackPreferences.TryGetDirectoryKey(pathOrUri);
        currentFilePath = pathOrUri;
        currentFileLoaded = false;
        lastSavedPositionSeconds = 0;
        // Look up the saved position only for local files. URI sources don't accumulate positions, so skipping the GetPosition call also keeps test fakes' GetPositionCalls clean.
        pendingResumePosition = TrackPreferences.IsLocalFilesystemPath(pathOrUri) ? recentFiles.GetPosition(pathOrUri) : null;
        PrefsLog($"OpenFile: pathOrUri={pathOrUri} → directoryKey={currentDirectoryKey ?? "<null>"}, resumePos={(pendingResumePosition?.ToString() ?? "<null>")}");
        playback.LoadFile(pathOrUri);
    }

    [RelayCommand]
    private void PlayPause()
    {
        // Catches the no-file-loaded case (the user-visible bug: clicking play before opening anything flipped the icon to "pause" without anything to play). mpv accepts pause toggles pre-load fine — this is purely a UX gate. Lives in the VM rather than only on the button so the Space-key path through PlayPauseCommand is also covered. Also incidentally gates pause on live streams / unseekable inputs that report duration=0; if anyone needs pause-on-livestream this should become a HasFile latched on FileLoaded.
        if (Duration <= TimeSpan.Zero)
        {
            return;
        }
        playback.TogglePause();
    }

    public void OnRenderContextReady()
    {
        if (initialFileLoaded)
        {
            return;
        }
        if (!string.IsNullOrEmpty(InitialFile))
        {
            // Route through OpenFile so the command-line file is recorded in recents the same way drag-and-drop and the file picker are.
            OpenFile(InitialFile);
        }
        initialFileLoaded = true;
    }

    // Seek to a normalized position in [0, 1]. View calls this on every user change to the scale; mpv's position catches up and pushes SeekValue back on the next playback tick, which is fine — the scale follows playback when the user isn't pressing it.
    public void SeekTo(double normalizedPosition)
    {
        playback.Seek(normalizedPosition * Duration.TotalSeconds);
    }

    private void OnPlaybackFileLoaded()
    {
        // Open the apply window for this file load, reset per-kind tracking, and apply immediately. mpv discovers tracks BEFORE firing FileLoaded — the only TracksReloaded that carries the new file's tracks lands ahead of FileLoaded, so waiting for "TracksReloaded after FileLoaded" misses it entirely. By FileLoaded time the VM mirror is populated; apply runs against it. The TracksReloaded retry path below still handles any post-FileLoaded track-list changes (e.g., a sub auto-loaded later, or the user adding one via menu — though the per-kind gate prevents re-applying kinds already settled).
        inApplyWindow = true;
        appliedKindsForCurrentFile.Clear();
        currentFileLoaded = true;
        PrefsLog($"FileLoaded: directoryKey={currentDirectoryKey ?? "<null>"}, applying preferences");
        ApplyTrackPreferences();
        ApplyResumePositionIfPending();
    }

    // Seek to the saved position once both FileLoaded has fired AND duration is known. mpv's emission order between MPV_EVENT_FILE_LOADED and the property-change for `duration` isn't contractually guaranteed — track lists arrive before FileLoaded (per OnPlaybackFileLoaded's existing comment) but duration may not — so we re-attempt from both events and only commit pendingResumePosition (clear it) when we have enough info to make a final decision. Brief (~50–150ms) flash of position-0 playback before the seek lands is a known cost; alternatives (mpv's `loadfile … start=N` option, pause-before-loadfile / unpause-after-seek) were rejected for fragility. Filter on (0, duration - NearEndIgnoreSeconds): zero saves a redundant seek when the natural start is fine, near-end avoids the "watched to completion → reload jumps to EOF" footgun.
    private void ApplyResumePositionIfPending()
    {
        if (!currentFileLoaded)
        {
            return;
        }
        if (!pendingResumePosition.HasValue)
        {
            return;
        }
        double duration = playback.DurationSeconds;
        if (duration <= 0)
        {
            return;
        }
        // Both gates passed — commit one way or another. Clear pendingResumePosition so a subsequent duration update doesn't re-apply.
        var resume = pendingResumePosition.Value;
        pendingResumePosition = null;
        if (resume <= 0 || resume >= duration - NearEndIgnoreSeconds)
        {
            PrefsLog($"resume: skipped (pos={resume} duration={duration} — at boundary)");
            return;
        }
        PrefsLog($"resume: seeking to {resume} (duration={duration})");
        playback.Seek(resume);
        // Prime the periodic-save throttle so the first save after resume is at resume+10, not at 10.
        lastSavedPositionSeconds = resume;
    }

    // Single eligibility-checked save path. All four save triggers (periodic, pause-edge, OpenFile-outgoing, Dispose) funnel through here so the gate stays consistent. After a successful save, lastSavedPositionSeconds is updated so the periodic throttle's next milestone is measured from this save, not the previous one.
    private void SaveCurrentPositionIfEligible()
    {
        if (currentFilePath == null)
        {
            return;
        }
        if (!currentFileLoaded)
        {
            return;
        }
        if (!TrackPreferences.IsLocalFilesystemPath(currentFilePath))
        {
            return;
        }
        double duration = playback.DurationSeconds;
        if (duration <= 0)
        {
            return;
        }
        double position = playback.PositionSeconds;
        if (position >= duration - NearEndIgnoreSeconds)
        {
            return;
        }
        recentFiles.RecordPosition(currentFilePath, position);
        lastSavedPositionSeconds = position;
        PrefsLog($"position saved: file={currentFilePath} pos={position:F3} duration={duration:F3}");
    }

    private void OnPlaybackTracksReloaded()
    {
        PrefsLog($"TracksReloaded: inApplyWindow={inApplyWindow} V={VideoTracks.Count} A={AudioTracks.Count} S={SubtitleTracks.Count}");
        if (!inApplyWindow)
        {
            return;
        }
        ApplyTrackPreferences();
    }

    private void ApplyTrackPreferences()
    {
        if (currentDirectoryKey == null)
        {
            return;
        }
        TryApplyForKind(MediaKind.Video, VideoTracks, playback.SetVideo);
        TryApplyForKind(MediaKind.Audio, AudioTracks, playback.SetAudio);
        TryApplyForKind(MediaKind.Subtitle, SubtitleTracks, playback.SetSubtitle);
    }

    private void TryApplyForKind(MediaKind kind, IReadOnlyList<MediaTrack> available, Action<int?> setter)
    {
        if (appliedKindsForCurrentFile.Contains(kind))
        {
            // Already applied for this file; don't re-fire. Critical for the "user adds an external sub via menu after a successful sub apply" case — without this gate, the resulting TracksReloaded would re-apply the saved subtitle preference and undo the user's just-added sub.
            return;
        }
        var saved = trackPreferences.Get(currentDirectoryKey!, kind);
        if (saved == null)
        {
            // No preference stored — mark as "applied" (i.e., done with this kind for this file) so we don't keep querying SQLite on every TracksReloaded.
            PrefsLog($"apply {kind}: no saved preference for dir={currentDirectoryKey}");
            appliedKindsForCurrentFile.Add(kind);
            return;
        }
        if (TrackMatcher.TryMatch(saved, available, out var trackId))
        {
            // Bypass the VM Select* layer to avoid re-saving the same preference we just read back.
            setter(trackId);
            appliedKindsForCurrentFile.Add(kind);
            PrefsLog($"apply {kind}: matched → setting trackId={trackId?.ToString() ?? "none"} (saved title={saved.Title ?? "<null>"} lang={saved.Lang ?? "<null>"})");
        }
        else
        {
            PrefsLog($"apply {kind}: no match in available({available.Count}) for saved title={saved.Title ?? "<null>"} lang={saved.Lang ?? "<null>"} — will retry on next TracksReloaded");
        }
        // No-match leaves the kind unmarked so the next TracksReloaded retries — covers mpv lazy-loading external tracks that match the preference.
    }

    private void OnPlaybackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IPlayback.PositionSeconds):
                Position = TimeSpan.FromSeconds(playback.PositionSeconds);
                if (playback.DurationSeconds > 0)
                {
                    SeekValue = Math.Clamp(playback.PositionSeconds / playback.DurationSeconds, 0, 1);
                }
                // Periodic save during playback: save once we've advanced PeriodicSaveThresholdSeconds since the previous save. Position-delta-based so it catches both forward playback AND large jumps from a recent (untracked) seek. The !IsPaused gate is purely a "skip cheap work while the user is paused" optimization — at EOF with keep-open=yes mpv keeps PositionSeconds at duration with IsPaused=false, so the gate doesn't catch that case; the near-end position filter inside SaveCurrentPositionIfEligible does.
                if (!playback.IsPaused
                    && Math.Abs(playback.PositionSeconds - lastSavedPositionSeconds) >= PeriodicSaveThresholdSeconds)
                {
                    SaveCurrentPositionIfEligible();
                }
                break;
            case nameof(IPlayback.DurationSeconds):
                Duration = TimeSpan.FromSeconds(playback.DurationSeconds);
                // Late-arriving duration retry: when FileLoaded fired before the duration property-change, ApplyResumePositionIfPending bailed without clearing pendingResumePosition. Now that we have a duration, retry.
                ApplyResumePositionIfPending();
                break;
            case nameof(IPlayback.IsPaused):
                // Detect the rising edge (play → pause) and save then. Falling edge (pause → play) carries no new positional information beyond what the periodic save will catch within the next PeriodicSaveThresholdSeconds.
                bool nowPaused = playback.IsPaused;
                if (nowPaused && !wasPaused)
                {
                    SaveCurrentPositionIfEligible();
                }
                wasPaused = nowPaused;
                IsPaused = nowPaused;
                break;
            case nameof(IPlayback.VideoTracks):
                VideoTracks = playback.VideoTracks;
                break;
            case nameof(IPlayback.AudioTracks):
                AudioTracks = playback.AudioTracks;
                break;
            case nameof(IPlayback.SubtitleTracks):
                SubtitleTracks = playback.SubtitleTracks;
                break;
            case nameof(IPlayback.Chapters):
                Chapters = playback.Chapters;
                break;
            case nameof(IPlayback.CurrentVideoId):
                CurrentVideoId = playback.CurrentVideoId;
                break;
            case nameof(IPlayback.CurrentAudioId):
                CurrentAudioId = playback.CurrentAudioId;
                break;
            case nameof(IPlayback.CurrentSubtitleId):
                CurrentSubtitleId = playback.CurrentSubtitleId;
                break;
        }
    }

    public void Dispose()
    {
        // Final position save before tear-down. Window-close path runs viewModel.Dispose() before playback.Dispose() (see MainWindow.OnWindowCloseRequest), and the SQLite connection lives in Program.cs's `using var stateDb` which outlives both — so the DB call here is safe.
        SaveCurrentPositionIfEligible();
        playback.PropertyChanged -= OnPlaybackPropertyChanged;
        playback.FileLoaded -= OnPlaybackFileLoaded;
        playback.TracksReloaded -= OnPlaybackTracksReloaded;
    }
}
