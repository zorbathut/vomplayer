using System;
using System.ComponentModel;
using Vomplayer.Controls;
using Vomplayer.Playback;
using Vomplayer.Services;
using Vomplayer.Util;
using Vomplayer.ViewModels;

namespace Vomplayer;

// Code-only GTK4 application window. Vertical box: VideoView on top, controls row below. The VM's property changes push widget updates through a switch on PropertyName; the widgets push user actions straight into VM commands.
public sealed class MainWindow : Gtk.ApplicationWindow
{
    private readonly Playback.Playback playback;
    private readonly ViewModelMain viewModel;
    private readonly VideoView videoView;
    private readonly Gtk.Scale seekScale;
    private readonly Gtk.Label positionLabel;
    private readonly Gtk.Label durationLabel;
    private readonly Gtk.Button playPauseButton;
    private readonly bool hdrRequested;
    private bool updatingFromVm;
    private bool hdrApplied;
    private uint pendingDragEndId;

    public MainWindow(Gtk.Application app, Playback.Playback playback, string? initialFile, bool hdrRequested)
    {
        if (app == null)
        {
            throw new ArgumentNullException(nameof(app));
        }
        if (playback == null)
        {
            throw new ArgumentNullException(nameof(playback));
        }
        this.playback = playback;
        this.hdrRequested = hdrRequested;

        SetApplication(app);
        Title = hdrRequested ? "Vomplayer" : "Vomplayer — SDR";
        SetDefaultSize(1280, 720);

        var filePicker = new FilePickerGtk(this);
        viewModel = new ViewModelMain(playback, filePicker);
        viewModel.InitialFile = initialFile;

        videoView = new VideoView();
        playback.AttachRenderSurface(videoView);
        videoView.RenderContextReady += OnVideoRenderContextReady;
        videoView.RenderFailed += OnVideoRenderFailed;

        var openButton = Gtk.Button.NewWithLabel("Open");
        playPauseButton = Gtk.Button.NewWithLabel("Play");
        var stopButton = Gtk.Button.NewWithLabel("Stop");

        seekScale = Gtk.Scale.NewWithRange(Gtk.Orientation.Horizontal, 0.0, 1.0, 0.001);
        seekScale.SetHexpand(true);
        seekScale.SetDrawValue(false);

        positionLabel = Gtk.Label.New("00:00");
        durationLabel = Gtk.Label.New("00:00");

        var controlsBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        controlsBox.SetMarginStart(6);
        controlsBox.SetMarginEnd(6);
        controlsBox.SetMarginTop(6);
        controlsBox.SetMarginBottom(6);
        controlsBox.Append(openButton);
        controlsBox.Append(playPauseButton);
        controlsBox.Append(stopButton);
        controlsBox.Append(positionLabel);
        controlsBox.Append(seekScale);
        controlsBox.Append(durationLabel);

        var rootBox = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        rootBox.Append(videoView);
        rootBox.Append(controlsBox);
        SetChild(rootBox);

        openButton.OnClicked += (_, _) => viewModel.OpenCommand.Execute(null);
        playPauseButton.OnClicked += (_, _) => viewModel.PlayPauseCommand.Execute(null);
        stopButton.OnClicked += (_, _) => viewModel.StopCommand.Execute(null);

        seekScale.OnValueChanged += OnSeekScaleValueChanged;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        OnCloseRequest += OnWindowCloseRequest;
    }

    public VideoView VideoView
    {
        get
        {
            return videoView;
        }
    }

    // GTK4's Gtk.Scale doesn't expose drag-start / drag-end signals we can reliably observe (its internal gesture claims pointer sequences, cancelling sibling GestureClick, and GirCore 0.7.0 can't marshal raw GdkEvent to let us use EventControllerLegacy). Instead we auto-detect the end of a drag: the first ValueChanged opens a drag, and 150 ms after the last change we close it. Short enough to feel responsive, long enough to cover a continuous drag's sample cadence.
    private void OnSeekScaleValueChanged(Gtk.Range sender, EventArgs e)
    {
        if (updatingFromVm)
        {
            return;
        }

        if (pendingDragEndId == 0)
        {
            viewModel.OnSeekDragStart();
        }
        else
        {
            GLib.Functions.SourceRemove(pendingDragEndId);
        }

        viewModel.SeekValue = seekScale.GetValue();

        pendingDragEndId = GLib.Functions.TimeoutAdd(
            (int)GLib.Constants.PRIORITY_DEFAULT,
            150,
            () =>
            {
                pendingDragEndId = 0;
                viewModel.OnSeekDragEnd();
                return false;
            });
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewModelMain.Position):
                positionLabel.SetLabel(TimeFormatter.Format(viewModel.Position.TotalSeconds));
                break;
            case nameof(ViewModelMain.Duration):
                durationLabel.SetLabel(TimeFormatter.Format(viewModel.Duration.TotalSeconds));
                break;
            case nameof(ViewModelMain.IsPaused):
                playPauseButton.SetLabel(viewModel.IsPaused ? "Play" : "Pause");
                break;
            case nameof(ViewModelMain.SeekValue):
                updatingFromVm = true;
                seekScale.SetValue(viewModel.SeekValue);
                updatingFromVm = false;
                break;
        }
    }

    private void OnVideoRenderContextReady()
    {
        viewModel.OnRenderContextReady();
        if (hdrRequested && !hdrApplied)
        {
            int rc = HdrHelper.ApplyPqToGtkWindow(this);
            if (rc == 0)
            {
                // Surface is now PQ/BT.2020; libplacebo must target the same, or the compositor will interpret sRGB-encoded output as PQ and colors will be badly overdriven.
                playback.EnableHdrOutput();
            }
            hdrApplied = true;
        }
    }

    private void OnVideoRenderFailed(int code)
    {
        Console.Error.WriteLine($"[vomplayer] mpv render failed with code {code}; video rendering stopped.");
    }

    private bool OnWindowCloseRequest(Gtk.Window sender, EventArgs e)
    {
        viewModel.Dispose();
        playback.Dispose();
        return false;
    }
}
