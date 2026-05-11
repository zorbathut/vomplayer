using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vomplayer.Playback;
using Vomplayer.Services;
using Vomplayer.UserData;

namespace Vomplayer.ViewModels;

// Coordinator. Owns Primary (always) and an optional Secondary VideoContext (lazy on EnablePip). When Secondary is null the VM's behavior is bit-for-bit equivalent to single-video — all commands target Primary, the per-context auto-advance handles its own EOF-rising-edge.
//
// When Secondary exists, routing is governed by SelectedSlot:
//   - SelectedSlot == null  ⇒  broadcast/sync. Transport (Seek*, StepFrame*, StepChapter) fans out to both contexts. PlayPause sync-broadcasts: computes a unified target from Primary's flip and calls SetPaused(target) on every stream so streams that drifted out of sync (selection-then-isolated-toggle) converge back. Per-video commands (Volume / Mute / Tracks / Open* / Load*) target Primary.
//   - SelectedSlot != null  ⇒  isolated. Every command (transport, PlayPause, per-video) goes to the selected context only.
//
// This split is the "everything just works in lockstep by default; click a stream to cue just that one for a moment" UX. Per-context auto-advance is still suppressed coordinator-side when both exist; lockstep advance runs from CheckLockstepAdvance.
//
// Read-only proxy properties (Position, Duration, …) reflect SingleTarget() — i.e. SelectedContext if set, otherwise Primary. PropertyChanged is forwarded only when the source context matches SingleTarget(), so the view never sees the off-target context's churn. SelectedSlot transitions fire PropertyChanged for all proxies (see OnSelectedSlotChanged) so the view re-reads after the swap.
public sealed partial class ViewModelMain : ObservableObject, IDisposable
{
    public enum VideoSlot
    {
        Primary,
        Secondary,
    }

    public VideoContext Primary { get; }

    // Lazy: created externally and handed in via EnablePip; null when PiP is off. The VM takes ownership and disposes Secondary in DisablePip / Dispose.
    public VideoContext? Secondary { get; private set; }

    // The currently-selected slot, or null when nothing is selected (broadcast/sync mode). SetSelected(Secondary) when Secondary is null silently snaps to null. DisablePip force-resets to null so a subsequent EnablePip starts in a clean state. Set this property to null (the default) to "go back to sync".
    [ObservableProperty]
    private VideoSlot? selectedSlot;

    // True iff Secondary is currently allocated. Mirrors Secondary != null but exposed as an observable property so the view can react to it via PropertyChanged.
    [ObservableProperty]
    private bool isPipEnabled;

    public string? InitialFile { get; set; }
    private bool initialFileLoaded;

    // Sync-mode offset invariant the post-edge correction defends: Secondary.Position == Primary.Position + targetOffsetSeconds. Null when slave is inactive (no PiP, selected mode active, or the offset hasn't been established yet — see capture/clear rules around EnablePip / OnSelectedSlotChanged / FileLoaded).
    private double? targetOffsetSeconds;

    // Raised on each sync-mode transport edge (PlayPause-to-playing, SeekTo, SeekRelative, StepChapter) so MainWindow can schedule the deferred post-edge corrective seek. No payload — the handler reads current state when the timer eventually fires.
    public event Action? PostEdgeCorrectionRequested;

    // The context the user has explicitly selected via the toolbar, or null if no selection (broadcast/sync mode). Read-only; mutate via SetSelected.
    public VideoContext? SelectedContext
    {
        get
        {
            if (SelectedSlot == VideoSlot.Secondary && Secondary != null)
            {
                return Secondary;
            }
            if (SelectedSlot == VideoSlot.Primary)
            {
                return Primary;
            }
            return null;
        }
    }

    // Default routing target for single-target operations (per-video commands like Volume / Mute / Open*). Returns the selected context if any, otherwise Primary. The view also binds proxy properties to this — in no-selection mode the chrome reflects Primary; in selected mode it reflects the selected stream.
    public VideoContext SingleTarget
    {
        get
        {
            return SelectedContext ?? Primary;
        }
    }

    // Read-only proxy surface — UI and tests bind these by name; values come from SingleTarget via PropertyChanged forwarding below. PropertyChanged is forwarded only when the source context matches SingleTarget, so the view never sees the off-target context's churn. SelectedSlot transitions fire PropertyChanged for all proxies (see OnSelectedSlotChanged) so the view re-reads from the new target.
    public TimeSpan Position
    {
        get
        {
            return SingleTarget.Position;
        }
    }

    public TimeSpan Duration
    {
        get
        {
            return SingleTarget.Duration;
        }
    }

    public bool IsPaused
    {
        get
        {
            return SingleTarget.IsPaused;
        }
    }

    public double SeekValue
    {
        get
        {
            return SingleTarget.SeekValue;
        }
    }

    public double Volume
    {
        get
        {
            return SingleTarget.Volume;
        }
    }

    public bool IsMuted
    {
        get
        {
            return SingleTarget.IsMuted;
        }
    }

    public IReadOnlyList<MediaTrack> VideoTracks
    {
        get
        {
            return SingleTarget.VideoTracks;
        }
    }

    public IReadOnlyList<MediaTrack> AudioTracks
    {
        get
        {
            return SingleTarget.AudioTracks;
        }
    }

    public IReadOnlyList<MediaTrack> SubtitleTracks
    {
        get
        {
            return SingleTarget.SubtitleTracks;
        }
    }

    public IReadOnlyList<MediaChapter> Chapters
    {
        get
        {
            return SingleTarget.Chapters;
        }
    }

    public int? CurrentVideoId
    {
        get
        {
            return SingleTarget.CurrentVideoId;
        }
    }

    public int? CurrentAudioId
    {
        get
        {
            return SingleTarget.CurrentAudioId;
        }
    }

    public int? CurrentSubtitleId
    {
        get
        {
            return SingleTarget.CurrentSubtitleId;
        }
    }

    public Playlist Playlist
    {
        get
        {
            return SingleTarget.Playlist;
        }
    }

    public string? MediaTitle
    {
        get
        {
            return SingleTarget.MediaTitle;
        }
    }

    // The set of proxy-property names that mirror per-context observables. Used by OnSelectedSlotChanged to re-fire PropertyChanged for all of them when selection swaps so the view re-reads from the new target.
    private static readonly string[] ProxyPropertyNames =
    {
        nameof(Position),
        nameof(Duration),
        nameof(IsPaused),
        nameof(SeekValue),
        nameof(Volume),
        nameof(IsMuted),
        nameof(VideoTracks),
        nameof(AudioTracks),
        nameof(SubtitleTracks),
        nameof(Chapters),
        nameof(CurrentVideoId),
        nameof(CurrentAudioId),
        nameof(CurrentSubtitleId),
        nameof(MediaTitle),
    };

    // Optional dependency: production wires it via AttachAutosave after construction so test-only consumers (which don't care about persistence) can keep using the existing ctor surface unchanged. Null until AttachAutosave runs.
    private ISavedPlaylists? savedPlaylists;
    private PlaylistAutosave? autosave;

    public PlaylistAutosave? Autosave
    {
        get
        {
            return autosave;
        }
    }

    public ViewModelMain(IPlayback playback, IFilePicker filePicker, IRecentFiles recentFiles, ITrackPreferences trackPreferences, IUrlDownloader urlDownloader, IUrlPrompt urlPrompt)
    {
        Primary = new VideoContext(playback, filePicker, recentFiles, trackPreferences, urlDownloader, urlPrompt);
        Primary.PropertyChanged += OnContextPropertyChanged;
        // File-load resets Primary.Position to 0; an in-flight implicit-burst anchor from the previous file would mis-anchor the first SeekTo against the new file. Subscribe directly to the playback (not VideoContext, since the anchor is purely a coordinator concern and doesn't need to flow through the context's mirror plumbing).
        playback.FileLoaded += InvalidateSyncSeekAnchor;
        // Same race rationale as the burst-anchor invalidation: a Primary file load between sync-mode operations resets Position to 0 and breaks the captured offset. The lazy fallback inside ApplyPostEdgeCorrection re-establishes a fresh offset on the next correction edge.
        playback.FileLoaded += ClearTargetOffset;
    }

    // Wire the autosave service. Production calls this immediately after ctor so the very first user action (open file via CLI arg, drag-drop, file dialog) is captured. Tests that don't care about autosave skip this call and the autosave-related public methods (LoadFromSaved) become unavailable. Idempotent — second call is rejected because there's no clean way to swap repos without losing the in-memory CurrentGuid.
    public void AttachAutosave(ISavedPlaylists repo)
    {
        if (repo == null)
        {
            throw new ArgumentNullException(nameof(repo));
        }
        if (autosave != null)
        {
            throw new InvalidOperationException("Autosave already attached");
        }
        savedPlaylists = repo;
        autosave = new PlaylistAutosave(repo);
        autosave.BindPrimary(Primary);
        if (Secondary != null)
        {
            // Pre-existing PiP state (uncommon — AttachAutosave is normally called before EnablePip can fire — but plausible if a future caller orders these the other way around).
            autosave.BindSecondary(Secondary);
        }
    }

    // Restore an entry from the saved-playlists store into the in-memory contexts. Pre-conditions: autosave must be attached (throws otherwise); PiP state should already match the saved entry (caller is responsible — MainWindow's open-recent-playlist handler toggles EnablePip/DisablePip before invoking this). startPaused=true sets pause before LoadFile so the user sees a thumbnail at the resume position rather than auto-play. Touches the entry's last_used_at so the menu reorders it to the top.
    public void LoadFromSaved(Guid guid, bool startPaused)
    {
        if (savedPlaylists == null || autosave == null)
        {
            throw new InvalidOperationException("AttachAutosave must be called before LoadFromSaved");
        }
        var entry = savedPlaylists.GetById(guid);
        if (entry == null)
        {
            return;
        }
        autosave.BeginRestore();
        try
        {
            SavedPlaylistStream? primaryStream = null;
            SavedPlaylistStream? secondaryStream = null;
            foreach (var s in entry.Streams)
            {
                if (s.SlotIndex == 0)
                {
                    primaryStream = s;
                }
                else if (s.SlotIndex == 1)
                {
                    secondaryStream = s;
                }
            }
            if (primaryStream != null)
            {
                Primary.RestorePlaylist(primaryStream.Items, primaryStream.CurrentIndex, startPaused);
            }
            if (secondaryStream != null && Secondary != null)
            {
                Secondary.RestorePlaylist(secondaryStream.Items, secondaryStream.CurrentIndex, startPaused);
            }
            autosave.SetCurrentGuid(guid);
            savedPlaylists.Touch(guid);
        }
        finally
        {
            autosave.EndRestore();
        }
        // Touch doesn't go through Persist (no payload changed), but the menu still needs to rebuild because last_used_at moved.
        autosave.RaiseSaved();
    }

    // Take ownership of `secondary` and switch into PiP mode. Caller (MainWindow) constructs the secondary VideoContext (with its own Playback) and hands it over here. The VM then disables per-context auto-advance on both and starts driving lockstep advance itself. Idempotent: calling EnablePip while already in PiP mode is a no-op (the supplied secondary is NOT swapped in — caller should DisablePip first).
    public void EnablePip(VideoContext secondary)
    {
        if (secondary == null)
        {
            throw new ArgumentNullException(nameof(secondary));
        }
        if (IsPipEnabled)
        {
            return;
        }
        Secondary = secondary;
        Primary.AutoAdvanceEnabled = false;
        secondary.AutoAdvanceEnabled = false;
        secondary.PropertyChanged += OnContextPropertyChanged;
        autosave?.BindSecondary(secondary);
        // Same FileLoaded subscription as Primary — a Secondary file load between SeekTos within the burst window would otherwise produce a Primary-anchored delta against a Secondary that just reset to 0.
        secondary.Playback.FileLoaded += InvalidateSyncSeekAnchor;
        // Mirror Primary's targetOffset clear on Secondary's loads.
        secondary.Playback.FileLoaded += ClearTargetOffset;
        InvalidateSyncSeekAnchor();
        // Initial offset: both contexts start at content time 0. Cleared by either FileLoaded handler above the moment a real file lands. (If both contexts are already mid-playback when EnablePip lands — uncommon but possible — the initial 0 is wrong; the next correction edge re-captures via the lazy fallback inside ApplyPostEdgeCorrection.)
        targetOffsetSeconds = 0;
        IsPipEnabled = true;
    }

    // Tear down PiP: disposes the Secondary VideoContext, restores Primary's per-context auto-advance, and resets SelectedSlot to null so the next EnablePip starts in a clean broadcast/sync state. No-op when PiP is already off.
    public void DisablePip()
    {
        if (!IsPipEnabled)
        {
            return;
        }
        var secondary = Secondary;
        Secondary = null;
        // Unbind autosave BEFORE disposing the secondary so the unbind's final Persist sees the live secondary's now-empty state. (Persist filters empty streams; with secondary still bound but empty, the row drops back to single-stream — exactly the user-visible state.)
        autosave?.UnbindSecondary();
        if (secondary != null)
        {
            secondary.PropertyChanged -= OnContextPropertyChanged;
            secondary.Playback.FileLoaded -= InvalidateSyncSeekAnchor;
            secondary.Playback.FileLoaded -= ClearTargetOffset;
            secondary.Dispose();
        }
        InvalidateSyncSeekAnchor();
        ClearTargetOffset();
        Primary.AutoAdvanceEnabled = true;
        if (SelectedSlot != null)
        {
            SelectedSlot = null;
        }
        IsPipEnabled = false;
        // After tearing down secondary, Primary may already be at file-EOF (a natural endpoint reached during PiP-mode lockstep wait). Per-context auto-advance has just been re-enabled but won't fire without a fresh rising edge — the existing IsEofReached=true is no longer "rising". Manually re-trigger if the conditions are met so the user doesn't have to manually click the next playlist row.
        if (Primary.IsAtPlayableEof && Primary.HasNextItem)
        {
            Primary.AdvanceAndLoadIfPossible();
        }
    }

    // Switch which slot receives input, or pass null to return to broadcast/sync mode. SetSelected(Secondary) when Secondary is null silently coerces to null so the invariant "SelectedSlot=Secondary ⇒ Secondary != null" holds.
    public void SetSelected(VideoSlot? slot)
    {
        if (slot == VideoSlot.Secondary && Secondary == null)
        {
            slot = null;
        }
        SelectedSlot = slot;
    }

    // Subscribed on both Primary and (when present) Secondary. Drops PropertyChanged from the off-target context so the view never sees its churn; Primary and Secondary share property names by design, and forwarding both unconditionally would cause spurious view updates that read back the on-target value (waste, not wrong, but confusing).
    private void OnContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoContext.IsAtPlayableEof))
        {
            CheckLockstepAdvance();
            return;
        }
        // ActiveHdrState is per-context coordination state, not a view-facing observable.
        if (e.PropertyName == nameof(VideoContext.ActiveHdrState))
        {
            return;
        }
        if (!ReferenceEquals(sender, SingleTarget))
        {
            return;
        }
        // Re-fire on this VM under the same name. Property names on VideoContext intentionally match the VM's proxy properties one-to-one, so a verbatim re-fire is correct.
        OnPropertyChanged(e.PropertyName);
    }

    // Auto-generated [ObservableProperty] partial hook fired AFTER SelectedSlot's value updates. We use it to push the selection swap through to the view: re-fire PropertyChanged for every proxy property so existing OnViewModelPropertyChanged handlers re-read against the new target context's values (volume slider snaps to target's volume, seek bar to target's position, etc.).
    partial void OnSelectedSlotChanged(VideoSlot? value)
    {
        // Mode transition (sync ↔ isolated) invalidates the anchor: the next sync-mode SeekTo should start fresh against Primary's actual position rather than the last commanded target from the previous mode.
        InvalidateSyncSeekAnchor();
        // Sync-mode targetOffset bookkeeping. Setter equality gating means every fire here is a real transition, so we just branch on the new value:
        //   - newValue != null  ⇒ entering selected mode (slave inactive). Clear the offset; the user is about to deliberately move one stream alone.
        //   - newValue == null  ⇒ returning to sync. Capture from current positions — the user just demonstrated their intended offset by leaving isolated mode at this configuration.
        if (value != null)
        {
            targetOffsetSeconds = null;
        }
        else if (Secondary != null)
        {
            targetOffsetSeconds = Secondary.Playback.PositionSeconds - Primary.Playback.PositionSeconds;
        }
        for (int i = 0; i < ProxyPropertyNames.Length; i++)
        {
            OnPropertyChanged(ProxyPropertyNames[i]);
        }
    }

    private bool insideLockstepAdvance;

    // Lockstep advance gate. Coordinator owns advance dispatch only when both contexts exist (per-context auto-advance handles single-video). Both must be at playable EOF AND both must have a next item — otherwise the pair stays paused. This is the deliberate reading of the user spec ("advance them together"); the alternative ("longer playlist keeps advancing alone") would violate that rule.
    //
    // Re-entrancy guard: Primary.AdvanceAndLoadIfPossible synchronously flips Primary.IsAtPlayableEof to false, which fires PropertyChanged → re-enters CheckLockstepAdvance with Secondary still IsAtPlayableEof=true. The early-return-on-not-at-EOF check would catch it today, but only because LoadCurrentItem is synchronous; if a future refactor makes the load async (e.g. yt-dlp queue), Secondary would still see Primary mid-advance. The flag makes the invariant explicit so the bug doesn't bite later.
    private void CheckLockstepAdvance()
    {
        if (insideLockstepAdvance)
        {
            return;
        }
        if (Secondary == null)
        {
            return;
        }
        if (!Primary.IsAtPlayableEof || !Secondary.IsAtPlayableEof)
        {
            return;
        }
        if (!Primary.HasNextItem || !Secondary.HasNextItem)
        {
            return;
        }
        // Capture pause state before advancing so we can preserve a user-paused half across the lockstep transition. AdvanceAndLoadIfPossible calls playback.LoadFile with startPaused=false (auto-advance treats the next file as "keep playing"). Without the re-apply below, a context the user deliberately paused (e.g. paused mid-wait while the other half played out to its EOF) would unpause on the next track. Re-paused via VideoContext.SetPaused, which serializes after LoadFile on the same dispatcher; final mpv state matches the captured one. Running contexts get no extra command — LoadFile's pause=no carries through. Note: SetPaused gates on Duration > 0; we rely on the Duration mirror still holding the previous file's positive value at this point (mpv property updates land via the main-thread observer pump, which can't run during this synchronous call).
        bool primaryWasPaused = Primary.IsPaused;
        bool secondaryWasPaused = Secondary.IsPaused;

        insideLockstepAdvance = true;
        try
        {
            Primary.AdvanceAndLoadIfPossible();
            Secondary.AdvanceAndLoadIfPossible();
        }
        finally
        {
            insideLockstepAdvance = false;
        }

        if (primaryWasPaused)
        {
            Primary.SetPaused(true);
        }
        if (secondaryWasPaused)
        {
            Secondary.SetPaused(true);
        }
    }

    // Iterates the contexts that should receive a *transport* command. Selected ⇒ just that one; null ⇒ Primary plus Secondary if present (sync broadcast).
    private IEnumerable<VideoContext> RoutingTargets()
    {
        if (SelectedContext != null)
        {
            yield return SelectedContext;
            yield break;
        }
        yield return Primary;
        if (Secondary != null)
        {
            yield return Secondary;
        }
    }

    [RelayCommand]
    private Task OpenAsync()
    {
        return SingleTarget.OpenAsync();
    }

    [RelayCommand]
    private Task OpenUrlAsync()
    {
        return SingleTarget.OpenUrlAsync();
    }

    [RelayCommand]
    private Task LoadAudioAsync()
    {
        return SingleTarget.LoadAudioAsync();
    }

    [RelayCommand]
    private Task LoadSubtitleAsync()
    {
        return SingleTarget.LoadSubtitleAsync();
    }

    [RelayCommand]
    private void PlayPause()
    {
        // Selection set ⇒ isolated toggle on that one stream. Other streams are deliberately untouched (the "cue PiP without disturbing primary" workflow). VideoContext.PlayPause handles the no-file-loaded UX gate.
        if (SelectedContext != null)
        {
            SelectedContext.PlayPause();
            return;
        }
        // No selection ⇒ sync-broadcast. The streams may have drifted (because earlier the user had Secondary selected and toggled it independently); a naive per-context toggle would keep them divergent. Compute a single target from Primary's flip and apply that to every stream so the chrome's Space gesture always returns to a unified state. Tiebreaker: Primary drives. SetPaused on each context applies the same Duration > 0 gate as PlayPause, so a stream with no file loaded is a no-op rather than echoing a spurious paused state.
        bool target = !Primary.Playback.IsPaused;
        Primary.SetPaused(target);
        if (Secondary != null)
        {
            Secondary.SetPaused(target);
        }
        // Schedule a corrective seek for the post-play settle (independent dispatcher latency + decoder startup time). The MainWindow-side timer coalesces multiple edges within its delay window into one correction. Eager re-capture of the offset HERE was rejected: it would overwrite a known-good offset with whatever drift accumulated during the prior play session, baking in the very wall-clock skew the correction is meant to defend against. The legitimate user "I just set up the offset" moment is the selected→null transition, which OnSelectedSlotChanged handles.
        if (!target && Secondary != null)
        {
            PostEdgeCorrectionRequested?.Invoke();
        }
    }

    public void SelectVideo(int? trackId)
    {
        SingleTarget.SelectVideo(trackId);
    }

    public void SelectAudio(int? trackId)
    {
        SingleTarget.SelectAudio(trackId);
    }

    public void SelectSubtitle(int? trackId)
    {
        SingleTarget.SelectSubtitle(trackId);
    }

    public void OpenFile(string pathOrUri)
    {
        // FileLoaded eventually invalidates the SeekTo anchor too, but it's async — eager invalidation closes the window where a SeekTo issued just after OpenFile (still within the burst threshold) would compute a delta against a target commanded against the previous file.
        InvalidateSyncSeekAnchor();
        SingleTarget.OpenFile(pathOrUri);
    }

    public void LoadPaths(IReadOnlyList<string> paths, bool replace)
    {
        InvalidateSyncSeekAnchor();
        SingleTarget.LoadPaths(paths, replace);
    }

    public void PlayPlaylistItem(int index)
    {
        InvalidateSyncSeekAnchor();
        SingleTarget.PlayPlaylistItem(index);
    }

    public void OnRenderContextReady()
    {
        if (initialFileLoaded)
        {
            return;
        }
        if (!string.IsNullOrEmpty(InitialFile))
        {
            // Initial file always lands in Primary — InitialFile is a process-level "the user passed this on argv" concept, not a per-context one. Routes through OpenFile so the command-line file is recorded in recents the same way drag-and-drop and the file picker are.
            Primary.OpenFile(InitialFile);
        }
        else if (savedPlaylists != null)
        {
            // No CLI arg: try to restore the most recent playlist. If it's multi-stream, do nothing (per user spec — don't auto-enable PiP at startup; let the user explicitly click the Recent entry to reactivate it). The single-stream path loads paused at the saved resume position.
            var last = savedPlaylists.GetMostRecent();
            if (last != null && last.StreamCount == 1)
            {
                LoadFromSaved(last.Guid, startPaused: true);
            }
        }
        initialFileLoaded = true;
    }

    // Sync-mode "absolute delta" contract: Primary takes the user-targeted move; Secondary mirrors the same absolute-seconds delta so videos of different lengths or starting offsets stay locked at the relative offset the user has established. Isolated mode (SelectedContext != null) routes everything to the selected stream and bypasses all of this.
    //
    // Implicit-burst anchor: the scrubber fires SeekTo per motion event (drag), per scroll-wheel tick, and per rapid hotkey press — all faster than mpv echoes time-pos back into Primary.Position. Reading Primary.Position fresh on each SeekTo would anchor against stale data and Secondary would cumulatively overshoot. We track the last commanded Primary target and its timestamp; SeekTos within SyncSeekImplicitBurstTicks (250 ms) of the previous one anchor against the stored target rather than re-reading Position. The anchor is also invalidated by any other transport that mutates Primary.Position outside this loop (SeekRelative, Step*, OpenFile, LoadPaths, PlayPlaylistItem) and by file-load events (Primary or Secondary), so the next SeekTo after such an event re-anchors against fresh state.
    private double? lastSyncSeekPrimaryTargetSeconds;
    private long lastSyncSeekTimestampTicks;
    private static readonly long SyncSeekImplicitBurstTicks = (long)(Stopwatch.Frequency * 0.25);
    // Wallclock indirection so tests can drive the burst-window expiration deterministically. Production uses Stopwatch.GetTimestamp.
    internal Func<long> NowProvider { get; set; } = Stopwatch.GetTimestamp;

    public void SeekTo(double normalizedPosition)
    {
        if (SelectedContext != null)
        {
            SelectedContext.SeekTo(normalizedPosition);
            return;
        }
        double primaryTargetSeconds = normalizedPosition * Primary.Duration.TotalSeconds;
        long now = NowProvider();
        double anchorSeconds;
        if (lastSyncSeekPrimaryTargetSeconds.HasValue && (now - lastSyncSeekTimestampTicks) <= SyncSeekImplicitBurstTicks)
        {
            anchorSeconds = lastSyncSeekPrimaryTargetSeconds.Value;
        }
        else
        {
            anchorSeconds = Primary.Position.TotalSeconds;
        }
        lastSyncSeekPrimaryTargetSeconds = primaryTargetSeconds;
        lastSyncSeekTimestampTicks = now;
        double deltaSeconds = primaryTargetSeconds - anchorSeconds;
        Primary.SeekTo(normalizedPosition);
        if (Secondary != null)
        {
            Secondary.SeekRelative(deltaSeconds);
            // Sync-mode SeekTo: even with both videos commanded to the same content-time delta, the two `+exact` seeks land at slightly different wall-clock times (codec asymmetry / hr-seek rewind distance). Schedule a deferred corrective seek to absorb that wall-clock skew once both have settled.
            PostEdgeCorrectionRequested?.Invoke();
        }
    }

    // Clear the implicit-burst anchor so the next SeekTo re-anchors against Primary.Position. Called from anywhere that mutates Primary's position outside SeekTo (SeekRelative, Step*, OpenFile, etc.) and from lifecycle events that reset the Primary–Secondary relationship (file load, EnablePip / DisablePip, selection-mode transitions).
    private void InvalidateSyncSeekAnchor()
    {
        lastSyncSeekPrimaryTargetSeconds = null;
    }

    // FileLoaded handler: when either Primary or Secondary loads a new file, mpv resets the loaded context's position to 0, breaking the captured sync-mode offset entirely. The next sync-mode transport edge (or the lazy fallback inside ApplyPostEdgeCorrection) re-establishes the offset against fresh post-load state.
    private void ClearTargetOffset()
    {
        targetOffsetSeconds = null;
    }

    // Post-edge corrective seek: pulls Secondary back to Primary.Position + targetOffsetSeconds. Internal so MainWindow's coalescing GLib timer (and tests) can drive it. See plan: PiP Sync — Post-Edge Corrective Seek.
    internal void ApplyPostEdgeCorrection()
    {
        // Secondary == null is the canonical "PiP is off" check; IsPipEnabled tracks the same invariant by construction (set true after Secondary is assigned in EnablePip, false after Secondary is nulled in DisablePip).
        if (Secondary == null || SelectedSlot != null)
        {
            return;
        }
        if (Primary.Playback.IsPaused || Secondary.Playback.IsPaused)
        {
            return;
        }
        if (Primary.Playback.IsSeeking || Secondary.Playback.IsSeeking)
        {
            // Slow-codec hr-seek may not have completed by the timer's delay window. Skipping is preferable to firing a corrective seek on top of an in-flight user seek; the next user transport edge will reschedule.
            return;
        }
        if (Primary.Playback.DurationSeconds <= 0 || Secondary.Playback.DurationSeconds <= 0)
        {
            return;
        }
        // Lazy capture: if a path that doesn't go through PlayPause / selected→null landed us here without an offset (lockstep advance, file-load auto-play, EnablePip with both already playing), capture from current positions and skip drift measurement this round. The next edge will trigger another correction.
        if (!targetOffsetSeconds.HasValue)
        {
            targetOffsetSeconds = Secondary.Playback.PositionSeconds - Primary.Playback.PositionSeconds;
            return;
        }
        double drift = (Secondary.Playback.PositionSeconds - Primary.Playback.PositionSeconds) - targetOffsetSeconds.Value;
        if (Math.Abs(drift) < PostEdgeCorrectionThresholdSeconds)
        {
            return;
        }
        double targetSeconds = Primary.Playback.PositionSeconds + targetOffsetSeconds.Value;
        Secondary.Playback.Seek(targetSeconds);
    }

    // 20 ms — one frame at 50 fps; well below the 40 ms lipsync detection threshold. Below this, doing nothing is preferable to a corrective snap.
    private const double PostEdgeCorrectionThresholdSeconds = 0.020;

    // Already absolute-delta and uniform across both contexts — mpv handles per-context edge clamping. Same fan-out for both isolated and sync modes; isolated reduces to a one-element loop. Invalidates the SeekTo anchor since this mutates Primary's position outside the SeekTo loop.
    public void SeekRelative(double seconds)
    {
        InvalidateSyncSeekAnchor();
        foreach (var ctx in RoutingTargets())
        {
            ctx.SeekRelative(seconds);
        }
        // Sync-mode fan-out: same wall-clock skew rationale as SeekTo — both seeks land at slightly different real times. Schedule corrective seek. Selected mode bypasses (only one context received the seek; nothing to correct).
        if (SelectedContext == null && Secondary != null)
        {
            PostEdgeCorrectionRequested?.Invoke();
        }
    }

    public void StepFrameForward()
    {
        StepFrameInternal(forward: true);
    }

    public void StepFrameBack()
    {
        StepFrameInternal(forward: false);
    }

    // Sync-mode StepFrame: advance Primary by one frame (mpv's atomic frame-step / frame-back-step, which also pauses Primary as a side effect). On Secondary, fire mpv's frame-step too — that's atomic-pause-and-step on its end, which avoids the SetPaused/SeekRelative race where mpv could decode a few frames between the two dispatcher commands while Secondary is mid-playback. Then apply a corrective SeekRelative on Secondary equal to the *difference* between Primary's frame duration and Secondary's, so Secondary's net move matches Primary's frame duration regardless of fps mismatch. When Secondary's own fps is unknown, assume it matches Primary's (correction = 0). When Primary's fps is unknown, fall back to per-context frame-step — drift is bounded to ~one frame per step.
    private void StepFrameInternal(bool forward)
    {
        InvalidateSyncSeekAnchor();
        if (SelectedContext != null)
        {
            if (forward) { SelectedContext.StepFrameForward(); } else { SelectedContext.StepFrameBack(); }
            return;
        }
        if (Secondary == null)
        {
            if (forward) { Primary.StepFrameForward(); } else { Primary.StepFrameBack(); }
            return;
        }
        if (forward) { Primary.StepFrameForward(); Secondary.StepFrameForward(); }
        else { Primary.StepFrameBack(); Secondary.StepFrameBack(); }
        double? primaryFps = Primary.VideoFps;
        if (primaryFps == null)
        {
            return;
        }
        double secondaryFps = Secondary.VideoFps ?? primaryFps.Value;
        double primaryFrameDelta = (forward ? 1.0 : -1.0) / primaryFps.Value;
        double secondaryFrameDelta = (forward ? 1.0 : -1.0) / secondaryFps;
        double correctionSeconds = primaryFrameDelta - secondaryFrameDelta;
        if (correctionSeconds != 0.0)
        {
            Secondary.SeekRelative(correctionSeconds);
        }
    }

    // Sync-mode StepChapter: derive Primary's resulting target from its mirror Chapters list and apply the same absolute-seconds delta to Secondary. The general `targetIndex = currentIndex + delta` math handles the pre-chapter-0 case (currentIndex == -1) too: `add chapter +1` from there lands at chapter 0 (-1 + 1 = 0), and `add chapter -1` underflows to -2 → fan-out fallback (correctly, since there's nothing before chapter 0). Out-of-range past either end falls back to per-context StepChapter so both mpv-clamp independently — reproducing mpv's exact clamp-seek semantics VM-side isn't worth the fragility. When Primary has no chapters at all, Primary.StepChapter is a no-op upstream; we still issue Secondary.StepChapter so Secondary's own chapters (if any) advance.
    public void StepChapter(int delta)
    {
        InvalidateSyncSeekAnchor();
        if (SelectedContext != null)
        {
            SelectedContext.StepChapter(delta);
            return;
        }
        if (Secondary == null)
        {
            Primary.StepChapter(delta);
            return;
        }
        var chapters = Primary.Chapters;
        if (chapters.Count == 0)
        {
            Primary.StepChapter(delta);
            Secondary.StepChapter(delta);
            // Per-context fan-out lands the two videos at independent chapter timestamps — the previously-captured offset no longer reflects user intent. Clear so the next correction edge lazy-captures from the post-fan-out state instead of trying to defend the stale invariant. We deliberately do NOT raise PostEdgeCorrectionRequested here: a correction would yank Secondary back to the OLD offset, undoing the fan-out the user implicitly accepted.
            targetOffsetSeconds = null;
            return;
        }
        double primaryPos = Primary.Position.TotalSeconds;
        int currentIndex = FindCurrentChapterIndex(chapters, primaryPos);
        int targetIndex = currentIndex + delta;
        if (targetIndex < 0 || targetIndex >= chapters.Count)
        {
            Primary.StepChapter(delta);
            Secondary.StepChapter(delta);
            // Same rationale as the no-chapters fan-out above.
            targetOffsetSeconds = null;
            return;
        }
        double primaryTargetSeconds = chapters[targetIndex].TimeSeconds;
        double deltaSeconds = primaryTargetSeconds - primaryPos;
        Primary.StepChapter(delta);
        Secondary.SeekRelative(deltaSeconds);
        // Same post-edge correction rationale as SeekTo: both seeks finish at independent wall-clock times.
        PostEdgeCorrectionRequested?.Invoke();
    }

    // Largest index whose chapter time ≤ position — mpv's "current chapter" semantics (chapter K is current while position is in [chapters[K].time, chapters[K+1].time)). Returns -1 when position is before chapter 0's time (mpv reports "no current chapter" in that case). Linear scan because chapter counts are small (typically <50).
    private static int FindCurrentChapterIndex(IReadOnlyList<MediaChapter> chapters, double positionSeconds)
    {
        int found = -1;
        for (int i = 0; i < chapters.Count; i++)
        {
            if (chapters[i].TimeSeconds <= positionSeconds)
            {
                found = i;
            }
            else
            {
                break;
            }
        }
        return found;
    }

    public void SetVolume(double percent)
    {
        SingleTarget.SetVolume(percent);
    }

    public void AdjustVolume(double deltaPercent)
    {
        SingleTarget.AdjustVolume(deltaPercent);
    }

    public void ToggleMute()
    {
        SingleTarget.ToggleMute();
    }

    public void Dispose()
    {
        // DisablePip's null check tolerates the off case; explicit call also unsubscribes Secondary.PropertyChanged before it gets disposed.
        if (Secondary != null)
        {
            DisablePip();
        }
        Primary.PropertyChanged -= OnContextPropertyChanged;
        Primary.Playback.FileLoaded -= InvalidateSyncSeekAnchor;
        Primary.Playback.FileLoaded -= ClearTargetOffset;
        PostEdgeCorrectionRequested = null;
        Primary.Dispose();
    }
}
