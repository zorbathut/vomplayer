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

    private MpvClient? mpv;
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

        videoView.HandleCreated += OnVideoHandleCreated;

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
            mpv?.Command("stop");
        };

        sliderSeek.AddHandler(Slider.PointerPressedEvent, (_, _) =>
        {
            seekingByUser = true;
        }, handledEventsToo: true);
        sliderSeek.AddHandler(Slider.PointerReleasedEvent, (_, _) =>
        {
            if (duration > 0 && mpv != null)
            {
                var target = (sliderSeek.Value * duration).ToString("F3", CultureInfo.InvariantCulture);
                mpv.Command("seek", target, "absolute");
            }
            seekingByUser = false;
        }, handledEventsToo: true);

        Closed += (_, _) =>
        {
            mpv?.Dispose();
        };
    }

    private void OnVideoHandleCreated(Avalonia.Platform.IPlatformHandle handle)
    {
        // NativeControlHost can re-fire this if the control is reparented; today we only
        // support the one-shot case and ignore subsequent handles. If reparenting ever
        // matters, mpv's wid will need to be swapped here (or we move to the render API).
        if (mpv != null)
        {
            return;
        }

        mpv = new MpvClient();
        mpv.SetOption("wid", ((long)handle.Handle).ToString(CultureInfo.InvariantCulture));
        // mpv's default Linux VO is waylandvk, which ignores wid and spawns its own window.
        // Force the X11 EGL context when we actually have an X11 child window to embed into.
        if (handle.HandleDescriptor == "XID")
        {
            mpv.SetOption("gpu-context", "x11egl");
        }
        mpv.SetOption("input-default-bindings", "yes");
        mpv.SetOption("input-vo-keyboard", "yes");
        mpv.SetOption("osc", "no");
        mpv.SetOption("keep-open", "yes");
        mpv.SetOption("terminal", "no");

        mpv.EventAvailable += () =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                mpv?.DrainEvents();
            });
        };
        mpv.PropertyChanged += OnMpvPropertyChanged;
        mpv.FileLoaded += UpdatePlayPauseLabel;

        mpv.Initialize();

        mpv.ObserveProperty("time-pos", MpvFormat.Double);
        mpv.ObserveProperty("duration", MpvFormat.Double);
        mpv.ObserveProperty("pause", MpvFormat.Flag);

        if (!string.IsNullOrEmpty(InitialFile))
        {
            mpv.Command("loadfile", InitialFile);
            mpv.SetProperty("pause", "no");
        }
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
        if (mpv == null)
        {
            return;
        }
        mpv.SetProperty("pause", isPaused ? "no" : "yes");
    }

    private async Task OpenFileAsync()
    {
        if (mpv == null)
        {
            return;
        }
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
