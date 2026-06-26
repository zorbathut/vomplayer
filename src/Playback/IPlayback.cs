using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace Vomplayer.Playback;

// Snapshot of one of mpv's tracks (video, audio, or subtitle — same record shape covers all three since the relevant fields are identical). Id matches mpv's track id (the value passed to the kind-specific property — vid/aid/sid — to select it). Title and Lang are nullable because mpv reports them as property-unavailable when the source container didn't provide them. External=true marks tracks loaded via `*-add` (or auto) rather than embedded in the primary file; ExternalFilename is the basename of the source file when external (null for embedded tracks) — used by the per-directory preferences matcher to identify "this is the same external file across directory siblings". Plain readonly record so the snapshot can be replaced wholesale on each track-list change without consumers worrying about partial mutation.
public sealed record MediaTrack(int Id, string? Title, string? Lang, bool External, string? ExternalFilename);

// Snapshot of one of mpv's chapters. Index is the position in `chapter-list` (0-based, matches mpv's `chapter` writable property for jump-to). Title is nullable because some containers carry only timestamps. TimeSeconds is the chapter start time in source-content seconds (mpv exposes this via `chapter-list/N/time` as a double).
public sealed record MediaChapter(int Index, string? Title, double TimeSeconds);

public interface IPlayback : INotifyPropertyChanged, IDisposable
{
    double PositionSeconds { get; }
    double DurationSeconds { get; }
    bool IsPaused { get; }
    bool IsSeeking { get; }
    // Mirror of mpv's `core-idle` property: true when the playback core isn't actively advancing — paused, at EOF with keep-open, or no file loaded. Differs from IsPaused (which is just the user-facing pause flag), and that's the point — IsPaused stays false at EOF with keep-open=yes, but IsCoreIdle correctly flips back to true. Used to gate side-effects that should track "actually playing right now" (e.g. screensaver inhibit).
    bool IsCoreIdle { get; }

    // Mirror of mpv's `eof-reached` property: true when the current file has hit end-of-file. With keep-open=yes (which we set), mpv parks at the last frame at EOF and does NOT fire MPV_EVENT_END_FILE — so the FileEnded event is unreliable for detecting natural EOF. eof-reached is the right signal. Used by playlist auto-advance, which reads the false→true transition.
    bool IsEofReached { get; }

    // mpv's `volume` property (percent — typical range 0..100, mpv allows up to volume-max which defaults to 130). Mute is independent of volume: muting doesn't reset Volume to 0, so unmuting restores the prior level.
    double Volume { get; }
    bool IsMuted { get; }

    // Snapshots of the source's tracks per kind (embedded + external). Replaced wholesale on track-list changes; consumers should treat them as immutable and re-read on PropertyChanged. Empty when no file is loaded or the file has no tracks of that kind.
    IReadOnlyList<MediaTrack> VideoTracks { get; }
    IReadOnlyList<MediaTrack> AudioTracks { get; }
    IReadOnlyList<MediaTrack> SubtitleTracks { get; }
    // Source's chapter list (ordered by mpv index). Replaced wholesale on chapter-list changes (file load, eject, etc.). Empty for chapter-less files.
    IReadOnlyList<MediaChapter> Chapters { get; }
    // mpv's currently-selected track id per kind, or null when no track is active (vid/aid/sid=no, or no file loaded). Mirrors `current-tracks/{video,audio,sub}/id`.
    int? CurrentVideoId { get; }
    int? CurrentAudioId { get; }
    int? CurrentSubtitleId { get; }

    // Whether the currently-loaded source's `video-params/gamma` is one of mpv's HDR transfer functions (PQ / HLG). False for SDR sources or when no file is loaded. Drives VideoContext's HDR policy and the diagnostic overlay's `source=` row. Promoted to the interface so per-video HDR policy lives behind the IPlayback seam — VideoContext doesn't need a concrete Playback reference.
    bool IsSourceHdr { get; }

    // The hwdec backend mpv settled on for the current source ("vaapi", "no", null when no file is loaded, etc.). Read by the diagnostic overlay's `hwdec=` row only.
    string? HwdecCurrent { get; }

    // Bounded transcript of mpv log lines from the components that drive hwdec negotiation (vd / backend / ffmpeg). Captured at "v" level so the full "tried X, X failed because Y, selected Z" trail is preserved. Cleared per file. Read by the diagnostic overlay; printed to stdout when the overlay is shown.
    IReadOnlyList<string> HwdecTranscript { get; }

    // Display aspect of the loaded source (dwidth/dheight, square-pixel rectangle the video should render into). Null when no file is loaded or no video stream is present. Drives the PiP layout's per-source aspect-correct sizing.
    double? VideoAspect { get; }

    // Source's container frame rate (fps). Null when no file is loaded, no video stream is present, or mpv hasn't decided yet (synthesized initial fire pre-load lands null). Used by the coordinator's sync-mode StepFrame: the absolute-seconds delta of "advance one frame on Primary" is 1/Primary.VideoFps, which we then apply to Secondary so the two streams stay locked to the same content offset across asymmetric frame rates. For VFR sources mpv reports the average; the resulting Secondary delta is approximate but stays bounded within a frame's worth of drift per step.
    double? VideoFps { get; }

    // Mirror of mpv's `estimated-vf-fps` — rolling-average decoded frame rate. Drives the FPS trust monitor (Playback owns the monitor); surfaced here for the diagnostic overlay. Null pre-load and on audio-only files.
    double? EstimatedVfFps { get; }

    // True iff the FPS trust monitor still believes the declared container-fps. False only after sustained estimated-vs-declared divergence within a file. Reset to true on every LoadFile. Drives VideoContext.ApplyVrrPolicy to clear the multiplier on VFR / mistagged-CFR sources.
    bool IsSourceFpsTrusted { get; }

    // Diagnostic-only string explaining the most recent trust transition (e.g. "divergence sustained 4.2s (declared=25.000, est=15.100)"). Empty until the monitor first flips; carries through subsequent in-state observations until the next transition.
    string FpsTrustReason { get; }

    // Mirror of mpv's `media-title` property: the source's metadata title (container/stream tag) when present, falling back to the filename without path/extension. Null when no file is loaded. For yt-dlp-downloaded URLs the cached file is named `%(title)s.%(ext)s`, so the fallback still surfaces the upstream video title rather than a hash. Drives the main-window title display.
    string? MediaTitle { get; }

    event Action? FileLoaded;
    event Action<int>? FileEnded;
    // Fires once per dispatcher-level track-list re-walk, AFTER the three per-kind properties (VideoTracks / AudioTracks / SubtitleTracks) have been updated on the main thread. Distinct from PropertyChanged on the lists individually because consumers (like the directory-preferences applier) need an "all three are settled" signal — relying on PropertyChanged for one specific kind misses files where that kind is empty (no notification fires for an empty→empty update due to the dedup gate). FileLoaded alone isn't enough either: FileLoaded fires before the dispatcher has finished re-walking and pushing the new lists.
    event Action? TracksReloaded;
    // Fires on transitions of IsSourceHdr (PQ/HLG ↔ neither). VideoContext subscribes to drive its ApplyHdrPolicy.
    event Action<bool>? SourceHdrChanged;
    // Fires on transitions of IsSourceFpsTrusted. VideoContext subscribes to drive ApplyVrrPolicy. Sticky-Untrusted-within-a-load means at most one transition (true→false) per file load; the inverse re-trust on a fresh LoadFile / new container-fps land also fires.
    event Action<bool>? IsSourceFpsTrustedChanged;

    void Initialize();
    // startPaused: when true, the file loads with pause=yes so mpv stops on the first frame instead of starting playback. Used by RestorePlaylist for startup auto-load and Recent-menu open so the user sees a thumbnail at the resume position. Implementation must keep the pause-state decision atomic with the loadfile dispatch — a separate SetPaused call before or after LoadFile races against mpv's load-time defaults.
    void LoadFile(string path, bool startPaused);
    void TogglePause();
    // Set the paused state explicitly. Idempotent — calling SetPaused(true) on an already-paused playback is a no-op. The coordinator's no-selection PlayPause path uses this to converge two streams that have drifted into different pause states (TogglePause on each independently could leave them divergent if one was already at the target).
    void SetPaused(bool paused);
    void Seek(double seconds);
    // Relative seek in source-content seconds (negative = backward). mpv handles edge clamping (won't seek before 0 or past duration). Same pre-load gate as Seek — no-op when no file is loaded.
    void SeekRelative(double seconds);
    // Step exactly one decoded frame. mpv pauses playback if not already paused.
    void StepFrameForward();
    void StepFrameBack();
    // Load an external audio or subtitle file via mpv's `audio-add` / `sub-add` command. mpv selects the new track and, if it can be parsed, fires the usual track-list change events that drive the UI refresh. (Video doesn't get a Load* method — multi-angle external video is rare enough not to justify the surface; the existing track-list still picks up any video that's added via mpv's command line or scripted entry.)
    void LoadAudio(string path);
    void LoadSubtitle(string path);
    // Set the active track per kind. Pass a track id from the matching list to select it, or null to disable that stream (writes "no" to mpv's vid/aid/sid property).
    void SetVideo(int? trackId);
    void SetAudio(int? trackId);
    void SetSubtitle(int? trackId);
    // Absolute volume in percent. Caller is responsible for clamping; the mpv property accepts 0..volume-max and refuses out-of-range values.
    void SetVolume(double percent);
    // Relative volume change in percent points (e.g. +5 / -5). Implementations clamp against 0 and the active volume-max.
    void AdjustVolume(double deltaPercent);
    void ToggleMute();

    // Set mpv's `target-prim`/`target-trc`/`target-peak` to the PQ/BT.2020 viewport so mpv emits PQ pass-through into the FBO. Caller must already have a PQ-tagged surface attached or the compositor will misinterpret the pixels. Inverse pair with DisableHdrOutput.
    void EnableHdrOutput();
    // Drop the PQ targets so mpv falls back to its auto target-* defaults (SDR tone-mapping handled by mpv's gl_video pipeline). See Playback.cs for why we don't pin gamma2.2 explicitly.
    void DisableHdrOutput();

    // Apply the VRR frame-multiplication filter so mpv emits frames at outputFps. Replaces any prior fps filter; expects to be paired with ClearFrameMultiplier on file unload / out-of-VRR-window transitions. Implemented via mpv's `vf set` command — SetProperty("vf", ...) reinitializes the entire video chain (visible flash, possible hwdec re-negotiation), the command path mutates the chain in place.
    void SetFrameMultiplier(double outputFps);
    void ClearFrameMultiplier();
}
