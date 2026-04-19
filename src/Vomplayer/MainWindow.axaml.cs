using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Vomplayer.Controls;
using Vomplayer.Mpv;
using Vomplayer.Util;

namespace Vomplayer;

public partial class MainWindow : Window
{
    private readonly VideoView videoView;
    private readonly Button buttonOpen;
    private readonly Button buttonPlayPause;
    private readonly Button buttonStop;
    private readonly Slider sliderSeek;
    private readonly TextBlock textPosition;
    private readonly TextBlock textDuration;

    private readonly MpvClient mpv;
    private bool seekingByUser;
    private bool isPaused = true;
    private double duration;

    public string? InitialFile { get; set; }

    public MainWindow()
    {
        InitializeComponent();

        videoView = this.FindControl<VideoView>("VideoView")!;
        buttonOpen = this.FindControl<Button>("ButtonOpen")!;
        buttonPlayPause = this.FindControl<Button>("ButtonPlayPause")!;
        buttonStop = this.FindControl<Button>("ButtonStop")!;
        sliderSeek = this.FindControl<Slider>("SliderSeek")!;
        textPosition = this.FindControl<TextBlock>("TextPosition")!;
        textDuration = this.FindControl<TextBlock>("TextDuration")!;

        mpv = new MpvClient();
        // vo=libmpv tells mpv to defer VO selection until a render context is registered (see VideoView). Without it mpv picks a default VO at Initialize() time, which under Wayland is waylandvk, which ignores our render context and spawns its own window.
        mpv.SetOption("vo", "libmpv");
        mpv.SetOption("osc", "no");
        mpv.SetOption("keep-open", "yes");
        mpv.SetOption("terminal", "no");

        mpv.EventAvailable += OnMpvEventAvailable;
        mpv.PropertyChanged += OnMpvPropertyChanged;
        mpv.FileLoaded += UpdatePlayPauseLabel;

        mpv.Initialize();

        mpv.ObserveProperty("time-pos", MpvFormat.Double);
        mpv.ObserveProperty("duration", MpvFormat.Double);
        mpv.ObserveProperty("pause", MpvFormat.Flag);

        videoView.Attach(mpv);
        videoView.RenderFailed += OnVideoRenderFailed;

        buttonOpen.Click += async (_, _) =>
        {
            await OpenFileAsync();
        };
        buttonPlayPause.Click += (_, _) =>
        {
            TogglePause();
        };
        buttonStop.Click += (_, _) =>
        {
            mpv.Command("stop");
        };

        sliderSeek.AddHandler(Slider.PointerPressedEvent, (_, _) =>
        {
            seekingByUser = true;
        }, handledEventsToo: true);
        sliderSeek.AddHandler(Slider.PointerReleasedEvent, (_, _) =>
        {
            if (duration > 0)
            {
                var target = (sliderSeek.Value * duration).ToString("F3", CultureInfo.InvariantCulture);
                mpv.Command("seek", target, "absolute");
            }
            seekingByUser = false;
        }, handledEventsToo: true);

        // Dispose ordering: Avalonia tears down the visual tree (firing VideoView.OnDetachedFromVisualTree → MpvRenderContext.Dispose) before raising this Closed event, so the render context is already freed by the time we dispose mpv. mpv_render_context_free must precede mpv_terminate_destroy; the ordering relies on that Avalonia guarantee.
        Closed += (_, _) =>
        {
            mpv.Dispose();
        };

        // Defer loadfile until the render context is live, otherwise mpv starts the file with no VO attached and the video stream fails.
        videoView.RenderContextReady += () =>
        {
            if (!string.IsNullOrEmpty(InitialFile))
            {
                mpv.Command("loadfile", InitialFile);
                mpv.SetProperty("pause", "no");
                InitialFile = null;
            }
        };
    }

    private void OnMpvEventAvailable()
    {
        Dispatcher.UIThread.Post(() =>
        {
            mpv.DrainEvents();
        });
    }

    private void OnMpvPropertyChanged(PropertyChange change)
    {
        switch (change.Name)
        {
            case "time-pos":
                var pos = change.Value.AsDouble ?? 0;
                textPosition.Text = TimeFormatter.Format(pos);
                if (!seekingByUser && duration > 0)
                {
                    sliderSeek.Value = Math.Clamp(pos / duration, 0, 1);
                }
                break;
            case "duration":
                duration = change.Value.AsDouble ?? 0;
                textDuration.Text = TimeFormatter.Format(duration);
                break;
            case "pause":
                isPaused = change.Value.AsFlag ?? true;
                UpdatePlayPauseLabel();
                break;
        }
    }

    private void UpdatePlayPauseLabel()
    {
        buttonPlayPause.Content = isPaused ? "Play" : "Pause";
    }

    private void TogglePause()
    {
        mpv.SetProperty("pause", isPaused ? "no" : "yes");
    }

    private void OnVideoRenderFailed(int code)
    {
        Console.Error.WriteLine($"[vomplayer] mpv render failed with code {code}; video rendering stopped.");
    }

    private async Task OpenFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open media",
            AllowMultiple = false,
        });
        if (files.Count == 0)
        {
            return;
        }
        var path = files[0].TryGetLocalPath();
        if (path == null)
        {
            return;
        }
        mpv.Command("loadfile", path);
        mpv.SetProperty("pause", "no");
    }
}
