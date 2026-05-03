using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Vomplayer.Controls;
using Vomplayer.Playback;
using Vomplayer.Services;
using Vomplayer.UserData;
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
    private const string GLibLib = "libglib-2.0.so.0";
    private const string GioLib = "libgio-2.0.so.0";

    [LibraryImport(GObjectLib, EntryPoint = "g_signal_connect_data", StringMarshalling = StringMarshalling.Utf8)]
    private static partial ulong SignalConnectData(IntPtr instance, string detailedSignal, IntPtr cHandler, IntPtr data, IntPtr destroyData, int connectFlags);

    [LibraryImport(GObjectLib, EntryPoint = "g_signal_handler_disconnect")]
    private static partial void SignalHandlerDisconnect(IntPtr instance, ulong handlerId);

    [LibraryImport(GtkLib, EntryPoint = "gdk_event_get_event_type")]
    private static partial int GdkEventGetEventType(IntPtr evt);

    // GdkFileList accessors. GirCore 0.7.0 binds GdkFileList.NewFromArray / NewFromList but NOT gdk_file_list_get_files (the only way to walk the contents from C#). Raw P/Invoke matches the established style for things GirCore doesn't bind cleanly (cf. g_signal_connect_data above). gdk symbols ship inside libgtk-4.so.1 on every target we care about (GTK doesn't split libgdk on its modern release line).
    [LibraryImport(GtkLib, EntryPoint = "gdk_file_list_get_files")]
    private static partial IntPtr GdkFileListGetFiles(IntPtr fileList);

    [LibraryImport(GioLib, EntryPoint = "g_file_get_path")]
    private static partial IntPtr GFileGetPath(IntPtr gfile);

    [LibraryImport(GioLib, EntryPoint = "g_file_get_uri")]
    private static partial IntPtr GFileGetUri(IntPtr gfile);

    [LibraryImport(GLibLib, EntryPoint = "g_free")]
    private static partial void GFree(IntPtr ptr);

    [LibraryImport(GLibLib, EntryPoint = "g_slist_free")]
    private static partial void GSListFree(IntPtr list);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SeekLegacyEventCallback(IntPtr sender, IntPtr evt, IntPtr userData);

    private readonly Playback.Playback playback;
    private readonly IRecentFiles recentFiles;
    private readonly ITrackPreferences trackPreferences;
    private readonly Services.IFilePicker filePicker;
    private readonly Services.IUrlDownloader urlDownloader;
    private readonly Services.IUrlPrompt urlPrompt;
    private readonly ViewModelMain viewModel;
    private readonly UserConfig userConfig;
    private readonly string configPath;
    private HotkeyMap hotkeys;
    private Gio.SimpleAction? diagnosticAction;
    private readonly Gtk.Scale seekScale;
    private readonly Controls.ChapterScrubber chapterScrubber;
    private readonly Gtk.Label positionLabel;
    private readonly Gtk.Label durationLabel;
    private readonly Gtk.Button playPauseButton;
    private readonly Gtk.Button muteButton;
    private readonly Gtk.Scale volumeScale;
    private readonly Gtk.Box controlsBox;
    private readonly Gtk.Box rootBox;
    private readonly Gtk.Overlay videoOverlay;
    private readonly Gtk.Box noVideoBg;
    private readonly Gtk.PopoverMenuBar menuBar;
    private readonly Controls.PlaylistPanel playlistPanel;
    // Stateful action backing "View → Playlist". Held as a field so the auto-show-on-multi-drop path can flip the action's state in lockstep with playlistPanel.SetVisible — keeps the menu's check glyph honest.
    private Gio.SimpleAction? playlistVisibleAction;
    // Window-title brand string, rolled once at construction. The 1% VomplAyer roll wants to be stable for the session — re-rolling on every media-title update would let it flicker mid-playback.
    private readonly string brand;
    private readonly VideoView? videoView;
    private readonly VideoArea? videoArea;
    private readonly VideoSurface? videoSurface;
    // Secondary (PiP) widget set. All null when PiP is off; populated by EnablePip / torn down by DisablePip. See MainWindow.Pip.cs.
    private VideoArea? secondaryArea;
    private VideoView? secondaryView;
    private VideoSurface? secondarySurface;
    private Playback.Playback? secondaryPlayback;
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
    // Mirror of updatingFromVm but for the volume scale: VM PropertyChanged → SetValue must not bounce back through OnVolumeScaleValueChanged and re-call SetVolume, which would race mpv's echo and produce flicker. Same idea, separate flag so seek-scale settle logic can't accidentally swallow a volume update.
    private bool updatingVolumeFromVm;
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

    public MainWindow(Gtk.Application app, Playback.Playback playback, IRecentFiles recentFiles, ITrackPreferences trackPreferences, UserConfig userConfig, string configPath, string? initialFile)
    {
        if (app == null)
        {
            throw new ArgumentNullException(nameof(app));
        }
        if (playback == null)
        {
            throw new ArgumentNullException(nameof(playback));
        }
        if (recentFiles == null)
        {
            throw new ArgumentNullException(nameof(recentFiles));
        }
        if (trackPreferences == null)
        {
            throw new ArgumentNullException(nameof(trackPreferences));
        }
        if (userConfig == null)
        {
            throw new ArgumentNullException(nameof(userConfig));
        }
        if (string.IsNullOrEmpty(configPath))
        {
            throw new ArgumentException("configPath must be non-empty", nameof(configPath));
        }
        this.playback = playback;
        this.recentFiles = recentFiles;
        this.trackPreferences = trackPreferences;
        this.userConfig = userConfig;
        this.configPath = configPath;
        // Derive the runtime keymap from the config now that we're past gtk_init. Trigger parsing logs and skips bad entries rather than aborting load — a single typo in config.toml shouldn't lock the user out of every other binding.
        this.hotkeys = HotkeyMap.FromTomlForm(userConfig.Hotkeys.ToDictionary(), m => Console.Error.WriteLine($"[vompl] {m}"));

        SetApplication(app);
        brand = Random.Shared.NextDouble() < 0.01 ? "VomplAyer" : "Vomplayer";
        Title = brand;
        SetDefaultSize(1280, 720);
        AddCssClass("vompl-main-window");

        InstallVomplCss();

        this.filePicker = new FilePickerGtk(this);
        // Per-URL cache root sits under our regular XDG-aware cache dir (NOT /tmp — /tmp clears on reboot, which would make the 24h-mtime sweep mostly redundant). The cache class wipes stale entries on Cleanup(); we run that once at startup, and YtDlpDownloader runs it again per download to bound disk for long-running sessions.
        var urlDownloadCache = new UrlDownloadCache(UserDataPaths.UrlDownloadCacheRoot);
        urlDownloadCache.Cleanup(DateTimeOffset.UtcNow, msg => Console.Error.WriteLine($"[vompl] {msg}"));
        this.urlDownloader = new YtDlpDownloader(urlDownloadCache, "yt-dlp");
        this.urlPrompt = new UrlPromptGtk(this);
        viewModel = new ViewModelMain(playback, filePicker, recentFiles, trackPreferences, urlDownloader, urlPrompt);
        viewModel.InitialFile = initialFile;

        Gtk.Widget videoWidget;
        if (WaylandDetect.IsWaylandBackend(GetDisplay()))
        {
            var area = new VideoArea();
            var surface = new VideoSurface(this, area);
            playback.AttachRenderSurface(d => surface.SetMpvDispatcher(d));
            surface.RenderContextReady += OnVideoRenderContextReadyWayland;
            surface.RenderFailed += OnVideoRenderFailed;
            surface.FirstFrameRendered += OnPrimaryFirstFrameRendered;
            videoArea = area;
            videoSurface = surface;
            videoWidget = area;
            // VideoContext (inside the VM) owns the per-instance HDR policy now: it subscribes to playback.SourceHdrChanged in its ctor and to surface.CurrentOutputHdrChanged when AttachHdrSink runs. AttachHdrSink fires from OnVideoRenderContextReadyWayland (post-realize) so SetHdr's pre-stage SDR call lands on a ready surface.
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

        // Freedesktop standard icon names — present in every GTK icon theme (Adwaita, Yaru, Breeze, …). Tooltip carries the textual affordance for accessibility and discoverability since the button is icon-only.
        playPauseButton = Gtk.Button.NewFromIconName("media-playback-start");
        playPauseButton.SetTooltipText("Play");
        playPauseButton.SetSensitive(false);

        seekScale = Gtk.Scale.NewWithRange(Gtk.Orientation.Horizontal, 0.0, 1.0, 0.001);
        seekScale.SetHexpand(true);
        seekScale.SetDrawValue(false);
        seekScale.SetSensitive(false);
        RemoveScaleLongPressGesture(seekScale);
        // ChapterScrubber owns the seekScale's container slot from here on (it appends seekScale into its own vertical Gtk.Box). The scale itself is kept by reference for the existing seek-state-machine wiring (raw legacy event controller, OnValueChanged, etc.) — chapter markers are an additive concern.
        chapterScrubber = new Controls.ChapterScrubber(seekScale);
        chapterScrubber.ChapterClicked += normalized => viewModel.SeekTo(normalized);

        positionLabel = Gtk.Label.New("00:00");
        durationLabel = Gtk.Label.New("00:00");
        // Position can never exceed duration, so sizing the position label to the formatted duration's character count is the tightest slot that won't reflow mid-playback (covers the MM:SS ↔ H:MM:SS flip at the 1h mark). Width is updated when Duration arrives; tabular-nums keeps each digit the same advance regardless of glyph.
        positionLabel.AddCssClass("vompl-time-label");
        durationLabel.AddCssClass("vompl-time-label");
        positionLabel.SetXalign(1.0f);

        muteButton = Gtk.Button.NewFromIconName("audio-volume-high");
        muteButton.SetTooltipText("Mute");
        muteButton.AddCssClass("flat");

        // 0..100 matches the volume-max we pin on mpv at Initialize, so the slider is the authoritative range. Step of 1 gives one-percent granularity for fine drag adjustments — every value-change posts a SetVolume to mpv via the dispatcher (no per-pixel dedup), but the round-trip is cheap and mpv coalesces. Hotkey-driven VolumeUp/Down uses a coarser step (5) deliberately: mouse drag wants finer precision than a key tap. Width-request keeps the widget compact in the controls bar.
        volumeScale = Gtk.Scale.NewWithRange(Gtk.Orientation.Horizontal, 0.0, 100.0, 1.0);
        volumeScale.SetDrawValue(false);
        volumeScale.SetValue(viewModel.Volume);
        volumeScale.SetSizeRequest(100, -1);
        volumeScale.SetTooltipText("Volume");
        RemoveScaleLongPressGesture(volumeScale);

        controlsBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        // Spacing around the bar comes from CSS padding on .vompl-controls-bar / .osd, not from widget margins. Margins sit OUTSIDE the background area — with a transparent window underneath, margins would show desktop through. Padding sits inside the background, so the bar's opaque fill extends to its outer edges. vompl-chrome gives it the theme bg; vompl-controls-bar adds the padding. Split so the fullscreen OSD swap (below) only touches the background/padding pair and leaves vompl-chrome off (OSD has its own semi-transparent fill).
        controlsBox.AddCssClass("vompl-chrome");
        controlsBox.AddCssClass("vompl-controls-bar");
        controlsBox.Append(playPauseButton);
        controlsBox.Append(positionLabel);
        controlsBox.Append(chapterScrubber.Widget);
        controlsBox.Append(durationLabel);
        controlsBox.Append(muteButton);
        controlsBox.Append(volumeScale);

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

        // Diagnostic overlay sits above noVideoBg in stacking order (later AddOverlay = higher). Anchored top-right (Halign=End, Valign=Start) so it never overlaps controlsBox (Valign=End) even when controlsBox is reparented in fullscreen. Constructed before BuildMenuBar to match the file's "assign then build menu" pattern (cf. viewModel at line 106); the menu action closure resolves `this.diagnosticOverlay` lazily at invoke time, so the ordering is stylistic rather than load-bearing. Reads HDR / source-HDR / hwdec from the current target (Selected ?? Primary) — providers re-resolve every refresh so a selection swap propagates within the next 1 Hz tick.
        diagnosticOverlay = new DiagnosticOverlay(() => viewModel.SingleTarget, () => GetTargetVideoSurfaceForDiagnostic());
        videoOverlay.AddOverlay(diagnosticOverlay.Widget);

        // Playlist panel sits to the right of the video. Hidden by default; toggled by "View → Playlist" or auto-shown when a multi-file drop populates the playlist (so first-time users see the result of their drop without hunting through menus). Wholesale-rebuild on Playlist.Changed.
        playlistPanel = new Controls.PlaylistPanel(viewModel.Playlist, viewModel.PlayPlaylistItem);
        playlistPanel.Widget.SetVisible(false);

        // Wrap video + playlist in a horizontal row so they share the middle layout slot. videoOverlay still hexpand/vexpand so the video region grows to fill remaining space when the panel is visible.
        var videoAndPlaylistRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        videoAndPlaylistRow.Append(videoOverlay);
        videoAndPlaylistRow.Append(playlistPanel.Widget);
        videoAndPlaylistRow.SetHexpand(true);
        videoAndPlaylistRow.SetVexpand(true);

        menuBar = BuildMenuBar(app);

        // Stream-selector toolbar — built before SetChild so it has its rootBox slot reserved. Hidden by default; visibility flips on IsPipEnabled via UpdateStreamSelectorVisibility. Sits between menubar and video region in windowed mode (a solid layout row that displaces the video area, mirroring controlsBox in windowed mode).
        BuildStreamSelectorToolbar();

        rootBox = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        rootBox.Append(menuBar);
        rootBox.Append(streamSelectorToolbar!);
        rootBox.Append(videoAndPlaylistRow);
        rootBox.Append(controlsBox);
        SetChild(rootBox);

        playPauseButton.OnClicked += (_, _) => viewModel.PlayPauseCommand.Execute(null);
        muteButton.OnClicked += (_, _) => viewModel.ToggleMute();
        volumeScale.OnValueChanged += OnVolumeScaleValueChanged;

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

        // Click on the video widget routes through the HotkeyMap (default: MouseDoubleClick1 → ToggleFullscreen) AND, on single-press (NPress=1), snaps the active slot to this widget — so when PiP is on, clicking either video focuses it. Button=0 means the gesture fires for any button; OnPressed reads the actual button via GetCurrentButton(). GestureClick delivers `pressed` for each press in a sequence with NPress incrementing — so a single-click action also fires once on the first press of a double-click, an inherent property the user has to live with.
        AttachClickToFocus(videoWidget, ViewModelMain.VideoSlot.Primary);

        // Per-widget drop target on the primary video region. Always routes to Primary regardless of active slot — so a drop on the main video area can't accidentally land in Secondary just because Secondary is currently active. Window-level drop (registered below) is the active-aware fallback for chrome / margin drops.
        var primaryDrop = Gtk.DropTarget.New(Gdk.FileList.GetGType(), Gdk.DragAction.Copy | Gdk.DragAction.Move | Gdk.DragAction.Link);
        primaryDrop.OnDrop += OnPrimaryFileDrop;
        videoWidget.AddController(primaryDrop);

        // Drag-and-drop loading. Register a Gdk.FileList drop target — both single- and multi-file drags deserialize into a 1-or-N element FileList from any file-manager / browser source that offers text/uri-list (universal). GirCore 0.7.0 doesn't bind GdkFileList.GetFiles, so we walk the underlying GSList ourselves via P/Invoke (see GdkFileListGetFiles + ExtractAndExpandPaths). Window-level target REPLACES the playlist; the panel-level target wired below APPENDS. GTK4's drop dispatch picks the topmost widget under the pointer that matches the offered formats, so a drop on the panel triggers ONLY the panel's target — replace and append are properly disjoint without a propagation dance. Copy|Move|Link is accepted because Wayland/X11 sources negotiate the action set with the destination; we read the file either way.
        var dropTarget = Gtk.DropTarget.New(Gdk.FileList.GetGType(), Gdk.DragAction.Copy | Gdk.DragAction.Move | Gdk.DragAction.Link);
        dropTarget.OnDrop += OnWindowFileDrop;
        AddController(dropTarget);

        // Panel-level append target. Same FileList GType, attached to the panel's drop area (the inner ListBox, so drops on the scrollbar don't accidentally consume). Only matched when the user drops onto the panel itself.
        var panelDropTarget = Gtk.DropTarget.New(Gdk.FileList.GetGType(), Gdk.DragAction.Copy | Gdk.DragAction.Move | Gdk.DragAction.Link);
        panelDropTarget.OnDrop += OnPanelFileDrop;
        playlistPanel.DropArea.AddController(panelDropTarget);

        // Mirror the real fullscreen state rather than treating a local bool as authority. Covers compositor/WM-initiated un-fullscreen that bypasses our key/gesture paths.
        OnNotify += OnWindowNotify;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.PropertyChanged += OnViewModelPipPropertyChanged;
        playback.PropertyChanged += OnPlaybackPropertyChangedForSeekSettle;
        playback.PropertyChanged += OnPlaybackPropertyChangedForScreensaver;
        OnCloseRequest += OnWindowCloseRequest;

        RefreshMuteButton();
    }

    // VM-pushed updates set `updatingVolumeFromVm` to short-circuit the round-trip. User-driven changes (drag, click, scroll wheel) push straight into the VM, which routes to mpv; mpv's echo lands on the next OnViewModelPropertyChanged → and is suppressed if the value matches the scale's own (the ObservableProperty same-value gate covers the equal-value case, but a tiny float drift could re-fire — accepted, the only effect is a redundant SetVolume).
    private void OnVolumeScaleValueChanged(Gtk.Range sender, EventArgs e)
    {
        if (updatingVolumeFromVm)
        {
            return;
        }
        viewModel.SetVolume(volumeScale.GetValue());
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
                string formattedDuration = TimeFormatter.Format(viewModel.Duration.TotalSeconds);
                durationLabel.SetLabel(formattedDuration);
                positionLabel.SetWidthChars(formattedDuration.Length);
                positionLabel.SetMaxWidthChars(formattedDuration.Length);
                bool hasMedia = viewModel.Duration > TimeSpan.Zero;
                seekScale.SetSensitive(hasMedia);
                playPauseButton.SetSensitive(hasMedia);
                // Re-push to the scrubber so its in-trough mark normalization (`time/duration`) updates whenever duration arrives or changes — covers both file-load (chapters fire before duration on some containers) and the rare same-file duration update.
                chapterScrubber.SetChapters(viewModel.Chapters, viewModel.Duration.TotalSeconds);
                break;
            case nameof(ViewModelMain.Chapters):
                chapterScrubber.SetChapters(viewModel.Chapters, viewModel.Duration.TotalSeconds);
                break;
            case nameof(ViewModelMain.IsPaused):
                playPauseButton.SetIconName(viewModel.IsPaused ? "media-playback-start" : "media-playback-pause");
                playPauseButton.SetTooltipText(viewModel.IsPaused ? "Play" : "Pause");
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
            case nameof(ViewModelMain.VideoTracks):
            case nameof(ViewModelMain.CurrentVideoId):
                RebuildVideoMenu();
                break;
            case nameof(ViewModelMain.AudioTracks):
            case nameof(ViewModelMain.CurrentAudioId):
                RebuildAudioMenu();
                break;
            case nameof(ViewModelMain.SubtitleTracks):
            case nameof(ViewModelMain.CurrentSubtitleId):
                // Each pair of notifications collapses into the same wholesale rebuild for its kind — the submenu reflects the cross-product of (current track list, current selection), so either changing means the visible items / radio glyph need to refresh.
                RebuildSubtitleMenu();
                break;
            case nameof(ViewModelMain.Volume):
                updatingVolumeFromVm = true;
                volumeScale.SetValue(viewModel.Volume);
                updatingVolumeFromVm = false;
                RefreshMuteButton();
                break;
            case nameof(ViewModelMain.IsMuted):
                RefreshMuteButton();
                break;
            case nameof(ViewModelMain.MediaTitle):
                Title = string.IsNullOrEmpty(viewModel.MediaTitle) ? brand : $"{viewModel.MediaTitle} — {brand}";
                break;
        }
    }

    // Pick the freedesktop volume icon. The button is purely a mute toggle, so the icon reflects mute state alone — at vol=0-but-not-muted the unmuted-low icon stays so a user clicking the button gets a Mute/Unmute toggle (not a confusing "icon says muted but click toggles to muted"). Three non-mute steps follow the GNOME / Plasma icon-theme convention: <34 = low, 34–66 = medium, 67+ = high.
    private void RefreshMuteButton()
    {
        string icon;
        if (viewModel.IsMuted)
        {
            icon = "audio-volume-muted";
        }
        else if (viewModel.Volume < 34)
        {
            icon = "audio-volume-low";
        }
        else if (viewModel.Volume < 67)
        {
            icon = "audio-volume-medium";
        }
        else
        {
            icon = "audio-volume-high";
        }
        muteButton.SetIconName(icon);
        muteButton.SetTooltipText(viewModel.IsMuted ? "Unmute" : "Mute");
    }

    // Wayland path: hand the IHdrSink to the per-context HDR policy so it can pre-stage the SDR image description (synchronously, before any frame renders), subscribe to output-HDR transitions, and run an initial ApplyHdrPolicy. AttachHdrSink encapsulates that sequence — see its docstring for the three pre-stage edge cases it covers.
    private void OnVideoRenderContextReadyWayland()
    {
        if (videoSurface != null)
        {
            viewModel.Primary.AttachHdrSink(videoSurface);
        }
        viewModel.OnRenderContextReady();
    }

    // Latches true on the primary's first rendered frame; never reset (the primary VideoSurface lives for the window's lifetime). Read by OnSecondaryFirstFrameRendered to decide whether to restack the secondary above the parent — see that method for the full rationale.
    private bool primaryFirstFrameRendered;

    private void OnPrimaryFirstFrameRendered()
    {
        primaryFirstFrameRendered = true;
        noVideoBg.SetVisible(false);
        // Restore the secondary's normal "above primary, below parent" stacking. Idempotent in the common case where it was never restacked above the parent (one extra wl_subsurface_place_above + commit; harmless). Hide noVideoBg first so the secondary stays continuously visible during the restack: the alternative order would briefly leave the secondary below an opaque-black noVideoBg between the place_above and the SetVisible.
        if (secondarySurface != null && videoSurface != null)
        {
            secondarySurface.PlaceAbove(videoSurface);
        }
    }

    // GLArea path: always SDR. The old main-surface HDR attach produced a blown-out UI (GTK widgets render sRGB values into a surface KWin interprets as PQ) and can't be toggled per-file without destroying the GTK surface, so this path is SDR-only; HDR content is tonemapped by mpv's auto targeting.
    private void OnVideoRenderContextReadyGLArea()
    {
        viewModel.OnRenderContextReady();
    }

    private void OnVideoRenderFailed(int code)
    {
        Console.Error.WriteLine($"[vomplayer] mpv render failed with code {code}; video rendering stopped.");
    }

    // The main window's own CSS background must be transparent so the video subsurface (placed below the parent wl_surface) shows through wherever no widget paints opaque. GTK4's render tree is parent-first, child-on-top with alpha compositing — a child's transparent background can't punch a hole through an opaque parent, so the only way to get alpha=0 anywhere is for the window itself not to paint. Keep the rule scoped by the vompl-main-window class so About/file/other dialogs (also Gtk.Window) aren't dragged along. In practice the rendered transparent area is exactly the video widget region, since every other chrome widget opts in to opaque via the vompl-chrome class (menu bar, controls bar in windowed mode). Priority APPLICATION (600) beats theme defaults.
    private static void InstallVomplCss()
    {
        var provider = Gtk.CssProvider.New();
        provider.LoadFromString("window.vompl-main-window { background: transparent; } .vompl-chrome { background-color: @theme_bg_color; } .vompl-controls-bar { padding: 6px; } .osd { padding: 6px; } .vompl-no-video-bg { background-color: black; } .vompl-diagnostic { background-color: rgba(0,0,0,0.55); color: #e0e0e0; padding: 8px 10px; margin: 8px; border-radius: 6px; font-family: monospace; font-size: 10pt; } .vompl-time-label { font-variant-numeric: tabular-nums; } .vompl-playlist-panel { border-left: 1px solid @borders; } .vompl-playlist-list row.vompl-playlist-current:not(:selected) { background-color: rgba(53, 132, 228, 0.25); } .vompl-playlist-list row.vompl-playlist-current label { font-weight: bold; } .vompl-selected-video { box-shadow: inset 0 0 0 2px rgba(255,255,255,0.9); } .vompl-stream-toolbar { padding: 4px 6px; }");
        Gtk.StyleContext.AddProviderForDisplay(Gdk.Display.GetDefault()!, provider, (uint)Gtk.Constants.STYLE_PROVIDER_PRIORITY_APPLICATION);
    }

    // Single keyboard dispatch path. The capture-phase EventControllerKey runs before any focused-child controller. Trigger.MakeKey canonicalizes the keyval (lowercase) and masks the modifier state to the GTK default-mod-mask, so CapsLock-on `f` and Shift+f match their bound forms regardless of whether the binding was authored as "f" or "<Shift>F". ExecuteAction returns true to consume the key, false to let it propagate (used for ExitFullscreen-when-not-fullscreen, so Escape remains available to dialogs/popovers we host in the future).
    private bool OnWindowKeyPressed(Gtk.EventControllerKey sender, Gtk.EventControllerKey.KeyPressedSignalArgs args)
    {
        // In fullscreen, treat any key press as activity: un-hide controls + stream-selector toolbar (if currently hidden) and re-arm the auto-hide timer. Without this, a user keyboard-cueing PiP without mouse motion would silently lose the toolbar selection after 2s of mouse idleness — the auto-hide reset would clear SelectedSlot mid-sequence. Mouse motion already arms the timer (OnWindowPointerMotion); this extends the same affordance to keyboard input.
        if (isFullscreen)
        {
            if (!controlsBox.GetVisible())
            {
                controlsBox.SetVisible(true);
            }
            if (streamSelectorToolbar != null && viewModel.IsPipEnabled && !streamSelectorToolbar.GetVisible())
            {
                streamSelectorToolbar.SetVisible(true);
            }
            SetCursorFromName(null);
            ArmControlsHideTimer();
        }
        var trigger = Trigger.MakeKey(args.Keyval, args.State);
        var action = hotkeys.Lookup(trigger);
        if (action == null)
        {
            return false;
        }
        return ExecuteAction(action.Value);
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
        // Restore the top stream-selector toolbar alongside the bottom controls when the user moves the mouse. Visibility is gated by IsPipEnabled — a single-stream session never shows the toolbar.
        if (streamSelectorToolbar != null && viewModel.IsPipEnabled && !streamSelectorToolbar.GetVisible())
        {
            streamSelectorToolbar.SetVisible(true);
        }
        SetCursorFromName(null);
        ArmControlsHideTimer();
    }

    private bool OnWindowFileDrop(Gtk.DropTarget sender, Gtk.DropTarget.DropSignalArgs args)
    {
        var paths = ExtractAndExpandPaths(args.Value);
        if (paths.Count == 0)
        {
            // Empty payload (typically: a directory drop that had no recognized video files inside). Don't orphan currently-playing media. Return false so the drag source sees the drop as rejected; per CLAUDE.md (silent error handling banned) log when the input itself was malformed (no files at all in the FileList) but stay quiet for the legitimate zero-match case.
            return false;
        }
        viewModel.LoadPaths(paths, replace: true);
        // Auto-show the panel for multi-item playlists so first-time users see the result of their drop. Single-file drops don't reveal — the user already sees their file playing.
        if (viewModel.Playlist.Items.Count >= 2)
        {
            ShowPlaylistPanel();
        }
        return true;
    }

    private bool OnPanelFileDrop(Gtk.DropTarget sender, Gtk.DropTarget.DropSignalArgs args)
    {
        var paths = ExtractAndExpandPaths(args.Value);
        if (paths.Count == 0)
        {
            return false;
        }
        viewModel.LoadPaths(paths, replace: false);
        return true;
    }

    // Pulls a flat list of paths (or URIs for non-local sources) out of a Gdk.FileList GValue, then expands any local-directory entries to their recursive video-file contents (extension-filtered). Direct file entries are kept as-is — the user's explicit drop is authoritative for "is this playable?", we don't second-guess by extension. URIs (where g_file_get_path returned NULL and we fell back to g_file_get_uri) skip the directory-expansion check entirely.
    //
    // GdkFileList is a boxed type, so args.Value.GetBoxed() returns the IntPtr to the boxed payload (NOT GetObject(), which is GObject-only and would return null here). gdk_file_list_get_files returns (transfer container) — the GSList container is caller-owned (g_slist_free), the GFile elements are owned by the FileList.
    private List<string> ExtractAndExpandPaths(GObject.Value value)
    {
        var raw = new List<string>();
        IntPtr fileListPtr = value.GetBoxed();
        if (fileListPtr == IntPtr.Zero)
        {
            Console.Error.WriteLine("[vompl] dnd: dropped value's boxed payload was null; ignoring");
            return new List<string>();
        }
        IntPtr gslist = GdkFileListGetFiles(fileListPtr);
        IntPtr node = gslist;
        while (node != IntPtr.Zero)
        {
            IntPtr gfile = Marshal.ReadIntPtr(node, 0);                    // GSList.data
            if (gfile != IntPtr.Zero)
            {
                IntPtr pathPtr = GFileGetPath(gfile);
                if (pathPtr != IntPtr.Zero)
                {
                    var p = Marshal.PtrToStringUTF8(pathPtr);
                    GFree(pathPtr);
                    if (!string.IsNullOrEmpty(p))
                    {
                        raw.Add(p);
                    }
                }
                else
                {
                    IntPtr uriPtr = GFileGetUri(gfile);
                    if (uriPtr != IntPtr.Zero)
                    {
                        var u = Marshal.PtrToStringUTF8(uriPtr);
                        GFree(uriPtr);
                        if (!string.IsNullOrEmpty(u))
                        {
                            raw.Add(u);
                        }
                    }
                }
            }
            node = Marshal.ReadIntPtr(node, IntPtr.Size);                  // GSList.next
        }
        GSListFree(gslist);

        return MediaExtensions.ExpandPaths(raw, msg => Console.Error.WriteLine($"[vompl] dnd: {msg}"));
    }

    // Single dispatch site for HotkeyMap-bound actions. Returns true when the input has been consumed so the key controller can short-circuit propagation; the click handler ignores the return value because GestureClick doesn't propagate the same way. ExitFullscreen returns false when not actually fullscreen so the bound key (typically Escape) doesn't get silently swallowed in non-fullscreen state — matches the pre-customization behavior.
    private bool ExecuteAction(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.Open:
                viewModel.OpenCommand.Execute(null);
                return true;
            case HotkeyAction.OpenUrl:
                viewModel.OpenUrlCommand.Execute(null);
                return true;
            case HotkeyAction.Quit:
                Close();
                return true;
            case HotkeyAction.PlayPause:
                viewModel.PlayPauseCommand.Execute(null);
                return true;
            case HotkeyAction.ToggleFullscreen:
                SetFullscreen(!isFullscreen);
                return true;
            case HotkeyAction.ExitFullscreen:
                if (isFullscreen)
                {
                    SetFullscreen(false);
                    return true;
                }
                return false;
            case HotkeyAction.ToggleDiagnosticOverlay:
                ToggleDiagnosticOverlay();
                return true;
            case HotkeyAction.ShowPreferences:
                ShowHotkeysDialog();
                return true;
            case HotkeyAction.SeekBack5:
                viewModel.SeekRelative(-5);
                return true;
            case HotkeyAction.SeekForward5:
                viewModel.SeekRelative(5);
                return true;
            case HotkeyAction.SeekBack10:
                viewModel.SeekRelative(-10);
                return true;
            case HotkeyAction.SeekForward10:
                viewModel.SeekRelative(10);
                return true;
            case HotkeyAction.FrameStepBack:
                viewModel.StepFrameBack();
                return true;
            case HotkeyAction.FrameStepForward:
                viewModel.StepFrameForward();
                return true;
            case HotkeyAction.SeekStart:
                viewModel.SeekTo(0);
                return true;
            case HotkeyAction.SeekEnd:
                viewModel.SeekTo(1);
                return true;
            case HotkeyAction.ChapterPrev:
                viewModel.StepChapter(-1);
                return true;
            case HotkeyAction.ChapterNext:
                viewModel.StepChapter(1);
                return true;
            case HotkeyAction.VolumeUp:
                viewModel.AdjustVolume(VolumeStepPercent);
                return true;
            case HotkeyAction.VolumeDown:
                viewModel.AdjustVolume(-VolumeStepPercent);
                return true;
            case HotkeyAction.ToggleMute:
                viewModel.ToggleMute();
                return true;
            default:
                return false;
        }
    }

    // Percent points per VolumeUp / VolumeDown press. 5 matches YouTube's keyboard step and lines up with the SeekBack5/Forward5 cadence.
    private const double VolumeStepPercent = 5;

    // Auto-show entry from OnWindowFileDrop. Flips the menu action's state so the check glyph stays in sync with the panel's actual visibility — the action's OnChangeState handler is responsible for the SetVisible call. Idempotent: if the panel is already visible, the same-state ChangeState is a no-op (GirCore's SetState dedups on equality).
    private void ShowPlaylistPanel()
    {
        if (playlistVisibleAction == null)
        {
            // Defensive: pre-BuildMenuBar callers shouldn't exist (the only caller is OnWindowFileDrop which fires post-Present), but log loud rather than silently NRE.
            Console.Error.WriteLine("[vompl] playlist: ShowPlaylistPanel before menu was built");
            return;
        }
        playlistVisibleAction.ChangeState(GLib.Variant.NewBoolean(true));
    }

    // Shared between the menu's stateful action and the hotkey-bound action. Reads the action's current state and toggles via ChangeState so the menu's check glyph stays in sync regardless of which path triggered the toggle. Throws if BuildMenuBar hasn't run yet — control flow today guarantees menu construction precedes the controllers that can fire ExecuteAction, but a future refactor that breaks that ordering should fail loud rather than make a hotkey silently no-op (CLAUDE.md bans silent error handling).
    private void ToggleDiagnosticOverlay()
    {
        if (diagnosticAction == null)
        {
            throw new InvalidOperationException("ToggleDiagnosticOverlay invoked before BuildMenuBar registered diagnosticAction");
        }
        var state = diagnosticAction.GetState();
        bool current = state != null && state.GetBoolean();
        diagnosticAction.ChangeState(GLib.Variant.NewBoolean(!current));
    }

    // Reload the runtime keymap from the dialog's edits, persist to disk, and refresh the menu accelerator labels. Called by HotkeysDialog when the user clicks Save. Failure to persist is logged but not fatal — the in-memory map is already updated, so the new bindings are live; the user can retry by saving again.
    internal void ApplyHotkeys(HotkeyMap map)
    {
        hotkeys = map;
        userConfig.Hotkeys = UserConfig.HotkeysSection.FromDictionary(map.ToTomlForm());
        try
        {
            userConfig.Save(configPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vompl] hotkeys: failed to save config to {configPath}: {ex.Message}");
        }
        RefreshMenuAccels();
    }

    internal HotkeyMap GetHotkeysSnapshot()
    {
        return hotkeys.Clone();
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

    // Snapshot of the playlist panel's visibility at fullscreen-enter, restored on fullscreen-exit. Hiding the panel in fullscreen prevents it from squeezing the video region; restoring on exit means a user who had it visible in windowed mode doesn't have to re-toggle every time.
    private bool playlistPanelVisibleBeforeFullscreen;

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
            // Snapshot the panel's pre-fullscreen visibility on entry only — re-entering fullscreen while already fullscreen would otherwise lose the original windowed-mode state.
            playlistPanelVisibleBeforeFullscreen = playlistPanel.Widget.GetVisible();
            playlistPanel.Widget.SetVisible(false);
        }
        else
        {
            playlistPanel.Widget.SetVisible(playlistPanelVisibleBeforeFullscreen);
        }
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
            ReparentStreamSelectorForFullscreen(true);
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
            ReparentStreamSelectorForFullscreen(false);
        }
    }

    // Reparent the stream-selector toolbar between rootBox (windowed: a solid layout row that displaces the video) and videoOverlay (fullscreen: floats at the top of the video). Same pattern as controlsBox below, but anchored at top. The toolbar's visibility is governed independently by IsPipEnabled, so this method only handles the parent + alignment + CSS swap.
    private void ReparentStreamSelectorForFullscreen(bool fullscreen)
    {
        if (streamSelectorToolbar == null)
        {
            return;
        }
        if (fullscreen)
        {
            if (streamSelectorToolbar.Parent == rootBox)
            {
                rootBox.Remove(streamSelectorToolbar);
            }
            streamSelectorToolbar.SetValign(Gtk.Align.Start);
            streamSelectorToolbar.SetHalign(Gtk.Align.Start);
            streamSelectorToolbar.RemoveCssClass("vompl-chrome");
            streamSelectorToolbar.AddCssClass("osd");
            if (streamSelectorToolbar.Parent != videoOverlay)
            {
                videoOverlay.AddOverlay(streamSelectorToolbar);
            }
        }
        else
        {
            if (streamSelectorToolbar.Parent == videoOverlay)
            {
                videoOverlay.RemoveOverlay(streamSelectorToolbar);
            }
            streamSelectorToolbar.RemoveCssClass("osd");
            streamSelectorToolbar.AddCssClass("vompl-chrome");
            streamSelectorToolbar.SetValign(Gtk.Align.Fill);
            streamSelectorToolbar.SetHalign(Gtk.Align.Fill);
            if (streamSelectorToolbar.Parent != rootBox)
            {
                // Re-insert at the slot the original BuildStreamSelectorToolbar placed it: between menubar (index 0) and videoAndPlaylistRow (index 1 in the original layout). InsertChildAfter places it after menubar.
                rootBox.InsertChildAfter(streamSelectorToolbar, menuBar);
            }
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
            // Top-of-screen stream-selector hides with the bottom controls. On hide, additionally reset SelectedSlot to null so the user's "I stopped touching it" gesture restores broadcast/sync mode. The reset is fullscreen-specific by design: in windowed mode the toolbar is always visible and the selection persists until the user explicitly clicks again.
            if (streamSelectorToolbar != null && viewModel.IsPipEnabled)
            {
                streamSelectorToolbar.SetVisible(false);
            }
            if (viewModel.SelectedSlot != null)
            {
                viewModel.SetSelected(null);
            }
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
        viewModel.PropertyChanged -= OnViewModelPipPropertyChanged;
        viewModel.Primary.PropertyChanged -= OnPrimaryContextPropertyChangedForToolbar;
        // VideoContext (via viewModel.Dispose below) handles its own playback.SourceHdrChanged unsubscribe and DetachHdrSink. MainWindow no longer touches HDR plumbing.
        // If PiP is on, tear it down so the secondary Playback/Surface are disposed under our control before the window's GTK widgets go.
        if (viewModel.IsPipEnabled)
        {
            DisablePip();
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
        // Detach the panel's Playlist.Changed subscription before viewModel.Dispose drops the playlist — keeps a late mainloop tick from invoking into a half-torn-down panel.
        playlistPanel.Dispose();
        viewModel.Dispose();
        videoSurface?.Dispose();
        playback.Dispose();
        return false;
    }
}
