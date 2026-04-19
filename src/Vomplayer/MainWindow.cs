using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
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
public sealed partial class MainWindow : Gtk.ApplicationWindow
{
    // Raw-signal P/Invoke. GirCore 0.7.0 cannot marshal Gtk.EventControllerLegacy's `event` signal payload: GdkEvent is its own fundamental GType (id 196), and GirCore's Value.Extract only handles GObject/Boxed/Enum/Flags/Param/Variant — it throws NotSupportedException for anything else. We connect to the signal via raw g_signal_connect_data and read the event type directly, skipping GirCore's extraction entirely.
    // Explicit SONAMEs: `libgtk-4.so.1` / `libgobject-2.0.so.0` are the runtime-installed libraries. The bare `libgtk-4.so` / `libgobject-2.0.so` names only exist as dev-package symlinks and would fail NativeLibrary resolution on runtime-only hosts.
    private const string GObjectLib = "libgobject-2.0.so.0";
    private const string GtkLib = "libgtk-4.so.1";

    [LibraryImport(GObjectLib, EntryPoint = "g_signal_connect_data", StringMarshalling = StringMarshalling.Utf8)]
    private static partial ulong SignalConnectData(IntPtr instance, string detailedSignal, IntPtr cHandler, IntPtr data, IntPtr destroyData, int connectFlags);

    [LibraryImport(GObjectLib, EntryPoint = "g_signal_handler_disconnect")]
    private static partial void SignalHandlerDisconnect(IntPtr instance, ulong handlerId);

    [LibraryImport(GtkLib, EntryPoint = "gdk_event_get_event_type")]
    private static partial int GdkEventGetEventType(IntPtr evt);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SeekLegacyEventCallback(IntPtr sender, IntPtr evt, IntPtr userData);

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
    // Seek-scale state machine. idle = both false; holding = userHolding; settling = awaitingSeekSettle (post-release, waiting for mpv's in-flight seek to report a time-pos distinct from the pre-release one). `seekValueAtRelease` is the baseline we wait to move away from — gating on "time-pos has actually advanced" avoids a race where mpv fires `seeking=false` before its `time-pos` update, which would otherwise let a stale SeekValue push flicker the scale.
    private bool userHolding;
    private bool awaitingSeekSettle;
    private double seekValueAtRelease;
    // Dedupe OnValueChanged against the last value we sent to SeekTo, reset to NaN on each press. NaN comparison is always false so the first post-press value-change always seeks; subsequent emissions at the same value (from any source) are skipped. Cheap defense against spurious re-emissions.
    private double lastUserSeek = double.NaN;
    // Delegate is retained as an instance field so it stays rooted while the signal connection lives. Handler id + controller pointer let us disconnect synchronously in close-request, before the delegate field is nulled and before GTK tears the widget down — closing the narrow window where a late event could dispatch into a collectable delegate.
    private SeekLegacyEventCallback? seekLegacyCallback;
    private ulong seekLegacyHandlerId;
    private IntPtr seekLegacyControllerHandle;
    private bool hdrAppliedOnMainSurface;

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
        RemoveScaleLongPressGesture(seekScale);

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

        // Gtk.Scale's internal gesture CLAIMS the pointer sequence on press, which denies any sibling gesture — their `released` signal never fires, `end`/`cancel` fire at claim-time instead. EventControllerLegacy is NOT a gesture (per gtk_widget_run_controllers), so it's unaffected by the claim protocol and sees raw ButtonPress/ButtonRelease at capture phase. We bypass GirCore's broken GdkEvent marshalling by using raw g_signal_connect_data; the callback returns 0 (gboolean FALSE) so the scale's gestures still receive the event — returning TRUE would short-circuit them and the slider would stop responding to mouse entirely.
        var legacy = Gtk.EventControllerLegacy.New();
        legacy.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        seekScale.AddController(legacy);

        seekLegacyCallback = OnSeekScaleRawEvent;
        seekLegacyControllerHandle = legacy.Handle.DangerousGetHandle();
        seekLegacyHandlerId = SignalConnectData(
            seekLegacyControllerHandle,
            "event",
            Marshal.GetFunctionPointerForDelegate(seekLegacyCallback),
            IntPtr.Zero, IntPtr.Zero, 0);

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        playback.PropertyChanged += OnPlaybackPropertyChangedForSeekSettle;
        OnCloseRequest += OnWindowCloseRequest;
    }

    // Seek on every user-driven change; `updatingFromVm` breaks the VM→scale→VM loop; `lastUserSeek` dedupes repeated emissions at the same value (see field comment). Scroll-wheel and keyboard Arrow keys take this path too — no press/release, so `userHolding` stays false and the standard VM-push flow resumes after each seek.
    private void OnSeekScaleValueChanged(Gtk.Range sender, EventArgs e)
    {
        if (updatingFromVm)
        {
            return;
        }
        double v = seekScale.GetValue();
        if (v == lastUserSeek)
        {
            return;
        }
        lastUserSeek = v;
        viewModel.SeekTo(v);
    }

    // Callback marshalled into GTK via raw g_signal_connect_data. Runs on the main thread (GTK signal delivery). Reads the event type via P/Invoke (not GirCore) and drives the state machine. Always returns 0 (gboolean FALSE) so the scale's internal gestures still process the event — returning nonzero would short-circuit them.
    private int OnSeekScaleRawEvent(IntPtr sender, IntPtr evt, IntPtr userData)
    {
        Gdk.EventType type = (Gdk.EventType)GdkEventGetEventType(evt);
        switch (type)
        {
            case Gdk.EventType.ButtonPress:
            case Gdk.EventType.TouchBegin:
                userHolding = true;
                awaitingSeekSettle = false;
                lastUserSeek = double.NaN;
                break;
            case Gdk.EventType.ButtonRelease:
            case Gdk.EventType.TouchEnd:
            case Gdk.EventType.TouchCancel:
            case Gdk.EventType.GrabBroken:
                userHolding = false;
                if (playback.IsSeeking)
                {
                    awaitingSeekSettle = true;
                    seekValueAtRelease = viewModel.SeekValue;
                }
                else
                {
                    PushVmSeekValueToScale();
                }
                break;
        }
        return 0;
    }

    // mpv fires `seeking=false` and the new `time-pos` from the same playback-loop step, but the events may arrive in either order via the property-change queue. If `seeking` arrives first and we push VM.SeekValue right then, we'd push the pre-seek stale position and flicker old→new on the next tick. Instead, keep this handler as a pure safety net: clear `awaitingSeekSettle` and let the SeekValue-change handler do the push when VM.SeekValue moves away from the release-time baseline (see OnViewModelPropertyChanged case SeekValue).
    private void OnPlaybackPropertyChangedForSeekSettle(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IPlayback.IsSeeking)
            && awaitingSeekSettle && !playback.IsSeeking && !userHolding
            && viewModel.SeekValue != seekValueAtRelease)
        {
            awaitingSeekSettle = false;
            PushVmSeekValueToScale();
        }
    }

    private void PushVmSeekValueToScale()
    {
        updatingFromVm = true;
        seekScale.SetValue(viewModel.SeekValue);
        updatingFromVm = false;
    }

    // Gtk.Range adds a GtkGestureLongPress in gtk_range_init that activates zoom/fine-tune mode after ~1s of holding the slider. Zoom makes the trough thicker and rescales cursor motion 1:1 — useful for a precision slider, useless for a seek bar. Worse, the activation path recomputes the slider origin and any post-activation motion (even sub-pixel hand jitter) emits a value-changed at ~the held position, which we'd re-SeekTo, causing a visible video jump back to the held position mid-hold. Removing the controller kills the long-press trigger entirely. Shift+click fine-tune on the slider still works (different code path in gtk_range_click_gesture_pressed) — deliberate modifier, leave alone.
    private static void RemoveScaleLongPressGesture(Gtk.Scale scale)
    {
        var controllers = scale.ObserveControllers();
        uint n = controllers.GetNItems();
        for (uint i = 0; i < n; i++)
        {
            var item = controllers.GetObject(i);
            if (item is Gtk.GestureLongPress lp)
            {
                scale.RemoveController(lp);
                return;
            }
        }
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
                if (userHolding)
                {
                    break;
                }
                // While settling, only resume tracking once mpv's time-pos has actually moved away from the value it had at release. That's the reliable signal that the seek has landed — checking `!IsSeeking` alone would race with the `seeking` property arriving before the matching time-pos.
                if (awaitingSeekSettle)
                {
                    if (viewModel.SeekValue == seekValueAtRelease)
                    {
                        break;
                    }
                    awaitingSeekSettle = false;
                }
                PushVmSeekValueToScale();
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
        playback.PropertyChanged -= OnPlaybackPropertyChangedForSeekSettle;
        // Disconnect the raw signal BEFORE releasing our delegate reference. GTK flushes pending events during window destruction, which can happen after this handler returns; if we dropped the delegate root first, a late dispatch would land in freed memory. Disconnect is synchronous — once it returns, the function pointer is unwired.
        if (seekLegacyHandlerId != 0 && seekLegacyControllerHandle != IntPtr.Zero)
        {
            SignalHandlerDisconnect(seekLegacyControllerHandle, seekLegacyHandlerId);
            seekLegacyHandlerId = 0;
            seekLegacyControllerHandle = IntPtr.Zero;
        }
        seekLegacyCallback = null;
        viewModel.Dispose();
        videoSurface?.Dispose();
        playback.Dispose();
        return false;
    }
}
