using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
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
    private readonly IUrlDownloader urlDownloader;
    private readonly IUrlPrompt urlPrompt;
    // URLs that came in through the Open URL flow (or were expanded from a YouTube playlist via the same flow). LoadCurrentItem checks this set to decide whether to route an item through yt-dlp or hand it straight to mpv. Drag-drop / command-line URLs are NOT added here in v1, so they continue to go directly to mpv (which generally fails for YouTube but works for direct streams). Documented v1 gap; if drag-drop YouTube URLs become a feature ask, the right fix is a discriminated PlaylistItem type rather than growing this set.
    private readonly HashSet<string> urlsRequiringDownload = new();
    // CTS for the in-flight URL download (if any). LoadCurrentItem cancels and replaces this synchronously before mutating any per-load state, so a stale download A racing a new load B can't clobber B's playback. The cancellation observer in LoadUrlAsync also re-checks this field is still its CTS at completion time — defense in depth against the cancel-callback racing the LoadFile call.
    private CancellationTokenSource? activeDownloadCts;
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
    // Previous IsEofReached value, for rising-edge detection driving playlist auto-advance. LoadCurrentItem proactively resets this to false (paired with Playback.LoadFile's synchronous IsEofReached reset) so the gate is aligned at file-load boundaries. Without that pairing, a user clicking row N after the playlist's last item naturally ended would have the rising edge already "consumed", and the new file's eventual EOF would still register as a rising edge — fine in that direction; the worse direction is the stale carry-over described on Playback.LoadFile (auto-advancing past a just-loaded file because IsEofReached was already true at observation time). The three-gate handler below adds defense in depth.
    private bool wasEofReached;
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

    [ObservableProperty]
    private double volume = 100;

    [ObservableProperty]
    private bool isMuted;

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

    // Owned playlist model. The view (PlaylistPanel) subscribes to Playlist.Changed and rebuilds rows wholesale; LoadPaths / PlayPlaylistItem / the auto-advance handler are the only mutators. Public so the panel can read Items / CurrentIndex on every Changed fire — Playlist itself is a plain class, no GTK dependency, fully unit-testable.
    public Playlist Playlist { get; } = new Playlist();

    public ViewModelMain(IPlayback playback, IFilePicker filePicker, IRecentFiles recentFiles, ITrackPreferences trackPreferences, IUrlDownloader urlDownloader, IUrlPrompt urlPrompt)
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
        if (urlDownloader == null)
        {
            throw new ArgumentNullException(nameof(urlDownloader));
        }
        if (urlPrompt == null)
        {
            throw new ArgumentNullException(nameof(urlPrompt));
        }
        this.playback = playback;
        this.filePicker = filePicker;
        this.recentFiles = recentFiles;
        this.trackPreferences = trackPreferences;
        this.urlDownloader = urlDownloader;
        this.urlPrompt = urlPrompt;
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
    private async Task OpenUrlAsync()
    {
        // Gate before prompting. yt-dlp not available is a hard stop for this flow — we don't have a sensible fallback (mpv-direct doesn't handle YouTube), so the right UX is a clear "install yt-dlp" message rather than letting the user type a URL and *then* failing.
        if (!urlDownloader.IsAvailable())
        {
            urlPrompt.ShowError(
                "yt-dlp not found",
                "Install yt-dlp to play URLs (e.g. `pip install yt-dlp`, or your distribution's package manager). Once installed, retry without restarting Vomplayer.");
            return;
        }
        var url = await urlPrompt.PromptForUrlAsync("Open URL");
        if (string.IsNullOrEmpty(url))
        {
            return;
        }
        IReadOnlyList<string> entries;
        // Cap the probe at 30 seconds. yt-dlp's --flat-playlist usually returns in well under a second; a hung probe (network outage, extractor regression, mid-download server stall) shouldn't leave the UI stuck with no recovery. Cancellation tears down the spawned yt-dlp via YtDlpDownloader.ProbeAsync's existing kill-on-cancel path.
        using (var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            try
            {
                entries = await urlDownloader.ProbeAsync(url, probeCts.Token);
            }
            catch (OperationCanceledException) when (probeCts.IsCancellationRequested)
            {
                urlPrompt.ShowError("Failed to open URL", "Probe timed out after 30 seconds. The URL may be unreachable or the extractor may be hanging.");
                return;
            }
            catch (Exception ex)
            {
                urlPrompt.ShowError("Failed to open URL", ex.Message);
                return;
            }
        }
        if (entries.Count == 0)
        {
            urlPrompt.ShowError("Failed to open URL", "yt-dlp returned no entries for this URL.");
            return;
        }
        // Reset the routing set to exactly the URLs from this OpenUrl invocation. Bounds the set's size to the current playlist (post-probe) so it doesn't grow over the session, and prevents a previously-OpenUrl'd URL from re-entering the download path if the user later types it as a local file string. (See urlsRequiringDownload field comment for the broader v1-gap rationale.)
        urlsRequiringDownload.Clear();
        foreach (var u in entries)
        {
            urlsRequiringDownload.Add(u);
        }
        LoadPaths(entries, replace: true);
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

    // Single-file public entry point — back-compat for the existing picker / command-line / single-drop callers. Routes through LoadPaths so the playlist mirror always reflects what's playing; the user-spec is "single file dropped REPLACES the playlist", and OpenFile is the morally-equivalent picker path.
    public void OpenFile(string pathOrUri)
    {
        if (pathOrUri == null)
        {
            throw new ArgumentNullException(nameof(pathOrUri));
        }
        LoadPaths(new[] { pathOrUri }, replace: true);
    }

    // Multi-file entry point. replace=true wipes the playlist and starts at index 0; replace=false appends, kicking off playback only if the playlist was previously empty (otherwise we just queue items behind the current one). An empty input is a no-op — covers zero-match directory drops without orphaning currently-playing media.
    public void LoadPaths(IReadOnlyList<string> paths, bool replace)
    {
        if (paths == null)
        {
            throw new ArgumentNullException(nameof(paths));
        }
        if (paths.Count == 0)
        {
            return;
        }
        bool kickOff;
        if (replace)
        {
            Playlist.Replace(paths);
            kickOff = true;
        }
        else
        {
            bool wasEmpty = Playlist.Items.Count == 0;
            Playlist.Append(paths);
            kickOff = wasEmpty;
        }
        if (kickOff)
        {
            LoadCurrentItem();
        }
    }

    // User clicked / activated a row in the playlist panel. SetCurrent + LoadCurrentItem — the former is a pure state mutation, the latter does the actual playback transition.
    public void PlayPlaylistItem(int index)
    {
        Playlist.SetCurrent(index);
        LoadCurrentItem();
    }

    // The shared per-file setup: outgoing-position save, recents.Record, currentDirectoryKey resolve, currentFilePath set, lastSavedPositionSeconds reset, pendingResumePosition lookup, then playback.LoadFile. Called from LoadPaths (when starting / replacing a playlist), PlayPlaylistItem, and the auto-advance handler. Idempotent w.r.t. the playlist itself — the playlist mutation already happened.
    //
    // Why save-on-LoadCurrentItem instead of subscribing to FileEnded: mpv's EndFile event delivers its reason field via a separate struct that the existing MpvClient.Dispatch reads incorrectly (it reads evt.Error, not the mpv_event_end_file.reason behind evt.Data) — and even with that fixed, at EOF with keep-open=yes time-pos parks at duration so the near-end filter would always elide the save. Saving here, on the user's deliberate "open new file" action OR on auto-advance, dodges both problems. Auto-advance specifically: at natural EOF position ≈ duration so SaveCurrentPositionIfEligible's near-end gate elides the save anyway — verified by the AutoAdvanceElidesNearEndOutgoingSave test.
    private void LoadCurrentItem()
    {
        if (Playlist.CurrentIndex < 0 || Playlist.CurrentIndex >= Playlist.Items.Count)
        {
            return;
        }
        string pathOrUri = Playlist.Items[Playlist.CurrentIndex];

        // Cancel any in-flight URL download from a previous LoadCurrentItem before mutating per-load state. If the user clicks a different playlist row mid-download, we don't want the late completion of the old download to call playback.LoadFile against the new file's slot. CTS swap is synchronous, so by the time we set currentFilePath below the previous download's continuation will have observed cancellation. Dispose is idempotent on CTS — LoadUrlAsync's `finally` also disposes its CTS, but a second Dispose is a guaranteed no-op so we don't need to guard against the double call.
        var previousCts = activeDownloadCts;
        activeDownloadCts = null;
        if (previousCts != null)
        {
            previousCts.Cancel();
            previousCts.Dispose();
        }

        SaveCurrentPositionIfEligible();

        recentFiles.Record(pathOrUri);
        // Resolve and cache the directory key NOW so Select* calls between LoadFile and the next load can reach it. URIs return null and disable persistence for this file.
        currentDirectoryKey = TrackPreferences.TryGetDirectoryKey(pathOrUri);
        currentFilePath = pathOrUri;
        currentFileLoaded = false;
        lastSavedPositionSeconds = 0;
        // Reset the eof-rising-edge mirror to match the synchronous IsEofReached=false that Playback.LoadFile is about to perform. Ensures the next true→ transition we observe is treated as a rising edge against this file, not against whatever the previous file's tail was.
        wasEofReached = false;
        // Look up the saved position only for local files. URI sources don't accumulate positions, so skipping the GetPosition call also keeps test fakes' GetPositionCalls clean.
        pendingResumePosition = TrackPreferences.IsLocalFilesystemPath(pathOrUri) ? recentFiles.GetPosition(pathOrUri) : null;
        PrefsLog($"LoadCurrentItem: pathOrUri={pathOrUri} → directoryKey={currentDirectoryKey ?? "<null>"}, resumePos={(pendingResumePosition?.ToString() ?? "<null>")}");

        if (urlsRequiringDownload.Contains(pathOrUri))
        {
            // Async path — yt-dlp downloads to local cache, then mpv loads the file. The CTS we install here is the handle the user's Cancel button binds to in the progress dialog.
            var cts = new CancellationTokenSource();
            activeDownloadCts = cts;
            // Fire-and-forget — exceptions are caught inside LoadUrlAsync and surfaced via the prompt's ShowError. Keeping LoadCurrentItem synchronous matches every existing caller (LoadPaths, PlayPlaylistItem, the auto-advance handler) which can't await.
            _ = LoadUrlAsync(pathOrUri, cts);
            return;
        }
        playback.LoadFile(pathOrUri);
    }

    private async Task LoadUrlAsync(string url, CancellationTokenSource cts)
    {
        UrlProgressHandle? progressHandle = null;
        try
        {
            progressHandle = urlPrompt.ShowDownloadProgress("Downloading", cts);
            string localPath = await urlDownloader.DownloadAsync(url, progressHandle.Progress, cts.Token);

            // Race guard (see "CTS swap" comment in LoadCurrentItem). If the user advanced to another row during the download, currentFilePath has moved and our late LoadFile would clobber the new playback. The activeDownloadCts identity check catches the same case if currentFilePath happens to equal the URL again (e.g., user re-loads the same URL).
            if (cts.Token.IsCancellationRequested
                || !ReferenceEquals(activeDownloadCts, cts)
                || currentFilePath != url)
            {
                return;
            }
            playback.LoadFile(localPath);
        }
        catch (OperationCanceledException)
        {
            // User clicked Cancel, or a newer LoadCurrentItem cancelled us. Either way, no playback to start.
        }
        catch (Exception ex)
        {
            // Show only if we're still the active load — a stale download's failure shouldn't pop up after the user has moved on.
            if (ReferenceEquals(activeDownloadCts, cts))
            {
                urlPrompt.ShowError("Download failed", ex.Message);
            }
        }
        finally
        {
            progressHandle?.Closer.Dispose();
            // Only clear the field if we're still its CTS — a newer LoadCurrentItem may have already swapped in its own.
            if (ReferenceEquals(activeDownloadCts, cts))
            {
                activeDownloadCts = null;
            }
            cts.Dispose();
        }
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

    public void SeekRelative(double seconds)
    {
        playback.SeekRelative(seconds);
    }

    public void StepFrameForward()
    {
        playback.StepFrameForward();
    }

    public void StepFrameBack()
    {
        playback.StepFrameBack();
    }

    public void StepChapter(int delta)
    {
        playback.StepChapter(delta);
    }

    public void SetVolume(double percent)
    {
        playback.SetVolume(percent);
    }

    public void AdjustVolume(double deltaPercent)
    {
        playback.AdjustVolume(deltaPercent);
    }

    public void ToggleMute()
    {
        playback.ToggleMute();
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
            case nameof(IPlayback.IsEofReached):
                // Three gates guard the playlist auto-advance:
                //   1. Rising edge (!wasEof && nowEof). mpv may fire eof-reached=true repeatedly while parked at EOF (e.g., property re-observe on track-list updates); we only want to advance once per natural EOF, not every time mpv re-asserts the flag.
                //   2. currentFileLoaded. A stale eof-reached=true from the prior file's tail can land in the dispatcher queue after LoadFile dispatches but before the new file's FileLoaded fires; without this gate the spurious rising edge would skip the just-loaded file.
                //   3. DurationSeconds > 0. Live streams may oscillate eof-reached without a meaningful "next item" semantic.
                // All three protect different scenarios; don't skimp on any.
                bool nowEof = playback.IsEofReached;
                if (nowEof && !wasEofReached && currentFileLoaded && playback.DurationSeconds > 0)
                {
                    var next = Playlist.Advance();
                    if (next != null)
                    {
                        LoadCurrentItem();
                    }
                }
                wasEofReached = nowEof;
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
            case nameof(IPlayback.Volume):
                Volume = playback.Volume;
                break;
            case nameof(IPlayback.IsMuted):
                IsMuted = playback.IsMuted;
                break;
        }
    }

    public void Dispose()
    {
        // Final position save before tear-down. Window-close path runs viewModel.Dispose() before playback.Dispose() (see MainWindow.OnWindowCloseRequest), and the SQLite connection lives in Program.cs's `using var stateDb` which outlives both — so the DB call here is safe.
        SaveCurrentPositionIfEligible();
        // Cancel any in-flight URL download so the spawned yt-dlp process exits before the GTK main loop tears down. LoadUrlAsync's finally block disposes the CTS — we only call Cancel here.
        var cts = activeDownloadCts;
        activeDownloadCts = null;
        if (cts != null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
        playback.PropertyChanged -= OnPlaybackPropertyChanged;
        playback.FileLoaded -= OnPlaybackFileLoaded;
        playback.TracksReloaded -= OnPlaybackTracksReloaded;
    }
}
