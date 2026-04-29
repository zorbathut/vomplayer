using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace Vomplayer.Playback;

// Snapshot of one of mpv's subtitle tracks. Id matches mpv's track id (the value passed to the `sid` property to select it). Title and Lang are nullable because mpv reports them as property-unavailable when the source container didn't provide them. External=true marks tracks loaded via `sub-add` (or sub-auto) rather than embedded in the primary file. Plain readonly record so the SubtitleTracks snapshot can be replaced wholesale on each track-list change without consumers worrying about partial mutation.
public sealed record SubtitleTrack(int Id, string? Title, string? Lang, bool External);

public interface IPlayback : INotifyPropertyChanged, IDisposable
{
    double PositionSeconds { get; }
    double DurationSeconds { get; }
    bool IsPaused { get; }
    bool IsSeeking { get; }
    // Mirror of mpv's `core-idle` property: true when the playback core isn't actively advancing — paused, at EOF with keep-open, or no file loaded. Differs from IsPaused (which is just the user-facing pause flag), and that's the point — IsPaused stays false at EOF with keep-open=yes, but IsCoreIdle correctly flips back to true. Used to gate side-effects that should track "actually playing right now" (e.g. screensaver inhibit).
    bool IsCoreIdle { get; }

    // Snapshot of the source's subtitle tracks (embedded + external). Replaced wholesale on track-list changes; consumers should treat it as immutable and re-read on PropertyChanged. Empty when no file is loaded or the file has no subtitles.
    IReadOnlyList<SubtitleTrack> SubtitleTracks { get; }
    // mpv's currently-selected subtitle track id, or null when no track is active (sid=no, or no file loaded). Mirrors `current-tracks/sub/id`.
    int? CurrentSubtitleId { get; }

    event Action? FileLoaded;
    event Action<int>? FileEnded;

    void Initialize();
    void LoadFile(string path);
    void TogglePause();
    void Seek(double seconds);
    // Load an external subtitle file via mpv's `sub-add` command. mpv selects the new track and, if it can be parsed, fires the usual track-list change events that drive the UI refresh.
    void LoadSubtitle(string path);
    // Set the active subtitle track. Pass a track id from SubtitleTracks to select it, or null to disable subtitles (writes "no" to mpv's sid property).
    void SetSubtitle(int? trackId);
}
