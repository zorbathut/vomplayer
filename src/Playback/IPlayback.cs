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

    event Action? FileLoaded;
    event Action<int>? FileEnded;
    // Fires once per dispatcher-level track-list re-walk, AFTER the three per-kind properties (VideoTracks / AudioTracks / SubtitleTracks) have been updated on the main thread. Distinct from PropertyChanged on the lists individually because consumers (like the directory-preferences applier) need an "all three are settled" signal — relying on PropertyChanged for one specific kind misses files where that kind is empty (no notification fires for an empty→empty update due to the dedup gate). FileLoaded alone isn't enough either: FileLoaded fires before the dispatcher has finished re-walking and pushing the new lists.
    event Action? TracksReloaded;

    void Initialize();
    void LoadFile(string path);
    void TogglePause();
    void Seek(double seconds);
    // Relative seek in source-content seconds (negative = backward). mpv handles edge clamping (won't seek before 0 or past duration). Same pre-load gate as Seek — no-op when no file is loaded.
    void SeekRelative(double seconds);
    // Step exactly one decoded frame. mpv pauses playback if not already paused.
    void StepFrameForward();
    void StepFrameBack();
    // Jump by `delta` chapters relative to the current chapter (typically ±1). No-op when the file has no chapters or the resulting index is out of range — mpv's `add chapter` clamps past the ends. Lives on IPlayback rather than computed VM-side from Chapters[] because mpv applies the chapter→time lookup atomically with the seek; routing through the VM mirror would risk a stale Chapters list across a postToMainThread hop.
    void StepChapter(int delta);
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
}
