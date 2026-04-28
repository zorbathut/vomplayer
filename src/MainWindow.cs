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
    private readonly Gtk.Box controlsBox;
    private readonly Gtk.Box rootBox;
    private readonly Gtk.Overlay videoOverlay;
    private readonly Gtk.Box noVideoBg;
    private readonly Gtk.PopoverMenuBar menuBar;
    private readonly bool forceSdr;
    private readonly VideoView? videoView;
    private readonly VideoArea? videoArea;
    private readonly VideoSurface? videoSurface;
    private readonly DiagnosticOverlay diagnosticOverlay;
    // Fullscreen state is a mirror of Gtk.Window.Fullscreened — the notify::fullscreened handler is authoritative. This lets compositor/WM-initiated fullscreen exits (Super-key, window menu, tiling WM shortcut) restore the controls even though our own toggles didn't run.
    private bool isFullscreen;
    private uint controlsHideTimeoutId;
    private bool isClosing;
    private int motionEventCount;
    private double armPositionX = double.NaN;
    private double armPositionY = double.NaN;
    private const uint ControlsHideDelayMs = 2000;
    // Ignore motion events whose position is within this many px of the position at the last timer arm. Filters out sub-pixel jitter and any spurious synthetic events the compositor/GTK might emit. Real user motion easily exceeds this.
    private const double MotionDeadZonePx = 3.0;
    // Set VOMPL_FS_DEBUG=1 in the environment to dump [fs] traces to stderr covering timer arms, timer fires, motion events (rate-limited), and the hide path — helps diagnose why autohide isn't firing if the default logic fails in the wild.
    private static readonly bool FsDebug = Environment.GetEnvironmentVariable("VOMPL_FS_DEBUG") == "1";
    private static readonly System.Diagnostics.Stopwatch FsStopwatch = System.Diagnostics.Stopwatch.StartNew();

    private static void FsLog(string msg)
    {
        if (FsDebug)
        {
            Console.Error.WriteLine($"[fs t={FsStopwatch.Elapsed.TotalSeconds:F3}] {msg}");
        }
    }
    // Screensaver/idle inhibit cookie returned by Gtk.Application.Inhibit. 0 ⇒ not currently inhibited (Gtk uses 0 as the failure / not-applied sentinel). Held while mpv reports core-idle=false (i.e. actually decoding/displaying); released on every transition back to idle (pause, EOF with keep-open, no file loaded) so we don't keep the system awake when playback parks at end-of-file.
    private uint screensaverInhibitCookie;
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

    public MainWindow(Gtk.Application app, Playback.Playback playback, string? initialFile, bool forceSdr)
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
        this.forceSdr = forceSdr;

        SetApplication(app);
        Title = forceSdr ? "Vomplayer — SDR" : "Vomplayer";
        SetDefaultSize(1280, 720);
        AddCssClass("vompl-main-window");

        InstallVomplCss();

        var filePicker = new FilePickerGtk(this);
        viewModel = new ViewModelMain(playback, filePicker);
        viewModel.InitialFile = initialFile;

        Gtk.Widget videoWidget;
        if (WaylandDetect.IsWaylandBackend(GetDisplay()))
        {
            var area = new VideoArea();
            var surface = new VideoSurface(this, area);
            playback.AttachRenderSurface(d => surface.SetMpvDispatcher(d));
            surface.RenderContextReady += OnVideoRenderContextReadyWayland;
            surface.RenderFailed += OnVideoRenderFailed;
            surface.FirstFrameRendered += OnVideoFirstFrameRendered;
            videoArea = area;
            videoSurface = surface;
            videoWidget = area;
            // Combined HDR policy: enable HDR viewport only when both the source is PQ/HLG AND the current output advertises HDR. Two signals drive re-evaluation — Playback.SourceHdrChanged (per-video) and VideoSurface.CurrentOutputHdrChanged (output probe completes, user drags window cross-monitor, monitor HDR toggled). --sdr skips the subscriptions entirely so an explicit "force SDR" request can't be overridden by either signal; hdrActiveState is stamped Forced here so the diagnostic overlay renders "SDR (--sdr)" instead of plain "SDR" and the override is visible at a glance.
            if (!forceSdr)
            {
                playback.SourceHdrChanged += OnSourceHdrChanged;
                surface.CurrentOutputHdrChanged += OnCurrentOutputHdrChanged;
            }
            else
            {
                hdrActiveState = HdrActiveState.Forced;
            }
        }
        else
        {
            var view = new VideoView();
            playback.AttachRenderSurface(d => view.AttachDispatcher(d));
            view.RenderContextReady += OnVideoRenderContextReadyGLArea;
            view.RenderFailed += OnVideoRenderFailed;
            videoView = view;
            videoWidget = view;
        }

        var openButton = Gtk.Button.NewWithLabel("Open");
        playPauseButton = Gtk.Button.NewWithLabel("Play");

        seekScale = Gtk.Scale.NewWithRange(Gtk.Orientation.Horizontal, 0.0, 1.0, 0.001);
        seekScale.SetHexpand(true);
        seekScale.SetDrawValue(false);
        RemoveScaleLongPressGesture(seekScale);

        positionLabel = Gtk.Label.New("00:00");
        durationLabel = Gtk.Label.New("00:00");

        controlsBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        // Spacing around the bar comes from CSS padding on .vompl-controls-bar / .osd, not from widget margins. Margins sit OUTSIDE the background area — with a transparent window underneath, margins would show desktop through. Padding sits inside the background, so the bar's opaque fill extends to its outer edges. vompl-chrome gives it the theme bg; vompl-controls-bar adds the padding. Split so the fullscreen OSD swap (below) only touches the background/padding pair and leaves vompl-chrome off (OSD has its own semi-transparent fill).
        controlsBox.AddCssClass("vompl-chrome");
        controlsBox.AddCssClass("vompl-controls-bar");
        controlsBox.Append(openButton);
        controlsBox.Append(playPauseButton);
        controlsBox.Append(positionLabel);
        controlsBox.Append(seekScale);
        controlsBox.Append(durationLabel);

        // Video sits inside an Overlay so fullscreen can move the controls on top of the video (valign=End + "osd" style class) without taking space in the layout. In windowed mode the overlay has no overlay children — controls are packed below in rootBox as usual.
        videoOverlay = Gtk.Overlay.New();
        videoOverlay.SetChild(videoWidget);
        videoOverlay.SetHexpand(true);
        videoOverlay.SetVexpand(true);

        // Black placeholder that fills the video region while the Wayland subsurface has no buffer attached yet (subsurface placed below the transparent parent shows the desktop through otherwise). Hidden permanently on mpv's FirstFrameRendered.
        noVideoBg = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        noVideoBg.AddCssClass("vompl-no-video-bg");
        noVideoBg.SetHalign(Gtk.Align.Fill);
        noVideoBg.SetValign(Gtk.Align.Fill);
        noVideoBg.SetHexpand(true);
        noVideoBg.SetVexpand(true);
        videoOverlay.AddOverlay(noVideoBg);

        // Diagnostic overlay sits above noVideoBg in stacking order (later AddOverlay = higher). Anchored top-right (Halign=End, Valign=Start) so it never overlaps controlsBox (Valign=End) even when controlsBox is reparented in fullscreen. Constructed before BuildMenuBar to match the file's "assign then build menu" pattern (cf. viewModel at line 106); the menu action closure resolves `this.diagnosticOverlay` lazily at invoke time, so the ordering is stylistic rather than load-bearing.
        diagnosticOverlay = new DiagnosticOverlay(playback, videoSurface, () => hdrActiveState == HdrActiveState.Hdr);
        videoOverlay.AddOverlay(diagnosticOverlay.Widget);

        menuBar = BuildMenuBar(app);

        rootBox = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        rootBox.Append(menuBar);
        rootBox.Append(videoOverlay);
        rootBox.Append(controlsBox);
        SetChild(rootBox);

        openButton.OnClicked += (_, _) => viewModel.OpenCommand.Execute(null);
        playPauseButton.OnClicked += (_, _) => viewModel.PlayPauseCommand.Execute(null);

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

        // Window-level key controller at Capture phase: f/F/Space/Escape work regardless of which child has focus. Capture pre-empts focused children, so a focused Gtk.Button never sees Space and can't double-fire play/pause. The scale uses arrow keys for seek; no text entry exists — nothing here is a key a focused child legitimately needs. Unlike EventControllerLegacy, OnKeyPressed delivers primitives (uint, uint, ModifierType), not a GdkEvent* — no GirCore marshalling hazard here.
        var keyController = Gtk.EventControllerKey.New();
        keyController.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        keyController.OnKeyPressed += OnWindowKeyPressed;
        AddController(keyController);

        // Mouse-motion drives the auto-hide timer while fullscreen. Capture phase so the controller fires before any child-widget controller — motion isn't normally consumed, but Capture guarantees delivery regardless.
        var motionController = Gtk.EventControllerMotion.New();
        motionController.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        motionController.OnMotion += OnWindowPointerMotion;
        AddController(motionController);

        // Double-click on the video widget toggles fullscreen. GestureClick does participate in the gesture-claim protocol, but the video widgets (VideoArea/VideoView) attach no other gestures, so there's nothing to contend with. Single-click is unbound; any future click-to-pause must consider that double-click fires a single-press first (GestureClick delivers pressed for each of the two presses, with NPress incrementing).
        var clickGesture = Gtk.GestureClick.New();
        clickGesture.Button = (uint)Gdk.Constants.BUTTON_PRIMARY;
        clickGesture.OnPressed += OnVideoClickPressed;
        videoWidget.AddController(clickGesture);

        // Drag-and-drop loading. Register a Gio.File drop target — GTK's content-format negotiation has built-in deserializers from text/uri-list (which file managers and browsers universally offer) to a single GFile, taking the first URI. We don't register Gdk.FileList because GirCore 0.7.0 doesn't expose its element accessor; in practice every common DnD source also offers text/uri-list, so the GFile path matches. Accept Copy|Move|Link because Wayland/X11 sources negotiate the action set with the destination — we read the file either way, so refusing Move would just reject otherwise-valid drops. Attached to the window so drops anywhere — including over chrome — are accepted; the user shouldn't have to aim at the video region.
        var dropTarget = Gtk.DropTarget.New(Gio.FileHelper.GetGType(), Gdk.DragAction.Copy | Gdk.DragAction.Move | Gdk.DragAction.Link);
        dropTarget.OnDrop += OnFileDrop;
        AddController(dropTarget);

        // Mirror the real fullscreen state rather than treating a local bool as authority. Covers compositor/WM-initiated un-fullscreen that bypasses our key/gesture paths.
        OnNotify += OnWindowNotify;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        playback.PropertyChanged += OnPlaybackPropertyChangedForSeekSettle;
        playback.PropertyChanged += OnPlaybackPropertyChangedForScreensaver;
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

    // Drive Gtk.Application.Inhibit/Uninhibit off mpv state. Predicate is `!IsCoreIdle || IsSeeking`: core-idle is the primary signal (and the right one over IsPaused, because mpv with keep-open=yes parks at EOF without setting pause=yes — IsPaused-driven inhibit would persist after a file ends). IsSeeking is folded in because mpv may briefly flip core-idle=true while a seek restarts; without it we'd thrash the inhibit (one DBus round-trip per seek) on a drag-scrub. Subscribed to both property names below — either changing re-evaluates.
    private void OnPlaybackPropertyChangedForScreensaver(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IPlayback.IsCoreIdle)
            && e.PropertyName != nameof(IPlayback.IsSeeking))
        {
            return;
        }
        ApplyScreensaverInhibit();
    }

    // Has Inhibit ever returned 0? On compositors that don't implement org.gnome.SessionManager / org.freedesktop.ScreenSaver (e.g. some wlroots-based compositors without the screensaver service), Gtk.Application.Inhibit returns 0 — the application gets no inhibit and the screensaver kicks in normally. CLAUDE.md bans silent error handling, so log once on the first such failure (no spam if every transition fails). Mirrors the ApplyHdrPolicy "compositor refused" pattern.
    private bool screensaverInhibitFailureLogged;

    private void ApplyScreensaverInhibit()
    {
        bool wantInhibit = !playback.IsCoreIdle || playback.IsSeeking;
        // The local `cookie != 0` guard is what makes calls safe to repeat — Gtk.Application.Inhibit itself is NOT idempotent (each call registers a new inhibitor and returns a fresh cookie). Without the guard, every property change while playing would leak a cookie.
        if (wantInhibit && screensaverInhibitCookie == 0)
        {
            var app = (Gtk.Application?)GetApplication();
            if (app == null)
            {
                return;
            }
            uint cookie = app.Inhibit(this, Gtk.ApplicationInhibitFlags.Idle, "Playing video");
            if (cookie == 0)
            {
                if (!screensaverInhibitFailureLogged)
                {
                    screensaverInhibitFailureLogged = true;
                    Console.Error.WriteLine("[vompl] screensaver: Gtk.Application.Inhibit returned 0 — compositor doesn't expose an inhibit interface (org.gnome.SessionManager / org.freedesktop.ScreenSaver). Screensaver may activate during playback.");
                }
                return;
            }
            screensaverInhibitCookie = cookie;
        }
        else if (!wantInhibit && screensaverInhibitCookie != 0)
        {
            var app = (Gtk.Application?)GetApplication();
            if (app != null)
            {
                app.Uninhibit(screensaverInhibitCookie);
            }
            screensaverInhibitCookie = 0;
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

    // Wayland path: pre-stage the SDR image description on the subsurface synchronously, before any frame can render. Necessary to cover three paths that ApplyHdrPolicy alone doesn't reach: (a) `--sdr` mode, where the policy event subscriptions are skipped entirely; (b) first-SDR-file in a fresh process, where the source-gamma observation arrives as null→bt.1886 and never crosses isSourceHdr's transition gate, so SourceHdrChanged never fires; (c) any compositor whose first wl_surface.enter races ahead of our bridge subscription (the comment in TryCreateRenderContext acknowledges the first enter is often missed). In all three, without this kickoff the subsurface stays untagged and KWin's HDR-aware compositor blows out gamma22 SDR output the same way the original bug did. We deliberately do NOT touch mpv's target-* options here — that interacts badly with mpv's auto resolution (tried, reverted) and ApplyHdrPolicy will set them appropriately when it does run for HDR sources.
    private void OnVideoRenderContextReadyWayland()
    {
        if (videoSurface != null)
        {
            videoSurface.SetHdr(false);
        }
        viewModel.OnRenderContextReady();
    }

    private void OnVideoFirstFrameRendered()
    {
        noVideoBg.SetVisible(false);
    }

    // GLArea path: always SDR. The old main-surface HDR attach produced a blown-out UI (GTK widgets render sRGB values into a surface KWin interprets as PQ) and can't be toggled per-file without destroying the GTK surface, so this path is SDR-only; HDR content is tonemapped by mpv's auto targeting.
    private void OnVideoRenderContextReadyGLArea()
    {
        viewModel.OnRenderContextReady();
    }

    // Outcome of the most recent ApplyHdrPolicy. Kept as a four-state enum (rather than a bool) so the stderr log on transition into Failed can distinguish "compositor refused" from "intentional SDR" — the diagnostic overlay collapses every non-Hdr state to "SDR" and doesn't care about the internal distinction, but ApplyHdrPolicy does.
    private enum HdrActiveState
    {
        // Subsurface untagged. Default pre-first-apply, or the explicit outcome when source or display is SDR.
        Sdr,
        // HDR tagging applied to the subsurface and mpv advanced to PQ targets.
        Hdr,
        // HDR was requested (source PQ/HLG + display HDR-capable) but VideoSurface.SetHdr(true) returned non-zero. Pixels scan out as SDR.
        Failed,
        // --sdr command-line override in effect. ApplyHdrPolicy never runs; state stays here for the process lifetime.
        Forced,
    }

    // Latest value from Playback.SourceHdrChanged. Playback fires on transitions only, not on every frame, so caching here lets ApplyHdrPolicy combine it with the current output bit on output-side changes too.
    private bool lastSourceHdr;
    // Initialized in the constructor: Forced when --sdr is in effect (policy never runs), Sdr otherwise (default pre-first-apply — subsurface untagged, equivalent at the pixel level to an explicit SDR policy).
    private HdrActiveState hdrActiveState;
    private static readonly bool logHdr = Environment.GetEnvironmentVariable("VOMPL_LOG_HDR") == "1";

    private void OnSourceHdrChanged(bool isHdr)
    {
        lastSourceHdr = isHdr;
        ApplyHdrPolicy();
    }

    private void OnCurrentOutputHdrChanged()
    {
        ApplyHdrPolicy();
    }

    // Single point of decision for the HDR-viewport question. Drives the wp_color_management_v1 tag on the subsurface and mpv's target-* options. Policy: PQ tag iff source is HDR (regardless of display HDR-capability). Rationale: mpv-via-libmpv is forced to use the older gl_video pipeline whose tone-map curve clips highlights hard at the source mastering-display peak (1000 nits → all-white SDR for typical files); KWin 6.x runs HDR-aware compositing with libplacebo, which has nicer curves. By tagging PQ and having mpv emit pass-through PQ, we hand HDR→SDR conversion to the compositor and inherit its tone-map. Caveats: we haven't instrumented the curve-equality claim ("matches gpu-next") — KWin's tonemap quality depends on its libplacebo version, the user's KDE settings, and the per-output ICC profile if any; on a misconfigured compositor this could be worse than mpv's tonemap, but in our testing the trade is a clear win. Display HDR-capability is no longer load-bearing for the policy — it's still tracked for the diagnostic overlay's `display=` row, but doesn't gate the tag. Window-spanning a PQ-tagged subsurface across HDR + SDR outputs is now correct by construction: the compositor pass-throughs on the HDR side and tonemaps on the SDR side per-output. On enable, stage the PQ/BT.2020 image description first so the very next eglSwapBuffers flushes it atomically with the first PQ-encoded mpv frame; only advance mpv to PQ targeting if the shim confirms the description is attachable (else mpv tone-maps to gamma22 — fallback for non-CM compositors). On disable, mirror in reverse order so the next SDR frame lands on an SDR-tagged surface (GAMMA22/BT.709) — see VideoSurface.SetHdr for why we tag SDR explicitly rather than leave the surface in compositor-defined territory.
    private void ApplyHdrPolicy()
    {
        if (videoSurface == null)
        {
            return;
        }
        bool? outputHdr = videoSurface.CurrentOutputIsHdr;
        bool wantHdr = lastSourceHdr;
        HdrActiveState newState;
        if (wantHdr)
        {
            bool applied = videoSurface.SetHdr(true) == 0;
            if (applied)
            {
                playback.EnableHdrOutput();
                newState = HdrActiveState.Hdr;
            }
            else
            {
                // Shim refused (no wp_color_manager_v1 advertised, description build failed, etc). Do NOT advance mpv to PQ targets — that would leave mpv rendering PQ-encoded pixels into an untagged surface, which the compositor would interpret as sRGB and display as blown-out whites. Staying SDR on both halves is safe.
                newState = HdrActiveState.Failed;
            }
        }
        else
        {
            // SetHdr(false) attaches the SDR (GAMMA22/BT.709) image description. -1 means the compositor doesn't advertise wp_color_manager_v1 (or description build failed) — surface stays untagged, which is implementation-defined per the wp_color_management_v1 spec. On most compositors that's fine (treated as sRGB); on KWin with an HDR output present, an untagged surface is misinterpreted and SDR output blows out. We can't do anything about it from here, but log so a future bug report ("blown out on a non-KWin compositor") is diagnosable.
            int sdrRc = videoSurface.SetHdr(false);
            if (sdrRc != 0 && hdrActiveState != HdrActiveState.Sdr)
            {
                Console.Error.WriteLine("[vompl] hdr: SDR tag attach failed (no wp_color_manager_v1) — surface left untagged; if SDR output looks blown out, your compositor is misinterpreting untagged surfaces");
            }
            playback.DisableHdrOutput();
            newState = HdrActiveState.Sdr;
        }
        if (newState != hdrActiveState)
        {
            // Silent error handling is banned (CLAUDE.md). Failed = the user asked for HDR and the compositor refused; surface it unconditionally rather than gating on VOMPL_LOG_HDR. Only logged on transitions into Failed so a stuck-in-Failed state doesn't spam.
            if (newState == HdrActiveState.Failed)
            {
                Console.Error.WriteLine("[vompl] hdr: requested but compositor refused (no wp_color_manager_v1 or description build failed) — staying SDR");
            }
        }
        if (logHdr)
        {
            string outStr = outputHdr.HasValue ? (outputHdr.Value ? "HDR" : "SDR") : "unknown";
            Console.Error.WriteLine($"[vompl] hdr policy: source={(lastSourceHdr ? "HDR" : "SDR")} display={outStr} → {newState} (was {hdrActiveState})");
        }
        hdrActiveState = newState;
    }

    private void OnVideoRenderFailed(int code)
    {
        Console.Error.WriteLine($"[vomplayer] mpv render failed with code {code}; video rendering stopped.");
    }

    // The main window's own CSS background must be transparent so the video subsurface (placed below the parent wl_surface) shows through wherever no widget paints opaque. GTK4's render tree is parent-first, child-on-top with alpha compositing — a child's transparent background can't punch a hole through an opaque parent, so the only way to get alpha=0 anywhere is for the window itself not to paint. Keep the rule scoped by the vompl-main-window class so About/file/other dialogs (also Gtk.Window) aren't dragged along. In practice the rendered transparent area is exactly the video widget region, since every other chrome widget opts in to opaque via the vompl-chrome class (menu bar, controls bar in windowed mode). Priority APPLICATION (600) beats theme defaults.
    private static void InstallVomplCss()
    {
        var provider = Gtk.CssProvider.New();
        provider.LoadFromString("window.vompl-main-window { background: transparent; } .vompl-chrome { background-color: @theme_bg_color; } .vompl-controls-bar { padding: 6px; } .osd { padding: 6px; } .vompl-no-video-bg { background-color: black; } .vompl-diagnostic { background-color: rgba(0,0,0,0.55); color: #e0e0e0; padding: 8px 10px; margin: 8px; border-radius: 6px; font-family: monospace; font-size: 10pt; }");
        Gtk.StyleContext.AddProviderForDisplay(Gdk.Display.GetDefault()!, provider, (uint)Gtk.Constants.STYLE_PROVIDER_PRIORITY_APPLICATION);
    }

    // Keyboard dispatch is split across two sources; check both when debugging input:
    //   1. This Capture-phase handler: f, F, Space, Escape.
    //   2. Gtk.Application accelerators registered in BuildMenuBar: Ctrl+O, Ctrl+Q, F11.
    // Escape stays here (not an accelerator) because its behavior is conditional — only exit-fullscreen, don't swallow otherwise.
    private bool OnWindowKeyPressed(Gtk.EventControllerKey sender, Gtk.EventControllerKey.KeyPressedSignalArgs args)
    {
        uint keyval = args.Keyval;
        if (keyval == (uint)Gdk.Constants.KEY_f
            || keyval == (uint)Gdk.Constants.KEY_F)
        {
            SetFullscreen(!isFullscreen);
            return true;
        }
        if (keyval == (uint)Gdk.Constants.KEY_space)
        {
            viewModel.PlayPauseCommand.Execute(null);
            return true;
        }
        if (keyval == (uint)Gdk.Constants.KEY_Escape)
        {
            if (isFullscreen)
            {
                SetFullscreen(false);
                return true;
            }
            return false;
        }
        return false;
    }

    private void OnWindowPointerMotion(Gtk.EventControllerMotion sender, Gtk.EventControllerMotion.MotionSignalArgs args)
    {
        if (!isFullscreen)
        {
            return;
        }
        motionEventCount++;
        double x = args.X;
        double y = args.Y;
        if (!double.IsNaN(armPositionX))
        {
            double dx = x - armPositionX;
            double dy = y - armPositionY;
            if (dx * dx + dy * dy < MotionDeadZonePx * MotionDeadZonePx)
            {
                if (motionEventCount % 30 == 1)
                {
                    FsLog($"motion #{motionEventCount} x={x:F1} y={y:F1} (in dead zone, ignored)");
                }
                return;
            }
        }
        armPositionX = x;
        armPositionY = y;
        if (motionEventCount % 10 == 1)
        {
            FsLog($"motion #{motionEventCount} x={x:F1} y={y:F1}");
        }
        if (!controlsBox.GetVisible())
        {
            controlsBox.SetVisible(true);
        }
        SetCursorFromName(null);
        ArmControlsHideTimer();
    }

    // GTK delivers a Gio.File for the matched format. Prefer GetPath() (local filesystem) and fall back to GetUri() so remote URIs (smb://, http://, …) flow through to mpv, which handles those natively. Returning false signals GTK to draw the rejected-drop cursor; per CLAUDE.md (silent error handling banned) we also log so a future "DnD didn't work" report has something to chase.
    private bool OnFileDrop(Gtk.DropTarget sender, Gtk.DropTarget.DropSignalArgs args)
    {
        if (args.Value.GetObject() is Gio.File file)
        {
            string? target = file.GetPath() ?? file.GetUri();
            if (!string.IsNullOrEmpty(target))
            {
                viewModel.OpenFile(target);
                return true;
            }
        }
        Console.Error.WriteLine("[vompl] dnd: dropped value did not yield a usable path or URI; ignoring");
        return false;
    }

    private void OnVideoClickPressed(Gtk.GestureClick sender, Gtk.GestureClick.PressedSignalArgs args)
    {
        if (args.NPress == 2)
        {
            SetFullscreen(!isFullscreen);
        }
    }

    // Notify handler for the "fullscreened" property. Resyncs when the compositor/WM changes the window state behind our back (e.g., a tiling-WM shortcut that un-fullscreens). If the state already matches, SetFullscreen already applied the visibility logic synchronously — no work left.
    private void OnWindowNotify(GObject.Object sender, GObject.Object.NotifySignalArgs args)
    {
        if (args.Pspec.GetName() != "fullscreened")
        {
            return;
        }
        if (Fullscreened == isFullscreen)
        {
            return;
        }
        ApplyFullscreenState(Fullscreened);
    }

    // isFullscreen tracks user intent, not the mirrored GTK state: it updates synchronously on keypress/click so a rapid F-F or F-then-doubleclick before notify arrives toggles correctly. OnWindowNotify resyncs if the compositor changes state externally.
    private void SetFullscreen(bool on)
    {
        if (isFullscreen == on)
        {
            return;
        }
        if (on)
        {
            Fullscreen();
        }
        else
        {
            Unfullscreen();
        }
        ApplyFullscreenState(on);
    }

    // Reparent controlsBox between rootBox (windowed, stacked below video) and videoOverlay (fullscreen, floating over the bottom of the video). Toggling visibility on the overlay child doesn't resize the video widget, so controls appearing/disappearing during auto-hide don't cause the video to rescale. The background/padding class pair swaps at the same time: windowed uses vompl-chrome + vompl-controls-bar (opaque theme bg + 6px padding); fullscreen uses osd (GTK's built-in semi-transparent dark over video). Parent-guarded: if ApplyFullscreenState ever runs twice for the same state (e.g., from our toggle and again from notify::fullscreened after a compositor-initiated transition racing with our own), the double-remove/double-add would fault on a non-child widget.
    private void ApplyFullscreenState(bool on)
    {
        FsLog($"apply on={on}");
        isFullscreen = on;
        motionEventCount = 0;
        armPositionX = double.NaN;
        armPositionY = double.NaN;
        // Asymmetric with controlsBox, which reparents into videoOverlay as an OSD in fullscreen. Menu bars don't OSD well, so we just hide unconditionally; users can still use F11/f/Escape/double-click and the action accelerators (Ctrl+O, Ctrl+Q) while fullscreen.
        menuBar.SetVisible(!on);
        if (on)
        {
            if (controlsBox.Parent == rootBox)
            {
                rootBox.Remove(controlsBox);
            }
            controlsBox.SetValign(Gtk.Align.End);
            controlsBox.RemoveCssClass("vompl-chrome");
            controlsBox.RemoveCssClass("vompl-controls-bar");
            controlsBox.AddCssClass("osd");
            if (controlsBox.Parent != videoOverlay)
            {
                videoOverlay.AddOverlay(controlsBox);
            }
            controlsBox.SetVisible(true);
            ArmControlsHideTimer();
        }
        else
        {
            CancelControlsHideTimer();
            // Restore the cursor unconditionally — the timer may have hidden it while we were fullscreen, and exits via Escape/F11/compositor don't fire pointer motion to undo that.
            SetCursorFromName(null);
            if (controlsBox.Parent == videoOverlay)
            {
                videoOverlay.RemoveOverlay(controlsBox);
            }
            controlsBox.RemoveCssClass("osd");
            controlsBox.AddCssClass("vompl-chrome");
            controlsBox.AddCssClass("vompl-controls-bar");
            controlsBox.SetValign(Gtk.Align.Fill);
            if (controlsBox.Parent != rootBox)
            {
                rootBox.Append(controlsBox);
            }
            controlsBox.SetVisible(true);
        }
    }

    private void ArmControlsHideTimer()
    {
        CancelControlsHideTimer();
        controlsHideTimeoutId = GLib.Functions.TimeoutAdd(
            (int)GLib.Constants.PRIORITY_DEFAULT,
            ControlsHideDelayMs,
            OnControlsHideTimeout);
        FsLog($"arm id={controlsHideTimeoutId}");
    }

    private void CancelControlsHideTimer()
    {
        if (controlsHideTimeoutId != 0)
        {
            FsLog($"cancel id={controlsHideTimeoutId}");
            GLib.Functions.SourceRemove(controlsHideTimeoutId);
            controlsHideTimeoutId = 0;
        }
    }

    // `isClosing` guards against the case where SourceRemove fails to cancel because the callback is already in-flight — dropping into a torn-down window would touch collected native widgets. QueueDraw on videoOverlay forces GTK to repaint the region the hidden controls previously occupied — without it, the parent wl_surface's pixel state under the (now-hidden) widget can linger on-screen because the video subsurface below is what's actually animating frame-to-frame, not the parent.
    private bool OnControlsHideTimeout()
    {
        controlsHideTimeoutId = 0;
        FsLog($"timeout fired isClosing={isClosing} isFS={isFullscreen} visible={controlsBox.GetVisible()}");
        if (isClosing)
        {
            return false;
        }
        if (isFullscreen)
        {
            controlsBox.SetVisible(false);
            // "none" is a standard CSS cursor name; GTK maps it to a blank cursor on every backend we care about. Setting it on the window covers the video region too: the wl_subsurface is opaque to GTK, but its input region is empty (see hdr_helper.c), so pointer events route to the parent GTK surface and the window-level cursor is what the compositor draws there.
            SetCursorFromName("none");
            videoOverlay.QueueDraw();
            FsLog($"hide applied visible={controlsBox.GetVisible()}");
        }
        return false;
    }

    private bool OnWindowCloseRequest(Gtk.Window sender, EventArgs e)
    {
        isClosing = true;
        CancelControlsHideTimer();
        // Release any held screensaver inhibit on close. Belt-and-braces: when the app exits and its DBus connection drops, the session daemon auto-releases inhibitors anyway, but doing this explicitly avoids depending on that and keeps clean shutdown behavior if the window is closed without quitting (multi-window future).
        if (screensaverInhibitCookie != 0)
        {
            var app = (Gtk.Application?)GetApplication();
            if (app != null)
            {
                app.Uninhibit(screensaverInhibitCookie);
            }
            screensaverInhibitCookie = 0;
        }
        playback.PropertyChanged -= OnPlaybackPropertyChangedForSeekSettle;
        playback.PropertyChanged -= OnPlaybackPropertyChangedForScreensaver;
        playback.SourceHdrChanged -= OnSourceHdrChanged;
        if (videoSurface != null)
        {
            videoSurface.CurrentOutputHdrChanged -= OnCurrentOutputHdrChanged;
        }
        // Disconnect the raw signal BEFORE releasing our delegate reference. GTK flushes pending events during window destruction, which can happen after this handler returns; if we dropped the delegate root first, a late dispatch would land in freed memory. Disconnect is synchronous — once it returns, the function pointer is unwired.
        if (seekLegacyHandlerId != 0 && seekLegacyControllerHandle != IntPtr.Zero)
        {
            SignalHandlerDisconnect(seekLegacyControllerHandle, seekLegacyHandlerId);
            seekLegacyHandlerId = 0;
            seekLegacyControllerHandle = IntPtr.Zero;
        }
        seekLegacyCallback = null;
        // Dispose the overlay before the objects it reads (playback, videoSurface): Dispose cancels its 1 Hz timer, ensuring no post-teardown tick fires into a disposed Playback.
        diagnosticOverlay.Dispose();
        viewModel.Dispose();
        videoSurface?.Dispose();
        playback.Dispose();
        return false;
    }
}
