using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Vomplayer.Playback;
using Vomplayer.Services;
using Vomplayer.UserData;
using Vomplayer.Wayland;

namespace Vomplayer.ViewModels;

// Encapsulates one video's state: its Playback, its Playlist, its per-instance HDR/VRR policy, and all per-load tracking. `ViewModelMain` is a coordinator that owns one or two of these — Primary always, Secondary lazily under PiP — and routes transport across them.
//
// AutoAdvanceEnabled gates the per-context EOF-rising-edge auto-advance. Default true preserves single-video behavior; the coordinator flips it to false on both contexts when a sibling exists, taking advance dispatch over itself (lockstep gate).
public sealed partial class VideoContext : ObservableObject, IDisposable
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

    private static readonly bool LogHdr = Environment.GetEnvironmentVariable("VOMPL_LOG_HDR") == "1";

    // The IPlayback this context drives. Owned: VideoContext.Dispose disposes it. Callers (Program.cs for Primary, PipController for Secondary) construct the Playback and hand it over; once handed in, lifetime is the context's.
    private readonly IPlayback playback;
    private readonly IFilePicker filePicker;
    private readonly IRecentFiles recentFiles;
    private readonly ITrackPreferences trackPreferences;
    // Owns the full yt-dlp interaction surface for this stream — interactive prompt + probe, the URL-routing set, and the download lifecycle. Per-context: an Open URL on Primary populating Secondary's set would be wrong.
    private readonly UrlLoadCoordinator urlLoad;
    // Currently-attached IHdrSink (the per-context VideoSurface on Wayland; null on the GLArea fallback path or when no surface is attached yet). AttachHdrSink populates this; DetachHdrSink clears it. Read by ApplyHdrPolicy and OnCurrentOutputHdrChanged.
    private IHdrSink? hdrSink;
    // Currently-attached IVrrSink (the per-context VideoSurface on Wayland; null otherwise). AttachVrrSink populates; DetachVrrSink clears. Drives ApplyVrrPolicy in combination with playback.VideoFps and playback.IsSourceFpsTrusted.
    private IVrrSink? vrrSink;
    // Latest value from playback.SourceHdrChanged. Playback fires on transitions only, not on every frame, so caching here lets ApplyHdrPolicy combine it with the current output bit on output-side changes too.
    private bool lastSourceHdr;
    // Most recent VrrDecision from ApplyVrrPolicy. Surfaced for the diagnostic overlay; set unconditionally on every apply so the overlay reflects the active policy decision regardless of whether the underlying mpv command was a no-op.
    public VrrDecision LastVrrDecision { get; private set; } = new(1, 0, "no policy run yet");
    // The directory key (per TrackPreferences.TryGetDirectoryKey) for the most recent OpenFile target. Null when the latest file isn't a local-filesystem path (URI sources don't participate in per-directory preferences). Mutated only on OpenFile and read on Select* (save) and the FileLoaded/TracksReloaded apply path.
    //
    // Known limitation (rapid file swap): OpenFile(B) overwrites this before FileLoaded for A arrives if the user opens two files in quick succession. The first-file's TracksReloaded then matches against B's directory preferences. Rare in practice, self-correcting on the next load. Fixing properly would require correlating mpv's `path` property with the load that triggered it.
    private string? currentDirectoryKey;
    // The full path-or-URI for the most recent OpenFile target. Distinct from currentDirectoryKey because the position layer keys on the file itself, not its directory. Set in OpenFile alongside currentDirectoryKey, used by every save trigger to identify which row to update. Null pre-OpenFile and after the outgoing-file save for a non-local source clears it. Exposed as ObservableProperty so the stream-selector toolbar can show the file's basename in its button label and refresh on file change.
    [ObservableProperty]
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
    // Set by RestorePlaylist while it loads a file the user did NOT deliberately open (startup autoload, Recent menu click). LoadCurrentItem checks this and skips recentFiles.Record so the file's `last_opened` timestamp reflects the user's last actual play, not the system's restore. Position-resume still works (it reads position_seconds, which Record doesn't clear).
    private bool restoringPlaylist;

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

    // Computed: currentFileLoaded && playback.IsEofReached && DurationSeconds > 0. Mirrors the three-gate auto-advance condition. The coordinator subscribes to PropertyChanged on this to drive lockstep advance when both contexts are at playable EOF.
    [ObservableProperty]
    private bool isAtPlayableEof;

    // Mirror of IPlayback.VideoAspect. Null until the source's dwidth/dheight have been observed. The PiP layout subscribes to PropertyChanged on this to recompute the secondary widget's size whenever the loaded source's display aspect changes.
    [ObservableProperty]
    private double? videoAspect;

    // Mirror of IPlayback.VideoFps (container frame rate). Null when no file is loaded, no video stream is present, or mpv hasn't reported a usable value. Read by the coordinator's sync-mode StepFrame to compute the absolute-seconds delta of one Primary frame (1/Primary.VideoFps).
    [ObservableProperty]
    private double? videoFps;

    // Mirror of IPlayback.MediaTitle. Null when no file is loaded. Drives the main window's title bar.
    [ObservableProperty]
    private string? mediaTitle;

    // When false, the per-context EOF-rising-edge auto-advance is suppressed. The coordinator flips this off on both contexts when a sibling exists, owning advance dispatch itself (lockstep). Default true preserves single-video behavior when this is the only live context.
    public bool AutoAdvanceEnabled { get; set; } = true;

    // The current playlist has at least one more item after CurrentIndex. Read-only; coordinator uses this as one of the lockstep advance gates.
    public bool HasNextItem
    {
        get
        {
            return Playlist.CurrentIndex >= 0 && Playlist.CurrentIndex < Playlist.Items.Count - 1;
        }
    }

    // Owned playlist model. The view (PlaylistPanel) subscribes to Playlist.Changed and rebuilds rows wholesale; LoadPaths / PlayPlaylistItem / the auto-advance handler are the only mutators. Public so the panel can read Items / CurrentIndex on every Changed fire — Playlist itself is a plain class, no GTK dependency, fully unit-testable.
    public Playlist Playlist { get; } = new Playlist();

    // Outcome of the most recent ApplyHdrPolicy. Kept as a three-state enum (rather than a bool) so the stderr log on transition into Failed can distinguish "compositor refused" from "intentional SDR" — the diagnostic overlay collapses every non-Hdr state to "SDR" and doesn't care about the internal distinction, but ApplyHdrPolicy does.
    public enum HdrActiveState
    {
        // Subsurface untagged or explicitly SDR-tagged. Default pre-first-apply, or the explicit outcome when source or display is SDR.
        Sdr,
        // HDR tagging applied to the subsurface and mpv advanced to PQ targets.
        Hdr,
        // HDR was requested (source PQ/HLG) but the sink's SetHdr(true) returned non-zero. Pixels scan out as SDR.
        Failed,
    }

    public HdrActiveState ActiveHdrState { get; private set; } = HdrActiveState.Sdr;

    // The currently-attached HDR sink, or null if none. Exposed so the diagnostic overlay can read CurrentOutputIsHdr without a separate VideoSurface reference.
    public IHdrSink? HdrSink
    {
        get
        {
            return hdrSink;
        }
    }

    // The currently-attached VRR sink, or null. Exposed so the diagnostic overlay can read the current output's VRR window without a separate VideoSurface reference.
    public IVrrSink? VrrSink
    {
        get
        {
            return vrrSink;
        }
    }

    // The Playback this context drives. Exposed for the coordinator's transport fan-out (calls playback methods on each context's Playback) and for diagnostic-overlay readouts.
    public IPlayback Playback
    {
        get
        {
            return playback;
        }
    }

    public VideoContext(IPlayback playback, IFilePicker filePicker, IRecentFiles recentFiles, ITrackPreferences trackPreferences, IUrlDownloader urlDownloader, IUrlPrompt urlPrompt)
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
        this.urlLoad = new UrlLoadCoordinator(urlDownloader, urlPrompt);
        this.playback.PropertyChanged += OnPlaybackPropertyChanged;
        this.playback.FileLoaded += OnPlaybackFileLoaded;
        this.playback.TracksReloaded += OnPlaybackTracksReloaded;
        // Source-HDR transitions are tracked even when no sink is attached yet — lastSourceHdr is the input to ApplyHdrPolicy whenever a sink does attach.
        this.playback.SourceHdrChanged += OnSourceHdrChanged;
        // VRR trust transitions: re-run ApplyVrrPolicy on every flip. Sticky-untrusted within a file load means at most one (true→false) flip per file; the inverse (re-trust on a fresh LoadFile / new container-fps land) also fires.
        this.playback.IsSourceFpsTrustedChanged += OnIsSourceFpsTrustedChanged;
    }

    public async Task OpenAsync()
    {
        var path = await filePicker.PickVideoFileAsync("Open media");
        if (path == null)
        {
            return;
        }
        // Route through OpenFile so the picker path goes through the same recents-record + currentDirectoryKey-resolve dance as drag-and-drop and command-line invocation. Without this, picker-opened files don't get a directory key set, and SaveTrackPreference silently no-ops on every menu pick.
        OpenFile(path);
    }

    // The drag-drop / command-line URL paths do NOT route through here, so they don't get into the urlsRequiringDownload set and continue to go directly to mpv (which generally fails for YouTube but works for direct streams). Documented v1 gap; if drag-drop YouTube URLs become a feature ask, the right fix is a discriminated PlaylistItem type rather than growing this method's reach.
    public async Task OpenUrlAsync()
    {
        var entries = await urlLoad.OpenUrlInteractiveAsync();
        if (entries == null)
        {
            return;
        }
        urlLoad.SetUrlsRequiringDownload(entries);
        // Single-video URL: behave as today — replace playlist and autoplay (download starts in LoadCurrentItem). Playlist URL: populate the playlist but DO NOT auto-load the first entry. The user clicks a row when ready, and LoadCurrentItem then kicks off the download for just that row. This keeps a paste-of-a-50-video-playlist from immediately downloading anything. Cancel any in-flight download from a previous load so a stale yt-dlp doesn't keep running in the background after the user reframes their intent with a new OpenUrl.
        if (entries.Count == 1)
        {
            LoadPaths(entries, replace: true);
        }
        else
        {
            urlLoad.CancelActive();
            Playlist.Replace(entries);
        }
    }

    public async Task LoadAudioAsync()
    {
        var path = await filePicker.PickAudioFileAsync("Open audio file");
        if (path == null)
        {
            return;
        }
        playback.LoadAudio(path);
    }

    public async Task LoadSubtitleAsync()
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
            LoadCurrentItem(startPaused: false);
        }
    }

    // User clicked / activated a row in the playlist panel. SetCurrent + LoadCurrentItem — the former is a pure state mutation, the latter does the actual playback transition.
    public void PlayPlaylistItem(int index)
    {
        Playlist.SetCurrent(index);
        LoadCurrentItem(startPaused: false);
    }

    // Restore a saved playlist into this context. Replaces the items list, sets CurrentIndex, and kicks off the load — startPaused threads through LoadCurrentItem to Playback.LoadFile so the file loads paused at the resume position (true) or auto-plays (false). Used by ViewModelMain.LoadFromSaved (Recent menu click + startup autoload). Two Changed events fire (Replace, then SetCurrent if currentIndex != 0); PlaylistAutosave's loading flag swallows both so restoration doesn't re-write the row with placeholder filenames.
    //
    // Distinct from LoadPaths(replace:true) because LoadPaths always loads from index 0 (no resume position to honor) and the user-initiated open is implicitly "play". Restoration honors the saved CurrentIndex AND the pause-on-restore intent.
    public void RestorePlaylist(IReadOnlyList<string> items, int currentIndex, bool startPaused)
    {
        if (items == null)
        {
            throw new ArgumentNullException(nameof(items));
        }
        if (items.Count == 0)
        {
            return;
        }
        int clamped = currentIndex >= 0 && currentIndex < items.Count ? currentIndex : 0;
        restoringPlaylist = true;
        try
        {
            Playlist.Replace(items);
            if (clamped != 0)
            {
                Playlist.SetCurrent(clamped);
            }
            LoadCurrentItem(startPaused);
        }
        finally
        {
            restoringPlaylist = false;
        }
    }

    // Coordinator-side advance entry point: advance the playlist by one and load the new current item. Returns true if an advance happened, false if the playlist was already at the end. The per-context auto-advance handler also routes through this method when AutoAdvanceEnabled is true.
    public bool AdvanceAndLoadIfPossible()
    {
        var next = Playlist.Advance();
        if (next == null)
        {
            return false;
        }
        LoadCurrentItem(startPaused: false);
        return true;
    }

    // The shared per-file setup: outgoing-position save, recents.Record, currentDirectoryKey resolve, currentFilePath set, lastSavedPositionSeconds reset, pendingResumePosition lookup, then playback.LoadFile. Called from LoadPaths (when starting / replacing a playlist), PlayPlaylistItem, and the auto-advance handler. Idempotent w.r.t. the playlist itself — the playlist mutation already happened.
    //
    // Why save-on-LoadCurrentItem instead of subscribing to FileEnded: mpv's EndFile event delivers its reason field via a separate struct that the existing MpvClient.Dispatch reads incorrectly (it reads evt.Error, not the mpv_event_end_file.reason behind evt.Data) — and even with that fixed, at EOF with keep-open=yes time-pos parks at duration so the near-end filter would always elide the save. Saving here, on the user's deliberate "open new file" action OR on auto-advance, dodges both problems. Auto-advance specifically: at natural EOF position ≈ duration so SaveCurrentPositionIfEligible's near-end gate elides the save anyway — verified by the AutoAdvanceElidesNearEndOutgoingSave test.
    private void LoadCurrentItem(bool startPaused)
    {
        if (Playlist.CurrentIndex < 0 || Playlist.CurrentIndex >= Playlist.Items.Count)
        {
            return;
        }
        string pathOrUri = Playlist.Items[Playlist.CurrentIndex];

        // Cancel any in-flight URL download from a previous LoadCurrentItem before mutating per-load state. If the user clicks a different playlist row mid-download, we don't want the late completion of the old download to call playback.LoadFile against the new file's slot. CTS swap is synchronous, so by the time we set currentFilePath below the previous download's continuation will have observed cancellation.
        urlLoad.CancelActive();

        SaveCurrentPositionIfEligible();

        // Skip recents.Record when LoadCurrentItem is reached via RestorePlaylist (system-driven restore, not a deliberate open). Bumping last_opened in that case would overwrite the user's last actual-play timestamp with "now"; user-visible click → PlayPlaylistItem → LoadCurrentItem still records normally because restoringPlaylist is false.
        if (!restoringPlaylist)
        {
            recentFiles.Record(pathOrUri);
        }
        // Resolve and cache the directory key NOW so Select* calls between LoadFile and the next load can reach it. URIs return null and disable persistence for this file.
        currentDirectoryKey = TrackPreferences.TryGetDirectoryKey(pathOrUri);
        CurrentFilePath = pathOrUri;
        currentFileLoaded = false;
        UpdateIsAtPlayableEof();
        lastSavedPositionSeconds = 0;
        // Reset the eof-rising-edge mirror to match the synchronous IsEofReached=false that Playback.LoadFile is about to perform. Ensures the next true→ transition we observe is treated as a rising edge against this file, not against whatever the previous file's tail was.
        wasEofReached = false;
        // Look up the saved position only for local files. URI sources don't accumulate positions, so skipping the GetPosition call also keeps test fakes' GetPositionCalls clean.
        pendingResumePosition = TrackPreferences.IsLocalFilesystemPath(pathOrUri) ? recentFiles.GetPosition(pathOrUri) : null;
        PrefsLog($"LoadCurrentItem: pathOrUri={pathOrUri} → directoryKey={currentDirectoryKey ?? "<null>"}, resumePos={(pendingResumePosition?.ToString() ?? "<null>")}");

        if (urlLoad.ShouldDownload(pathOrUri))
        {
            // Coordinator-internal race guards (cancellation, newer-download-took-over) gate the callback. The CurrentFilePath check inside the callback is the host's own "still current" predicate — if the user advanced to another row during the download, our late LoadFile would clobber the new playback. The startPaused captured here flows into the eventual LoadFile so restored URL items honor the pause intent across the async download hop.
            urlLoad.StartDownload(pathOrUri, (url, localPath) =>
            {
                if (CurrentFilePath != url)
                {
                    return;
                }
                playback.LoadFile(localPath, startPaused);
            });
            return;
        }
        playback.LoadFile(pathOrUri, startPaused);
    }

    public void PlayPause()
    {
        // Catches the no-file-loaded case (the user-visible bug: clicking play before opening anything flipped the icon to "pause" without anything to play). mpv accepts pause toggles pre-load fine — this is purely a UX gate. Lives in the VM rather than only on the button so the Space-key path through PlayPauseCommand is also covered. Also incidentally gates pause on live streams / unseekable inputs that report duration=0; if anyone needs pause-on-livestream this should become a HasFile latched on FileLoaded.
        if (Duration <= TimeSpan.Zero)
        {
            return;
        }
        playback.TogglePause();
    }

    // Set the paused state explicitly, with the same Duration > 0 UX gate as PlayPause. Used by the coordinator's no-selection sync-broadcast: PlayPause needs a deterministic target to converge potentially-drifted streams, but the gate must still apply per-context so a stream with no file loaded doesn't echo a spurious paused state.
    public void SetPaused(bool paused)
    {
        if (Duration <= TimeSpan.Zero)
        {
            return;
        }
        playback.SetPaused(paused);
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
        UpdateIsAtPlayableEof();
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
        var filePath = CurrentFilePath;
        if (filePath == null)
        {
            return;
        }
        if (!currentFileLoaded)
        {
            return;
        }
        if (!TrackPreferences.IsLocalFilesystemPath(filePath))
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
        recentFiles.RecordPosition(filePath, position);
        lastSavedPositionSeconds = position;
        PrefsLog($"position saved: file={filePath} pos={position:F3} duration={duration:F3}");
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

    // Wire the per-context HDR sink. The pre-stage SDR call runs synchronously here, before any frame can render — necessary to cover two paths that ApplyHdrPolicy alone doesn't reach: (a) first-SDR-file in a fresh process, where the source-gamma observation arrives as null→bt.1886 and never crosses isSourceHdr's transition gate, so SourceHdrChanged never fires; (b) any compositor whose first wl_surface.enter races ahead of the bridge subscription. In both, without this kickoff the subsurface stays untagged and KWin's HDR-aware compositor blows out gamma22 SDR output.
    //
    // After pre-stage, subscribe to CurrentOutputHdrChanged and run an initial ApplyHdrPolicy so a source-HDR file already loaded picks up its tag now. Caller responsibility: invoke this only when the sink is past pre-render-ready (e.g., from RenderContextReady on Wayland) — the contract of IHdrSink.SetHdr returns -1 pre-realize and we'd silently fail without an obvious place to retry.
    public void AttachHdrSink(IHdrSink? sink)
    {
        if (hdrSink == sink)
        {
            return;
        }
        if (hdrSink != null)
        {
            hdrSink.CurrentOutputHdrChanged -= OnCurrentOutputHdrChanged;
        }
        hdrSink = sink;
        if (sink == null)
        {
            return;
        }
        // Pre-stage SDR — leaving the surface untagged is implementation-defined and can blow out on KWin.
        sink.SetHdr(false);
        sink.CurrentOutputHdrChanged += OnCurrentOutputHdrChanged;
        ApplyHdrPolicy();
    }

    public void DetachHdrSink()
    {
        if (hdrSink == null)
        {
            return;
        }
        hdrSink.CurrentOutputHdrChanged -= OnCurrentOutputHdrChanged;
        hdrSink = null;
    }

    // Wire the per-context VRR sink. Subscribes to CurrentOutputVrrRangeChanged and runs an initial ApplyVrrPolicy so a file whose VideoFps already landed picks up its multiplier now. Symmetric with AttachHdrSink in shape; no pre-stage call because VRR has no "tag the surface" side — the multiplier is purely an mpv-side filter.
    public void AttachVrrSink(IVrrSink? sink)
    {
        if (vrrSink == sink)
        {
            return;
        }
        if (vrrSink != null)
        {
            vrrSink.CurrentOutputVrrRangeChanged -= OnCurrentOutputVrrRangeChanged;
        }
        vrrSink = sink;
        if (sink == null)
        {
            return;
        }
        sink.CurrentOutputVrrRangeChanged += OnCurrentOutputVrrRangeChanged;
        ApplyVrrPolicy();
    }

    public void DetachVrrSink()
    {
        if (vrrSink == null)
        {
            return;
        }
        vrrSink.CurrentOutputVrrRangeChanged -= OnCurrentOutputVrrRangeChanged;
        vrrSink = null;
    }

    private void OnSourceHdrChanged(bool isHdr)
    {
        lastSourceHdr = isHdr;
        ApplyHdrPolicy();
    }

    private void OnCurrentOutputHdrChanged()
    {
        ApplyHdrPolicy();
    }

    private void OnCurrentOutputVrrRangeChanged()
    {
        ApplyVrrPolicy();
    }

    private void OnIsSourceFpsTrustedChanged(bool trusted)
    {
        ApplyVrrPolicy();
    }

    // Single point of decision for the HDR-viewport question. Drives the wp_color_management_v1 tag on the subsurface and mpv's target-* options. Policy: PQ tag iff source is HDR (regardless of display HDR-capability). Rationale: mpv-via-libmpv is forced to use the older gl_video pipeline whose tone-map curve clips highlights hard at the source mastering-display peak (1000 nits → all-white SDR for typical files); KWin 6.x runs HDR-aware compositing with libplacebo, which has nicer curves. By tagging PQ and having mpv emit pass-through PQ, we hand HDR→SDR conversion to the compositor and inherit its tone-map. Display HDR-capability is no longer load-bearing for the policy — it's still tracked for the diagnostic overlay's `display=` row, but doesn't gate the tag. Window-spanning a PQ-tagged subsurface across HDR + SDR outputs is now correct by construction: the compositor pass-throughs on the HDR side and tonemaps on the SDR side per-output. On enable, stage the PQ/BT.2020 image description first so the very next eglSwapBuffers flushes it atomically with the first PQ-encoded mpv frame; only advance mpv to PQ targeting if the shim confirms the description is attachable (else mpv tone-maps to gamma22 — fallback for non-CM compositors). On disable, mirror in reverse order so the next SDR frame lands on an SDR-tagged surface (GAMMA22/BT.709) — see VideoSurface.SetHdr for why we tag SDR explicitly rather than leave the surface in compositor-defined territory.
    private void ApplyHdrPolicy()
    {
        if (hdrSink == null)
        {
            return;
        }
        bool? outputHdr = hdrSink.CurrentOutputIsHdr;
        bool wantHdr = lastSourceHdr;
        HdrActiveState newState;
        if (wantHdr)
        {
            bool applied = hdrSink.SetHdr(true) == 0;
            if (applied)
            {
                playback.EnableHdrOutput();
                newState = HdrActiveState.Hdr;
            }
            else
            {
                // Shim refused (no wp_color_manager_v1 advertised, description build failed, etc). Do NOT advance mpv to PQ targets — that would leave mpv rendering PQ-encoded pixels into an untagged surface, which the compositor would interpret as sRGB and display as blown-out whites. Staying SDR on both halves is safe.
                newState = HdrActiveState.Failed;
            }
        }
        else
        {
            // SetHdr(false) attaches the SDR (GAMMA22/BT.709) image description. -1 means the compositor doesn't advertise wp_color_manager_v1 (or description build failed) — surface stays untagged, which is implementation-defined per the wp_color_management_v1 spec. On most compositors that's fine (treated as sRGB); on KWin with an HDR output present, an untagged surface is misinterpreted and SDR output blows out. We can't do anything about it from here, but log so a future bug report ("blown out on a non-KWin compositor") is diagnosable.
            int sdrRc = hdrSink.SetHdr(false);
            if (sdrRc != 0 && ActiveHdrState != HdrActiveState.Sdr)
            {
                Console.Error.WriteLine("[vompl] hdr: SDR tag attach failed (no wp_color_manager_v1) — surface left untagged; if SDR output looks blown out, your compositor is misinterpreting untagged surfaces");
            }
            playback.DisableHdrOutput();
            newState = HdrActiveState.Sdr;
        }
        if (newState != ActiveHdrState)
        {
            // Silent error handling is banned (CLAUDE.md). Failed = the user asked for HDR and the compositor refused; surface it unconditionally rather than gating on VOMPL_LOG_HDR. Only logged on transitions into Failed so a stuck-in-Failed state doesn't spam.
            if (newState == HdrActiveState.Failed)
            {
                Console.Error.WriteLine("[vompl] hdr: requested but compositor refused (no wp_color_manager_v1 or description build failed) — staying SDR");
            }
        }
        if (LogHdr)
        {
            string outStr = outputHdr.HasValue ? (outputHdr.Value ? "HDR" : "SDR") : "unknown";
            Console.Error.WriteLine($"[vompl] hdr policy: source={(lastSourceHdr ? "HDR" : "SDR")} display={outStr} → {newState} (was {ActiveHdrState})");
        }
        ActiveHdrState = newState;
    }

    // Single point of decision for the VRR frame-multiplier question. Combines source FPS, the current output's VRR window, the current scanout refresh (used as a hard ceiling — see VrrPolicy), and the trust flag through VrrPolicy.Decide; calls SetFrameMultiplier on the playback for N≥2 and ClearFrameMultiplier otherwise. Caches the decision (`LastVrrDecision`) for the diagnostic overlay. Runs from: AttachVrrSink (initial), VideoFps property change, IsSourceFpsTrustedChanged, CurrentOutputVrrRangeChanged. No-op when no sink is attached (the GLArea fallback path doesn't expose an IVrrSink, so multipliers don't apply there). A mode change on the same active output without an accompanying VRR-range or active-output event won't re-trigger; the policy stays stale until the next file load, which we accept (rare enough to not warrant a new event surface).
    private void ApplyVrrPolicy()
    {
        if (vrrSink == null)
        {
            return;
        }
        var decision = VrrPolicy.Decide(playback.VideoFps, vrrSink.CurrentOutputVrrRange, vrrSink.CurrentOutputRefreshHz, playback.IsSourceFpsTrusted);
        LastVrrDecision = decision;
        if (decision.Multiplier >= 2)
        {
            playback.SetFrameMultiplier(decision.OutputFps);
        }
        else
        {
            playback.ClearFrameMultiplier();
        }
    }

    private void UpdateIsAtPlayableEof()
    {
        bool newValue = currentFileLoaded && playback.IsEofReached && playback.DurationSeconds > 0;
        if (newValue != IsAtPlayableEof)
        {
            IsAtPlayableEof = newValue;
        }
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
                UpdateIsAtPlayableEof();
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
                UpdateIsAtPlayableEof();
                if (AutoAdvanceEnabled
                    && nowEof && !wasEofReached && currentFileLoaded && playback.DurationSeconds > 0)
                {
                    AdvanceAndLoadIfPossible();
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
            case nameof(IPlayback.VideoAspect):
                VideoAspect = playback.VideoAspect;
                break;
            case nameof(IPlayback.VideoFps):
                VideoFps = playback.VideoFps;
                ApplyVrrPolicy();
                break;
            case nameof(IPlayback.MediaTitle):
                MediaTitle = playback.MediaTitle;
                break;
        }
    }

    public void Dispose()
    {
        // Final position save before tear-down. The SQLite connection lives in Program.cs's `using var stateDb` which outlives every VideoContext, so the DB call here is safe; and playback is still live (disposed at the end of this method).
        SaveCurrentPositionIfEligible();
        // Cancel any in-flight URL download so the spawned yt-dlp process exits before the GTK main loop tears down.
        urlLoad.Dispose();
        playback.PropertyChanged -= OnPlaybackPropertyChanged;
        playback.FileLoaded -= OnPlaybackFileLoaded;
        playback.TracksReloaded -= OnPlaybackTracksReloaded;
        playback.SourceHdrChanged -= OnSourceHdrChanged;
        playback.IsSourceFpsTrustedChanged -= OnIsSourceFpsTrustedChanged;
        DetachHdrSink();
        DetachVrrSink();
        playback.Dispose();
    }
}
