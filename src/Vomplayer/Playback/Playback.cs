using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Vomplayer.Controls;
using Vomplayer.Mpv;

namespace Vomplayer.Playback;

// Owns MpvClient and the mapping from mpv events to strongly-typed observable state. No Avalonia dependency — the one cross-thread coupling is injected via Action<Action> (pass Dispatcher.UIThread.Post in production, a => a() in tests). Never exposes MpvClient past the service boundary; VideoView attaches through AttachRenderSurface.
public sealed partial class Playback : ObservableObject, IPlayback
{
    private readonly MpvClient mpv;
    private readonly Action<Action> postToMainThread;

    [ObservableProperty]
    private double positionSeconds;

    [ObservableProperty]
    private double durationSeconds;

    [ObservableProperty]
    private bool isPaused = true;

    public event Action? FileLoaded;
    public event Action<int>? FileEnded;

    public Playback(Action<Action> postToMainThread)
    {
        if (postToMainThread == null)
        {
            throw new ArgumentNullException(nameof(postToMainThread));
        }
        this.postToMainThread = postToMainThread;
        mpv = new MpvClient();
    }

    public void Initialize()
    {
        // vo=libmpv defers VO selection until a render context is registered — otherwise mpv picks a default VO (on Wayland that's waylandvk, which ignores our render context and spawns its own window).
        mpv.SetOption("vo", "libmpv");
        mpv.SetOption("osc", "no");
        mpv.SetOption("keep-open", "yes");
        mpv.SetOption("terminal", "no");

        mpv.EventAvailable += OnMpvEventAvailable;
        mpv.PropertyChanged += OnMpvPropertyChanged;
        mpv.FileLoaded += OnMpvFileLoaded;
        mpv.FileEnded += OnMpvFileEnded;
        mpv.Shutdown += OnMpvShutdown;

        mpv.Initialize();

        mpv.ObserveProperty("time-pos", MpvFormat.Double);
        mpv.ObserveProperty("duration", MpvFormat.Double);
        mpv.ObserveProperty("pause", MpvFormat.Flag);
    }

    public void LoadFile(string path)
    {
        if (path == null)
        {
            throw new ArgumentNullException(nameof(path));
        }
        mpv.Command("loadfile", path);
        mpv.SetProperty("pause", "no");
    }

    public void TogglePause()
    {
        mpv.SetProperty("pause", IsPaused ? "no" : "yes");
    }

    public void Stop()
    {
        mpv.Command("stop");
    }

    public void Seek(double seconds)
    {
        var target = seconds.ToString("F3", CultureInfo.InvariantCulture);
        mpv.Command("seek", target, "absolute");
    }

    // Attaches the VideoView's render surface to this playback session. Keeps MpvClient behind the service boundary — only Playback is allowed to hand the native handle to VideoView.
    public void AttachRenderSurface(VideoView videoView)
    {
        if (videoView == null)
        {
            throw new ArgumentNullException(nameof(videoView));
        }
        videoView.AttachClient(mpv);
    }

    // Callable for tests. In production it's invoked indirectly via postToMainThread.
    public void ProcessEvents()
    {
        mpv.DrainEvents();
    }

    private void OnMpvEventAvailable()
    {
        postToMainThread(ProcessEvents);
    }

    private void OnMpvPropertyChanged(PropertyChange change)
    {
        switch (change.Name)
        {
            case "time-pos":
                PositionSeconds = change.Value.AsDouble ?? 0;
                break;
            case "duration":
                DurationSeconds = change.Value.AsDouble ?? 0;
                break;
            case "pause":
                IsPaused = change.Value.AsFlag ?? true;
                break;
            default:
                throw new InvalidOperationException($"Unexpected property name: {change.Name}");
        }
    }

    private void OnMpvFileLoaded()
    {
        FileLoaded?.Invoke();
    }

    private void OnMpvFileEnded(int reason)
    {
        FileEnded?.Invoke(reason);
    }

    private void OnMpvShutdown()
    {
        Console.Error.WriteLine("[vomplayer] mpv signalled shutdown");
    }

    public void Dispose()
    {
        // Null event invocation lists first so a late mid-dispose notification can't fire into a torn-down subscriber — mirrors MpvClient.Dispose.
        FileLoaded = null;
        FileEnded = null;
        mpv.Dispose();
    }
}
