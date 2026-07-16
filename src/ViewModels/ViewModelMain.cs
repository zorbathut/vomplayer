using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vomplayer.Playback;
using Vomplayer.Services;
using Vomplayer.UserData;
using Vomplayer.Util;

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

    // Files passed on the command line, consumed once on the first render-context-ready. A list, not a single path: `vomplayer a.mp4 b.mp4` loads all of them, matching what the same invocation does when forwarded to an already-running primary.
    public IReadOnlyList<string>? InitialFiles { get; set; }
    private bool initialFileLoaded;

    // The stored intended delta between the two streams: Secondary.Position == Primary.Position + targetOffsetSeconds. This is the single source of truth for sync-mode absolute seeks — they re-pin Secondary to primaryTarget + this — and the value the continuous drift controller defends. The governing rule: the offset is (re)captured ONLY by actions that establish the sync relationship from the current positions — EnablePip → 0; FileLoaded → null/pending; the selected→sync transition → captured divergence; EnsureTargetOffset's baseline-on-first-use of a pending value; and a sync-mode PlayPause that changes only ONE stream's play state (converging a differed pair "joins" one stream to the other, which sets the sync point). Actions that move BOTH streams together (sync Seek/StepChapter/StepFrame, a same-state play-both) never write it — they read and defend it, so ordinary seeking/playback can't shift the user's sync. Null means "pending" — no PiP, or not yet baselined after a load; reads go through EnsureTargetOffset, which resolves a pending value from the current divergence.
    private double? targetOffsetSeconds;

    // PiP drift-correction controller state, driven by PipController's repeating timer via ApplyDriftCorrection. driftMode latches CatchUp — full ±5% held until the drift overshoots zero — per the three-tier control law. catchUpSign is the drift sign captured on entering CatchUp (the overshoot detector). secondarySpeed mirrors the last speed pushed to mpv so redundant SetSpeed posts are skipped.
    private enum DriftCorrectionMode { Approach, CatchUp }
    private DriftCorrectionMode driftMode = DriftCorrectionMode.Approach;
    private int catchUpSign;
    private double secondarySpeed = 1.0;

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
        // A Primary file load resets Position to 0 and breaks the captured offset. Clear it to "pending" (null); EnsureTargetOffset re-baselines from the post-load divergence on the next sync seek or correction. Subscribed directly to the playback (not VideoContext) since the offset is purely a coordinator concern and doesn't need to flow through the context's mirror plumbing.
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

    // Shut autosave down for the rest of this VM's lifetime. Required *before* the rest of the shutdown teardown begins (specifically before pipController.Dispose, which routes through this VM's DisablePip → autosave.UnbindSecondary and would otherwise overwrite a stream_count=2 saved row with a primary-only row). Idempotent; safe to call multiple times. Called from both MainWindow.OnWindowCloseRequest (the production shutdown path) and Dispose (safety net for VMs disposed outside that ritual — tests, future callers).
    public void DetachAutosave()
    {
        autosave?.Detach();
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
        // Mirror Primary's targetOffset clear on Secondary's loads.
        secondary.Playback.FileLoaded += ClearTargetOffset;
        // Initial offset: both contexts start at content time 0. Secondary is always handed in freshly-constructed with no file loaded, so this 0 is transient — Secondary's first FileLoaded (above) clears it to "pending" (null), and EnsureTargetOffset then baselines from the real post-load divergence. The 0 only ever "sticks" in the degenerate case where no file is loaded into Secondary at all, where it's harmless (sync seeks need both durations > 0).
        targetOffsetSeconds = 0;
        // Fresh secondary starts at mpv's default speed 1.0; reset the controller so a prior session's latch/speed can't leak in.
        driftMode = DriftCorrectionMode.Approach;
        catchUpSign = 0;
        secondarySpeed = 1.0;
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
            secondary.Playback.FileLoaded -= ClearTargetOffset;
            secondary.Dispose();
        }
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
        // No selection ⇒ sync-broadcast. The streams may have drifted (because earlier the user had Secondary selected and toggled it independently); a naive per-context toggle would keep them divergent. Compute a single flip target and apply that to every stream so the chrome's Space gesture always returns to a unified state. The flip derives from the first stream that actually has a file loaded (Primary preferred): a fileless stream's IsPaused can never change (SetPaused's Duration gate no-ops), so deriving from a fileless Primary would recompute the same stuck target forever — Space could start a loaded Secondary but never pause it. SetPaused on each context applies the same Duration > 0 gate as PlayPause, so a stream with no file loaded stays a no-op rather than echoing a spurious paused state.
        bool primaryHasFile = Primary.Duration > TimeSpan.Zero;
        bool secondaryHasFile = Secondary != null && Secondary.Duration > TimeSpan.Zero;
        VideoContext? driver = primaryHasFile ? Primary : (secondaryHasFile ? Secondary : null);
        if (driver == null)
        {
            return;
        }
        bool target = !driver.Playback.IsPaused;
        bool primaryWasPaused = Primary.Playback.IsPaused;
        Primary.SetPaused(target);
        if (Secondary == null)
        {
            return;
        }
        bool secondaryWasPaused = Secondary.Playback.IsPaused;
        Secondary.SetPaused(target);
        if (primaryHasFile && secondaryHasFile && primaryWasPaused != secondaryWasPaused)
        {
            // The two streams started in DIFFERENT play states, so converging them to a common state actually changed only ONE of them — the other was already there. That stream just "joined" the other at the live positions, which (like a single-track adjustment) defines a fresh sync point: capture the offset from the current divergence. Both streams must actually have files — a fileless stream's differed pre-state is vacuous (its SetPaused was gated to a no-op, so nothing "joined") and capturing against its zero position would store garbage. We deliberately do NOT schedule a corrective seek — the offset we just captured already matches the positions, and a deferred correction would only yank the stream that was ALREADY playing to absorb the joining stream's decoder-startup lag, which is exactly the spurious resync this fixes.
            targetOffsetSeconds = Secondary.Playback.PositionSeconds - Primary.Playback.PositionSeconds;
            return;
        }
        // Both streams started in the SAME play state — a genuine both-track action. The offset is deliberately NOT recaptured here (that would bake in prior-session drift); the continuous drift controller (ApplyDriftCorrection) absorbs the post-play startup skew against the EXISTING offset once both streams are advancing.
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
        SingleTarget.OpenFile(pathOrUri);
    }

    public void LoadPaths(IReadOnlyList<string> paths, bool replace)
    {
        SingleTarget.LoadPaths(paths, replace);
    }

    public void PlayPlaylistItem(int index)
    {
        SingleTarget.PlayPlaylistItem(index);
    }

    // Previous/next-track navigation from the control-bar buttons. Per-video commands (like PlayPlaylistItem / Open*), NOT transport: loading a different file targets SingleTarget only, it does not fan out to both PiP streams the way Seek/StepFrame do. In sync mode that's Primary, exactly like clicking a playlist row; the Primary-only load fires FileLoaded → ClearTargetOffset, which EnsureTargetOffset re-baselines on the next sync seek or correction.
    public void NextTrack()
    {
        SingleTarget.NextTrack();
    }

    public void PreviousTrack()
    {
        SingleTarget.PreviousTrack();
    }

    public void OnRenderContextReady()
    {
        if (initialFileLoaded)
        {
            return;
        }
        initialFileLoaded = true;
        if (InitialFiles != null && InitialFiles.Count > 0)
        {
            // Initial files always land in Primary — InitialFiles is a process-level "the user passed these on argv" concept, not a per-context one. A single file routes through OpenFile so it's recorded in recents the same way drag-and-drop and the file picker are; multiple files go through the same replace-load the forwarded-command-line path uses.
            if (InitialFiles.Count == 1)
            {
                Primary.OpenFile(InitialFiles[0]);
            }
            else
            {
                Primary.LoadPaths(InitialFiles, replace: true);
            }
            return;
        }
        // A remote-forwarded GApplication OnOpen can arrive between window construction and the first render-context-ready callback. If that happened, Primary already has the externally-loaded file in its playlist — don't clobber it with the saved-playlist restore. Race window is small but real on slow startup.
        if (Primary.Playlist.Items.Count > 0)
        {
            return;
        }
        if (savedPlaylists != null)
        {
            // No CLI arg: try to restore the most recent playlist. If it's multi-stream, do nothing (per user spec — don't auto-enable PiP at startup; let the user explicitly click the Recent entry to reactivate it). The single-stream path loads paused at the saved resume position.
            var last = savedPlaylists.GetMostRecent();
            if (last != null && last.StreamCount == 1)
            {
                LoadFromSaved(last.Guid, startPaused: true);
            }
        }
    }

    // Sync-mode absolute-seek contract: Primary takes the user-targeted move; Secondary is re-pinned to the ABSOLUTE position primaryTarget + offset, where offset is the stored intended delta (Secondary − Primary). The target is computed directly from the normalized scrubber value, never from Primary's live (and during a scrubber drag, stale) Position — so there is nothing to accumulate and the streams cannot drift apart across repeated seeks. Isolated mode (SelectedContext != null) routes everything to the selected stream and bypasses all of this.
    public void SeekTo(double normalizedPosition)
    {
        if (SelectedContext != null)
        {
            SelectedContext.SeekTo(normalizedPosition);
            return;
        }
        if (Secondary == null)
        {
            Primary.SeekTo(normalizedPosition);
            return;
        }
        // Sync mode. Resolve the offset BEFORE moving Primary so a baseline-on-first-use reads the current (pre-seek) divergence, then absolute-pin Secondary to the same content-time target plus the stored offset.
        double primaryTargetSeconds = normalizedPosition * Primary.Duration.TotalSeconds;
        double offset = EnsureTargetOffset();
        Primary.SeekTo(normalizedPosition);
        Secondary.Playback.Seek(primaryTargetSeconds + offset);
        // Even pinned to the same content-time target, the two `+exact` seeks land at slightly different wall-clock times (codec asymmetry / hr-seek rewind distance). The continuous drift controller absorbs that residual skew once both have settled.
    }

    // Returns the stored sync offset (Secondary − Primary), baselining it from the current divergence if it hasn't been established yet (null = "pending" after a file load / EnablePip). Once set it is immutable until the next file load (→ ClearTargetOffset) or the authorized selected→sync capture — no transport command ever writes it, which is what keeps the two streams locked at the user's intended delta. Only call when Secondary != null.
    private double EnsureTargetOffset()
    {
        if (!targetOffsetSeconds.HasValue)
        {
            targetOffsetSeconds = Secondary!.Playback.PositionSeconds - Primary.Playback.PositionSeconds;
        }
        return targetOffsetSeconds.Value;
    }

    // Chapter-seek preroll, in seconds: chapter seeks land this many seconds before the cue (0 = exactly on the cue). Synced from UserConfig by MainWindow at startup and on each preferences save. Applied here (policy, not mpv) via the pure ChapterStep resolver in SeekToChapter and StepChapter.
    public double ChapterSeekPrerollSeconds { get; set; }

    // Chapter-marker click target: a chapter's absolute cue time. Apply the preroll (ChapterStep.LandingForCue floors it at the previous cue / 0), then route through SeekTo so the click reuses the same PiP-sync absolute-pin machinery as any other absolute seek. Normalizing by SingleTarget.Duration round-trips exactly: SeekTo denormalizes against the same context's duration (SelectedContext when isolated, Primary in sync), which is the context whose chapters the scrubber is showing.
    public void SeekToChapter(double cueSeconds)
    {
        double dur = SingleTarget.Duration.TotalSeconds;
        if (dur <= 0)
        {
            return;
        }
        double target = ChapterStep.LandingForCue(SingleTarget.Chapters, cueSeconds, ChapterSeekPrerollSeconds);
        SeekTo(target / dur);
    }

    // FileLoaded handler: when either Primary or Secondary loads a new file, mpv resets the loaded context's position to 0, breaking the captured sync-mode offset entirely. Setting it "pending" (null) makes the next EnsureTargetOffset re-baseline against fresh post-load state — so the two streams adopt whatever positions they legitimately come up at (resume positions on a session restore, ~0 for fresh content loaded together) rather than collapsing one onto the other.
    private void ClearTargetOffset()
    {
        targetOffsetSeconds = null;
    }

    // Beyond this drift, a speed nudge would take too long — snap Secondary back with a hard seek instead.
    private const double HardResyncThresholdSeconds = 1.0;
    // Above this drift, run the latched full-rate catch-up (Tier 2); below it, the deadband + proportional settle (Tier 3).
    private const double CoarseCatchupThresholdSeconds = 0.050;
    // The bang-bang catch-up rate: ±5% off normal speed.
    private const double CoarseCatchupRate = 0.05;
    // Within this drift, command exactly 1.0 and idle — stops the proportional tier from chasing measurement jitter, and absorbs Tier 2's intentional overshoot. 20 ms ≈ one frame at 50 fps, below the 40 ms lipsync threshold. Tuned for the audio-clocked case (PiP audio on → mpv's master clock is audio → time-pos is ms-fine); a video-only PiP would want this larger.
    private const double SyncDeadbandSeconds = 0.020;
    // Skip redundant SetSpeed dispatcher posts when the newly-computed rate barely moved.
    private const double SpeedApplyEpsilon = 0.0005;

    // Continuous PiP drift controller, driven by PipController's repeating timer (and tests) once per tick. Replaces the old one-shot post-edge corrective seek: a three-tier control law that defends the stored offset while both streams play. Internal so the timer and the coordinator tests can drive it directly.
    internal void ApplyDriftCorrection()
    {
        // Secondary == null is the canonical "PiP is off" check; SelectedSlot != null is isolated mode (the slave is user-controlled). Either way, release any residual speed and bail.
        if (Secondary == null || SelectedSlot != null)
        {
            ResetSecondarySpeed();
            return;
        }
        if (Primary.Playback.DurationSeconds <= 0 || Secondary.Playback.DurationSeconds <= 0)
        {
            ResetSecondarySpeed();
            return;
        }
        if (Primary.Playback.IsSeeking || Secondary.Playback.IsSeeking)
        {
            // A user seek is in flight (or the just-issued hard resync hasn't landed). Skip until it settles; the absolute-pin seek already put Secondary at the right target.
            ResetSecondarySpeed();
            return;
        }
        if (Primary.Playback.IsCoreIdle || Secondary.Playback.IsCoreIdle)
        {
            // core-idle covers paused, network/cache stall, and EOF-with-keep-open. Don't correct — or baseline a pending offset — while either stream isn't actually advancing.
            ResetSecondarySpeed();
            return;
        }

        // EnsureTargetOffset baselines from the current divergence if the offset is still "pending"; on that baseline round drift computes to 0 → deadband idle, exactly a capture-and-skip.
        double offset = EnsureTargetOffset();
        double drift = (Secondary.Playback.PositionSeconds - Primary.Playback.PositionSeconds) - offset;

        // Tier 1 — hard resync: too far out to slew, snap Secondary onto Primary + offset.
        if (Math.Abs(drift) > HardResyncThresholdSeconds)
        {
            ApplySecondarySpeed(1.0);
            Secondary.Playback.Seek(Primary.Playback.PositionSeconds + offset);
            driftMode = DriftCorrectionMode.Approach;
            return;
        }

        // Tier 2 — latched catch-up: once entered, hold full ±5% until the drift overshoots zero (sign flip). Blow-out past 1 s is caught by Tier 1 above.
        if (driftMode == DriftCorrectionMode.CatchUp)
        {
            if (Math.Sign(drift) == catchUpSign)
            {
                ApplySecondarySpeed(1.0 - CoarseCatchupRate * catchUpSign);
                return;
            }
            // Overshot zero → release to the fine approach (fall through).
            driftMode = DriftCorrectionMode.Approach;
        }

        if (Math.Abs(drift) > CoarseCatchupThresholdSeconds)
        {
            catchUpSign = Math.Sign(drift);
            driftMode = DriftCorrectionMode.CatchUp;
            ApplySecondarySpeed(1.0 - CoarseCatchupRate * catchUpSign);
            return;
        }

        // Tier 3 — Approach: within the deadband command exactly 1.0; otherwise a proportional nudge that scales toward 0 as drift shrinks (±5% at 50 ms, ~±2% at the 20 ms deadband edge, where the deadband takes over).
        if (Math.Abs(drift) <= SyncDeadbandSeconds)
        {
            ApplySecondarySpeed(1.0);
            return;
        }
        double adjust = (drift / CoarseCatchupThresholdSeconds) * CoarseCatchupRate;
        ApplySecondarySpeed(1.0 - adjust);
    }

    // Push a new secondary playback speed, skipping the dispatcher post when it barely changed. Only called when Secondary != null.
    private void ApplySecondarySpeed(double speed)
    {
        if (Math.Abs(speed - secondarySpeed) < SpeedApplyEpsilon)
        {
            return;
        }
        secondarySpeed = speed;
        Secondary!.Playback.SetSpeed(speed);
    }

    // Release any active speed nudge and drop the latch. Called on every gated-out tick (paused/seeking/idle/isolated/PiP-off).
    private void ResetSecondarySpeed()
    {
        driftMode = DriftCorrectionMode.Approach;
        // Secondary == null only during the PiP-off gate: nothing to reset on mpv (Disable already disarmed the timer, and a later re-enable mints a fresh Playback at default speed 1.0). Keep our mirror honest.
        if (Secondary == null)
        {
            secondarySpeed = 1.0;
            return;
        }
        ApplySecondarySpeed(1.0);
    }

    // Diagnostic snapshot of the drift controller for the overlay. Reads live positions; safe on the main thread.
    public PipSyncDiagnostic GetSyncDiagnostic()
    {
        if (Secondary == null)
        {
            return new PipSyncDiagnostic(false, targetOffsetSeconds, 0, null, secondarySpeed, "off");
        }
        double currentOffset = Secondary.Playback.PositionSeconds - Primary.Playback.PositionSeconds;
        double? drift = targetOffsetSeconds.HasValue ? currentOffset - targetOffsetSeconds.Value : (double?)null;
        string mode;
        if (SelectedSlot != null)
        {
            mode = "isolated";
        }
        else
        {
            mode = driftMode == DriftCorrectionMode.CatchUp ? "catchup" : "approach";
        }
        return new PipSyncDiagnostic(true, targetOffsetSeconds, currentOffset, drift, secondarySpeed, mode);
    }

    // Relative seek stays a uniform fan-out across both contexts — mpv handles per-context edge clamping. Unlike SeekTo it does NOT re-pin to an absolute target: moving both by the same delta is offset-preserving by construction, and it never reads the offset or Primary's position, so it can't shift the locked sync. Same fan-out for both isolated and sync modes; isolated reduces to a one-element loop.
    public void SeekRelative(double seconds)
    {
        foreach (var ctx in RoutingTargets())
        {
            ctx.SeekRelative(seconds);
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

    // Next/previous-chapter step. One computed path for all preroll values: ChapterStep resolves the absolute target (cue minus preroll, floored so repeated steps stay monotonic — see ChapterStep) from the context's own mirror Chapters + Position, and we seek there. preroll == 0 reduces to landing exactly on the cue, reproducing the prior in-range behavior; we no longer route through mpv's `add chapter` (now removed). ResolveTarget returns null when there's nowhere to go (no chapters; next past the last; previous before the first) — a true no-op, so unlike the old fan-out we don't touch targetOffsetSeconds.
    public void StepChapter(int delta)
    {
        if (SelectedContext != null)
        {
            StepChapterIsolated(SelectedContext, delta);
            return;
        }
        if (Secondary == null)
        {
            StepChapterIsolated(Primary, delta);
            return;
        }
        // Sync mode: seek Primary to its chapter target and absolute-pin Secondary to target + offset (same single-source-of-truth contract as SeekTo). Chapter navigation tracks Primary's chapters — the stream whose chapters the scrubber shows.
        double? target = ChapterStep.ResolveTarget(Primary.Chapters, Primary.Position.TotalSeconds, delta, ChapterSeekPrerollSeconds);
        if (!target.HasValue)
        {
            return;
        }
        double offset = EnsureTargetOffset();
        Primary.Playback.Seek(target.Value);
        Secondary.Playback.Seek(target.Value + offset);
    }

    private void StepChapterIsolated(VideoContext context, int delta)
    {
        double? target = ChapterStep.ResolveTarget(context.Chapters, context.Position.TotalSeconds, delta, ChapterSeekPrerollSeconds);
        if (target.HasValue)
        {
            context.Playback.Seek(target.Value);
        }
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
        // Detach the autosave *before* DisablePip so UnbindSecondary's re-persist can't write a stale primary-only row over a stream_count=2 saved entry. Idempotent with the same call in MainWindow.OnWindowCloseRequest, which is the production-relevant call site (vm.Dispose runs after pipController.Dispose, which already triggered DisablePip).
        DetachAutosave();
        // DisablePip's null check tolerates the off case; explicit call also unsubscribes Secondary.PropertyChanged before it gets disposed.
        if (Secondary != null)
        {
            DisablePip();
        }
        Primary.PropertyChanged -= OnContextPropertyChanged;
        Primary.Playback.FileLoaded -= ClearTargetOffset;
        Primary.Dispose();
    }
}

// Diagnostic snapshot of the PiP drift-sync controller, surfaced by ViewModelMain.GetSyncDiagnostic for the diagnostic overlay. Enabled=false when PiP is off. TargetOffsetSeconds is null while the offset is still pending (not yet baselined); DriftSeconds ("catch-up required") is null in that same pending state. CurrentOffsetSeconds is the live Secondary − Primary divergence. Speed is the last rate pushed to the secondary; Mode is the controller's latch state ("off" / "isolated" / "approach" / "catchup").
public readonly record struct PipSyncDiagnostic(
    bool Enabled,
    double? TargetOffsetSeconds,
    double CurrentOffsetSeconds,
    double? DriftSeconds,
    double Speed,
    string Mode);
