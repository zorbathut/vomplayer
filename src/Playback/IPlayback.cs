using System;
using System.ComponentModel;

namespace Vomplayer.Playback;

public interface IPlayback : INotifyPropertyChanged, IDisposable
{
    double PositionSeconds { get; }
    double DurationSeconds { get; }
    bool IsPaused { get; }
    bool IsSeeking { get; }

    event Action? FileLoaded;
    event Action<int>? FileEnded;

    void Initialize();
    void LoadFile(string path);
    void TogglePause();
    void Stop();
    void Seek(double seconds);
}
