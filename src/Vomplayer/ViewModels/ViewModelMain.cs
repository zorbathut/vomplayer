using System;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vomplayer.Playback;
using Vomplayer.Services;

namespace Vomplayer.ViewModels;

public sealed partial class ViewModelMain : ObservableObject, IDisposable
{
    private readonly IPlayback playback;
    private readonly IFilePicker filePicker;
    private bool isSeekingByUser;
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

    public ViewModelMain(IPlayback playback, IFilePicker filePicker)
    {
        if (playback == null)
        {
            throw new ArgumentNullException(nameof(playback));
        }
        if (filePicker == null)
        {
            throw new ArgumentNullException(nameof(filePicker));
        }
        this.playback = playback;
        this.filePicker = filePicker;
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
        playback.LoadFile(path);
    }

    [RelayCommand]
    private void PlayPause()
    {
        playback.TogglePause();
    }

    [RelayCommand]
    private void Stop()
    {
        playback.Stop();
    }

    public void OnRenderContextReady()
    {
        if (initialFileLoaded)
        {
            return;
        }
        if (!string.IsNullOrEmpty(InitialFile))
        {
            playback.LoadFile(InitialFile);
        }
        initialFileLoaded = true;
    }

    public void OnSeekDragStart()
    {
        isSeekingByUser = true;
    }

    public void OnSeekDragEnd()
    {
        isSeekingByUser = false;
        var target = SeekValue * Duration.TotalSeconds;
        playback.Seek(target);
    }

    private void OnPlaybackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IPlayback.PositionSeconds):
                Position = TimeSpan.FromSeconds(playback.PositionSeconds);
                if (!isSeekingByUser && playback.DurationSeconds > 0)
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
