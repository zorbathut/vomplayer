using System;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vomplayer.Playback;
using Vomplayer.Services;
using Vomplayer.UserData;

namespace Vomplayer.ViewModels;

public sealed partial class ViewModelMain : ObservableObject, IDisposable
{
    private readonly IPlayback playback;
    private readonly IFilePicker filePicker;
    private readonly IRecentFiles recentFiles;
    private bool initialFileLoaded;

    [ObservableProperty]
    private TimeSpan position;

    [ObservableProperty]
    private TimeSpan duration;

    [ObservableProperty]
    private bool isPaused = true;

    [ObservableProperty]
    private double seekValue;

    public string? InitialFile { get; set; }

    public ViewModelMain(IPlayback playback, IFilePicker filePicker, IRecentFiles recentFiles)
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
        this.playback = playback;
        this.filePicker = filePicker;
        this.recentFiles = recentFiles;
        this.playback.PropertyChanged += OnPlaybackPropertyChanged;
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        var path = await filePicker.PickVideoFileAsync("Open media");
        if (path == null)
        {
            return;
        }
        recentFiles.Record(path);
        playback.LoadFile(path);
    }

    // Direct path/URI load, bypassing the file picker. Used by drag-and-drop. Accepts whatever libmpv accepts: a local filesystem path, or a remote URI (http://, https://, smb://, …).
    public void OpenFile(string pathOrUri)
    {
        recentFiles.Record(pathOrUri);
        playback.LoadFile(pathOrUri);
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
                break;
            case nameof(IPlayback.DurationSeconds):
                Duration = TimeSpan.FromSeconds(playback.DurationSeconds);
                break;
            case nameof(IPlayback.IsPaused):
                IsPaused = playback.IsPaused;
                break;
        }
    }

    public void Dispose()
    {
        playback.PropertyChanged -= OnPlaybackPropertyChanged;
    }
}
