using System;
using System.ComponentModel;
using Vomplayer.Controls;
using Vomplayer.Playback;
using Vomplayer.Services;
using Vomplayer.Util;
using Vomplayer.ViewModels;
using Vomplayer.Wayland;

namespace Vomplayer;

// Code-only GTK4 application window. Vertical box: video region on top, controls row below. The VM's property changes push widget updates through a switch on PropertyName; the widgets push user actions straight into VM commands.
//
// Two render paths, selected at runtime:
//   - Wayland path: video goes to a wl_subsurface (VideoArea + VideoSurface). Main surface stays sRGB; only the subsurface is HDR-tagged. UI doesn't get re-interpreted as PQ.
//   - GLArea path: video renders into Gtk.GLArea's FBO on the main surface (VideoView). HDR requests attach PQ to the main surface as before (known issue: UI looks blown out in HDR — accept on X11/Windows/macOS fallback).
public sealed class MainWindow : Gtk.ApplicationWindow
{
    private readonly Playback.Playback playback;
    private readonly ViewModelMain viewModel;
    private readonly Gtk.Scale seekScale;
    private readonly Gtk.Label positionLabel;
    private readonly Gtk.Label durationLabel;
    private readonly Gtk.Button playPauseButton;
    private readonly bool hdrRequested;
    private readonly VideoView? videoView;
    private readonly VideoArea? videoArea;
    private readonly VideoSurface? videoSurface;
    private bool updatingFromVm;
    private bool hdrAppliedOnMainSurface;
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

        Gtk.Widget videoWidget;
        if (WaylandDetect.IsWaylandBackend(GetDisplay()))
        {
            var area = new VideoArea();
            var surface = new VideoSurface(this, area, hdrRequested);
            playback.AttachRenderSurface(client => surface.SetMpvClient(client));
            surface.RenderContextReady += OnVideoRenderContextReadyWayland;
            surface.RenderFailed += OnVideoRenderFailed;
            videoArea = area;
            videoSurface = surface;
            videoWidget = area;
        }
        else
        {
            var view = new VideoView();
            playback.AttachRenderSurface(client => view.AttachClient(client));
            view.RenderContextReady += OnVideoRenderContextReadyGLArea;
            view.RenderFailed += OnVideoRenderFailed;
            videoView = view;
            videoWidget = view;
        }

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
        rootBox.Append(videoWidget);
        rootBox.Append(controlsBox);
        SetChild(rootBox);

        openButton.OnClicked += (_, _) => viewModel.OpenCommand.Execute(null);
        playPauseButton.OnClicked += (_, _) => viewModel.PlayPauseCommand.Execute(null);
        stopButton.OnClicked += (_, _) => viewModel.StopCommand.Execute(null);

        seekScale.OnValueChanged += OnSeekScaleValueChanged;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        OnCloseRequest += OnWindowCloseRequest;
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

    // Wayland path: subsurface is HDR-tagged by the shim if supported. Only tell libplacebo to target PQ if the shim actually attached the description — otherwise mpv would output PQ into a sRGB-interpreted surface and colors would be badly overdriven.
    private void OnVideoRenderContextReadyWayland()
    {
        viewModel.OnRenderContextReady();
        if (hdrRequested && videoSurface != null && videoSurface.HdrActive)
        {
            playback.EnableHdrOutput();
        }
    }

    // GLArea path: HDR has to be attached to the main surface (no subsurface). UI will look blown out because GTK widgets render sRGB values into a surface KWin interprets as PQ. Documented fallback behavior.
    private void OnVideoRenderContextReadyGLArea()
    {
        viewModel.OnRenderContextReady();
        if (hdrRequested && !hdrAppliedOnMainSurface)
        {
            int rc = HdrHelper.ApplyPqToGtkWindow(this);
            if (rc == 0)
            {
                playback.EnableHdrOutput();
            }
            hdrAppliedOnMainSurface = true;
        }
    }

    private void OnVideoRenderFailed(int code)
    {
        Console.Error.WriteLine($"[vomplayer] mpv render failed with code {code}; video rendering stopped.");
    }

    private bool OnWindowCloseRequest(Gtk.Window sender, EventArgs e)
    {
        viewModel.Dispose();
        videoSurface?.Dispose();
        playback.Dispose();
        return false;
    }
}
