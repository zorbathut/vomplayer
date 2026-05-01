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
    };

    public ViewModelMain(IPlayback playback, IFilePicker filePicker, IRecentFiles recentFiles, ITrackPreferences trackPreferences, IUrlDownloader urlDownloader, IUrlPrompt urlPrompt, bool forceSdr)
    {
        // VideoContext does its own null checks; this ctor's only added value is forwarding PropertyChanged so the proxy properties above re-fire under their own names on the VM (existing OnViewModelPropertyChanged handlers in MainWindow rely on the property names matching the VM's surface, not the context's).
        Primary = new VideoContext(playback, filePicker, recentFiles, trackPreferences, urlDownloader, urlPrompt, forceSdr);
        Primary.PropertyChanged += OnContextPropertyChanged;
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
        if (secondary != null)
        {
            secondary.PropertyChanged -= OnContextPropertyChanged;
            secondary.Dispose();
        }
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
        initialFileLoaded = true;
    }

    public void SeekTo(double normalizedPosition)
    {
        foreach (var ctx in RoutingTargets())
        {
            ctx.SeekTo(normalizedPosition);
        }
    }

    public void SeekRelative(double seconds)
    {
        foreach (var ctx in RoutingTargets())
        {
            ctx.SeekRelative(seconds);
        }
    }

    public void StepFrameForward()
    {
        foreach (var ctx in RoutingTargets())
        {
            ctx.StepFrameForward();
        }
    }

    public void StepFrameBack()
    {
        foreach (var ctx in RoutingTargets())
        {
            ctx.StepFrameBack();
        }
    }

    public void StepChapter(int delta)
    {
        foreach (var ctx in RoutingTargets())
        {
            ctx.StepChapter(delta);
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
        // DisablePip's null check tolerates the off case; explicit call also unsubscribes Secondary.PropertyChanged before it gets disposed.
        if (Secondary != null)
        {
            DisablePip();
        }
        Primary.PropertyChanged -= OnContextPropertyChanged;
        Primary.Dispose();
    }
}
