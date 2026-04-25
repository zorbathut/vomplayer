using System;
using System.ComponentModel;

namespace Vomplayer.Playback;

public interface IPlayback : INotifyPropertyChanged, IDisposable
{
    double PositionSeconds { get; }
    double DurationSeconds { get; }
    bool IsPaused { get; }
    bool IsSeeking { get; }
    // Mirror of mpv's `core-idle` property: true when the playback core isn't actively advancing — paused, at EOF with keep-open, or no file loaded. Differs from IsPaused (which is just the user-facing pause flag), and that's the point — IsPaused stays false at EOF with keep-open=yes, but IsCoreIdle correctly flips back to true. Used to gate side-effects that should track "actually playing right now" (e.g. screensaver inhibit).
    bool IsCoreIdle { get; }

    event Action? FileLoaded;
    event Action<int>? FileEnded;

    void Initialize();
    void LoadFile(string path);
    void TogglePause();
    void Stop();
    void Seek(double seconds);
}
