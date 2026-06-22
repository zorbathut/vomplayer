using System;
using System.Collections.Generic;
using System.ComponentModel;
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
public sealed partial class MainWindow : Gtk.ApplicationWindow, IPipHost
{
    private readonly Playback.Playback playback;
    private readonly IRecentFiles recentFiles;
    private readonly ISavedPlaylists savedPlaylists;
    private readonly ITrackPreferences trackPreferences;
    private readonly Services.IFilePicker filePicker;
    private readonly Services.IUrlDownloader urlDownloader;
    private readonly Services.IUrlPrompt urlPrompt;
    private readonly ViewModelMain viewModel;
    private readonly UserConfig userConfig;
    private readonly string configPath;
    // GTK's `gtk-application-prefer-dark-theme` value as the desktop reported it at launch, captured before we ever override it. The "Auto" theme mode resolves to this snapshot. See ApplyThemePreference.
    private readonly bool systemPreferDarkDefault;
    private HotkeyMap hotkeys;
    private Gio.SimpleAction? diagnosticAction;
    private readonly SeekScaleController seekScaleController;
    private readonly Controls.ChapterScrubber chapterScrubber;
    private readonly Gtk.Label positionLabel;
    private readonly Gtk.Label durationLabel;
    private readonly Gtk.Button playPauseButton;
    private readonly Gtk.Button prevButton;
    private readonly Gtk.Button nextButton;
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
    private readonly PipController pipController;
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
    // While the cursor is hidden (auto-hide timer fired), require this much straight-line displacement from armPosition — which, since the timer only fires after the pointer has rested within MotionDeadZonePx for the full delay, is where the pointer was sitting when it hid — before un-hiding. Much larger than MotionDeadZonePx so an accidental bump — a brushed mouse, a knocked desk — doesn't pop the cursor and controls back over the video; only a deliberate sweep (or a click) reveals them. Logical px, matching the motion-event coordinate space, so it's scale-independent like the dead zone.
    private const double CursorRevealThresholdPx = 40.0;
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
    // VM-pushed volume update guard: VM PropertyChanged → SetValue must not bounce back through OnVolumeScaleValueChanged and re-call SetVolume, which would race mpv's echo and produce flicker.
    private bool updatingVolumeFromVm;

    // IPipHost surface — exposes the bits PipController needs.
    Gtk.Window IPipHost.Window { get { return this; } }
    Gtk.Overlay IPipHost.VideoOverlay { get { return videoOverlay; } }
    Gtk.Box IPipHost.ControlsBox { get { return controlsBox; } }
    VideoArea? IPipHost.PrimaryArea { get { return videoArea; } }
    VideoView? IPipHost.PrimaryView { get { return videoView; } }
    VideoSurface? IPipHost.PrimarySurface { get { return videoSurface; } }
    Controls.PlaylistPanel IPipHost.PlaylistPanel { get { return playlistPanel; } }
    HotkeyMap IPipHost.Hotkeys { get { return hotkeys; } }
    void IPipHost.ExecuteAction(HotkeyAction action) { ExecuteAction(action); }

    public MainWindow(Gtk.Application app, Playback.Playback playback, IRecentFiles recentFiles, ISavedPlaylists savedPlaylists, ITrackPreferences trackPreferences, UserConfig userConfig, string configPath, string? initialFile)
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
        if (savedPlaylists == null)
        {
            throw new ArgumentNullException(nameof(savedPlaylists));
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
        this.savedPlaylists = savedPlaylists;
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

        // Capture the desktop's prefer-dark value before applying the user's choice — this is the only window, so nothing has overridden it yet — then apply. Done before widgets are shown so the first paint is already the right polarity (no light->dark flash).
        systemPreferDarkDefault = Gtk.Settings.GetDefault()?.GtkApplicationPreferDarkTheme ?? false;
        ApplyThemePreference(ThemeModeParser.Parse(userConfig.Application.Theme, m => Console.Error.WriteLine($"[vompl] {m}")));

        this.filePicker = new FilePickerGtk(this);
        // Per-URL cache root sits under our regular XDG-aware cache dir (NOT /tmp — /tmp clears on reboot, which would make the 24h-mtime sweep mostly redundant). The cache class wipes stale entries on Cleanup(); we run that once at startup, and YtDlpDownloader runs it again per download to bound disk for long-running sessions.
        var urlDownloadCache = new UrlDownloadCache(UserDataPaths.UrlDownloadCacheRoot);
        urlDownloadCache.Cleanup(DateTimeOffset.UtcNow, msg => Console.Error.WriteLine($"[vompl] {msg}"));
        this.urlDownloader = new YtDlpDownloader(urlDownloadCache, YtDlpDownloader.BuildCommand(FlatpakDetect.IsSandboxed()));
        this.urlPrompt = new UrlPromptGtk(this);
        viewModel = new ViewModelMain(playback, filePicker, recentFiles, trackPreferences, urlDownloader, urlPrompt);
        // AttachAutosave before InitialFile is consumed (OnRenderContextReady) so the very first user-visible action — even one driven by the CLI arg — is captured.
        viewModel.AttachAutosave(savedPlaylists);
        viewModel.Autosave!.Saved += RebuildRecentMenu;
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

        // Previous/next-track buttons. Freedesktop "skip" icons (the to-track ⏮/⏭ glyphs, distinct from the "seek" double-arrows); present across Adwaita/Breeze/Yaru. Insensitive until media loads, like playPauseButton — at a true directory edge the command no-ops (we don't scan the directory on every state change just to grey them out).
        prevButton = Gtk.Button.NewFromIconName("media-skip-backward");
        prevButton.SetTooltipText("Previous");
        prevButton.SetSensitive(false);
        nextButton = Gtk.Button.NewFromIconName("media-skip-forward");
        nextButton.SetTooltipText("Next");
        nextButton.SetSensitive(false);

        seekScaleController = new SeekScaleController(playback);
        seekScaleController.SeekRequested += v => viewModel.SeekTo(v);
        // ChapterScrubber wraps the controller's scale in its own vertical Gtk.Box and adds chapter markers on top. The controller still owns the scale's input/state machine; the scrubber is purely additive layout.
        chapterScrubber = new Controls.ChapterScrubber(seekScaleController.Scale);
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
        ScaleHelpers.RemoveLongPressGesture(volumeScale);

        controlsBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        // Spacing around the bar comes from CSS padding on .vompl-controls-bar / .osd, not from widget margins. Margins sit OUTSIDE the background area — with a transparent window underneath, margins would show desktop through. Padding sits inside the background, so the bar's opaque fill extends to its outer edges. vompl-chrome gives it the theme bg; vompl-controls-bar adds the padding. Split so the fullscreen OSD swap (below) only touches the background/padding pair and leaves vompl-chrome off (OSD has its own semi-transparent fill).
        controlsBox.AddCssClass("vompl-chrome");
        controlsBox.AddCssClass("vompl-controls-bar");
        controlsBox.Append(playPauseButton);
        controlsBox.Append(positionLabel);
        controlsBox.Append(chapterScrubber.Widget);
        controlsBox.Append(durationLabel);
        controlsBox.Append(prevButton);
        controlsBox.Append(nextButton);
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

        // Playlist panel sits to the right of the video. Hidden by default; toggled by "View → Playlist" or auto-shown when a multi-file drop populates the playlist (so first-time users see the result of their drop without hunting through menus). Wholesale-rebuild on Playlist.Changed.
        playlistPanel = new Controls.PlaylistPanel(viewModel.Playlist, viewModel.PlayPlaylistItem);
        playlistPanel.Widget.SetVisible(false);

        // PipController must exist before BuildMenuBar so the latter can wire the Add Stream / Delete Stream menu actions through it. Constructed before diagnosticOverlay so the latter's lambda can capture a non-null reference. Toolbar visibility is governed by IsPipEnabled, hidden by default.
        pipController = new PipController(this, viewModel, recentFiles, trackPreferences, filePicker, urlDownloader, urlPrompt);

        // Diagnostic overlay sits above noVideoBg in stacking order (later AddOverlay = higher). Anchored top-right (Halign=End, Valign=Start) so it never overlaps controlsBox (Valign=End) even when controlsBox is reparented in fullscreen. Reads HDR / source-HDR / hwdec from the current target (Selected ?? Primary) — providers re-resolve every refresh so a selection swap propagates within the next 1 Hz tick.
        diagnosticOverlay = new DiagnosticOverlay(() => viewModel.SingleTarget, () => pipController.GetTargetVideoSurfaceForDiagnostic());
        videoOverlay.AddOverlay(diagnosticOverlay.Widget);

        // Wrap video + playlist in a horizontal row so they share the middle layout slot. videoOverlay still hexpand/vexpand so the video region grows to fill remaining space when the panel is visible.
        var videoAndPlaylistRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        videoAndPlaylistRow.Append(videoOverlay);
        videoAndPlaylistRow.Append(playlistPanel.Widget);
        videoAndPlaylistRow.SetHexpand(true);
        videoAndPlaylistRow.SetVexpand(true);

        menuBar = BuildMenuBar(app);

        rootBox = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        rootBox.Append(menuBar);
        // Toolbar sits between menubar and video region in windowed mode (a solid layout row that displaces the video area, mirroring controlsBox in windowed mode). Reparented to videoOverlay in fullscreen.
        rootBox.Append(pipController.Toolbar);
        rootBox.Append(videoAndPlaylistRow);
        rootBox.Append(controlsBox);
        SetChild(rootBox);

        playPauseButton.OnClicked += (_, _) => viewModel.PlayPauseCommand.Execute(null);
        prevButton.OnClicked += (_, _) => viewModel.PreviousTrack();
        nextButton.OnClicked += (_, _) => viewModel.NextTrack();
        muteButton.OnClicked += (_, _) => viewModel.ToggleMute();
        volumeScale.OnValueChanged += OnVolumeScaleValueChanged;

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
        var primaryDrop = UriListDropTarget.Create(paths => HandlePrimaryDrop(paths));
        videoWidget.AddController(primaryDrop);

        // Drag-and-drop loading. We use Gtk.DropTargetAsync via UriListDropTarget rather than the older Gtk.DropTarget(Gdk.FileList) path because GTK 4's content-deserializer machinery for FileList drops in a Flatpak sandbox always tries to mediate via the FileTransfer/Documents portals — which hard-rejects paths outside the trusted-roots list (e.g. Steam Deck /var/mnt/* drops). UriListDropTarget calls gdk_drop_read_async with explicit mime list ["text/uri-list"], bypassing the deserializer machinery entirely and the portal call with it. With --filesystem=host:ro in the manifest, the sandbox already has read access to whatever the source dropped. See UriListDropTarget.cs's header for full details. Window-level target REPLACES the playlist; the panel-level target wired below APPENDS. GTK4's drop dispatch picks the topmost widget under the pointer that matches the offered formats, so a drop on the panel triggers ONLY the panel's target — replace and append are properly disjoint without a propagation dance. Copy|Move|Link is accepted because Wayland/X11 sources negotiate the action set with the destination; we read the file either way.
        var dropTarget = UriListDropTarget.Create(paths => HandleWindowDrop(paths));
        AddController(dropTarget);

        // Panel-level append target, attached to the panel's drop area (the inner ListBox, so drops on the scrollbar don't accidentally consume). Only matched when the user drops onto the panel itself.
        var panelDropTarget = UriListDropTarget.Create(paths => HandlePanelDrop(paths));
        playlistPanel.DropArea.AddController(panelDropTarget);

        // Mirror the real fullscreen state rather than treating a local bool as authority. Covers compositor/WM-initiated un-fullscreen that bypasses our key/gesture paths.
        OnNotify += OnWindowNotify;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
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
                seekScaleController.SetSensitive(hasMedia);
                playPauseButton.SetSensitive(hasMedia);
                prevButton.SetSensitive(hasMedia);
                nextButton.SetSensitive(hasMedia);
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
                seekScaleController.SetVmSeekValue(viewModel.SeekValue);
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

    // Wayland path: hand the IHdrSink to the per-context HDR policy so it can pre-stage the SDR image description (synchronously, before any frame renders), subscribe to output-HDR transitions, and run an initial ApplyHdrPolicy. AttachHdrSink encapsulates that sequence — see its docstring for the three pre-stage edge cases it covers. Same surface implements IVrrSink for the per-output VRR window, so attach it on the same boundary; AttachVrrSink runs an initial ApplyVrrPolicy once both sink and source FPS are known.
    private void OnVideoRenderContextReadyWayland()
    {
        if (videoSurface != null)
        {
            viewModel.Primary.AttachHdrSink(videoSurface);
            viewModel.Primary.AttachVrrSink(videoSurface);
        }
        viewModel.OnRenderContextReady();
    }

    private void OnPrimaryFirstFrameRendered()
    {
        noVideoBg.SetVisible(false);
        // PipController latches the bit and, if it had previously re-stacked the secondary above the parent (because secondary rendered before primary), restores it to its normal "above primary, below parent" stacking. Hide noVideoBg first so the secondary stays continuously visible during the restack: the alternative order would briefly leave the secondary below an opaque-black noVideoBg.
        pipController.NotifyPrimaryFirstFrameRendered();
    }

    // Click on the primary video widget. Pure HotkeyMap dispatch — single-click → PlayPause, double-click → ToggleFullscreen. Fires on press for immediate response; the primary has no drag-to-move so there's no conflict with sub-threshold motion (PipController's body click fires on release to defer to its drag gesture).
    private void AttachClickToFocus(Gtk.Widget widget, ViewModelMain.VideoSlot slot)
    {
        var clickGesture = Gtk.GestureClick.New();
        clickGesture.Button = 0;
        clickGesture.OnPressed += (sender, args) => HandleVideoClick(slot, sender, args);
        widget.AddController(clickGesture);
    }

    private void HandleVideoClick(ViewModelMain.VideoSlot slot, Gtk.GestureClick sender, Gtk.GestureClick.PressedSignalArgs args)
    {
        uint button = sender.GetCurrentButton();
        if (button == 0)
        {
            return;
        }
        // A click is activity: reveal the hidden cursor/controls even if the cursor was sticky-hidden through small movements, and regardless of whether the button is bound to an action.
        NotifyFullscreenActivity();
        var trigger = new Trigger.MouseClick(button, args.NPress);
        var action = hotkeys.Lookup(trigger);
        if (action != null)
        {
            ExecuteAction(action.Value);
        }
    }

    private void HandlePrimaryDrop(List<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }
        // Drop on the primary video area always targets Primary, regardless of active slot. (When PiP is off this is identical to the window-level drop's target.)
        viewModel.Primary.LoadPaths(paths, replace: true);
        if (viewModel.Primary.Playlist.Items.Count >= 2)
        {
            ShowPlaylistPanel();
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
        NotifyFullscreenActivity();
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
        // Hidden-ness is derived from controlsBox visibility (the auto-hide timer sets both invisible together, and every reveal path sets both visible together) — no separate flag to keep in sync. While hidden, motion must exceed CursorRevealThresholdPx; while visible, only the sub-pixel MotionDeadZonePx jitter filter applies.
        bool cursorHidden = !controlsBox.GetVisible();
        if (double.IsNaN(armPositionX))
        {
            // No reference yet — armPosition is reset to NaN on every fullscreen transition. Adopt this event's position as the origin. If the cursor is already hidden (entered fullscreen and idled out without ever moving), don't let the origin-setting event itself reveal: a real displacement from here must accumulate first, otherwise the very first micro-motion would defeat the sticky-cursor behavior.
            armPositionX = x;
            armPositionY = y;
            if (cursorHidden)
            {
                FsLog($"motion #{motionEventCount} x={x:F1} y={y:F1} (hidden, origin adopted, no reveal)");
                return;
            }
            NotifyFullscreenActivity();
            return;
        }
        double dx = x - armPositionX;
        double dy = y - armPositionY;
        if (!CursorRevealPolicy.IsSignificantMotion(dx, dy, cursorHidden, MotionDeadZonePx, CursorRevealThresholdPx))
        {
            if (motionEventCount % 30 == 1)
            {
                FsLog($"motion #{motionEventCount} x={x:F1} y={y:F1} (sub-threshold, {(cursorHidden ? "below reveal threshold" : "dead zone")}, ignored)");
            }
            return;
        }
        armPositionX = x;
        armPositionY = y;
        if (motionEventCount % 10 == 1)
        {
            FsLog($"motion #{motionEventCount} x={x:F1} y={y:F1} (accepted, {(cursorHidden ? "reveal" : "dead-zone exceeded")})");
        }
        NotifyFullscreenActivity();
    }

    // Single reveal entry point for fullscreen "user is active" signals: pointer motion past the regime threshold, key press, and clicks on either video. Un-hides the bottom controls and the top stream-selector toolbar (the latter gated by IsPipEnabled — a single-stream session never shows it), restores the cursor, and re-arms the auto-hide timer. No-op outside fullscreen so callers needn't guard.
    public void NotifyFullscreenActivity()
    {
        if (!isFullscreen)
        {
            return;
        }
        if (!controlsBox.GetVisible())
        {
            controlsBox.SetVisible(true);
        }
        if (viewModel.IsPipEnabled && !pipController.Toolbar.GetVisible())
        {
            pipController.Toolbar.SetVisible(true);
        }
        SetCursorFromName(null);
        ArmControlsHideTimer();
    }

    // Recent-menu click handler. Lifecycle dance lives here (in MainWindow) rather than in ViewModelMain.LoadFromSaved because PiP enable/disable involves constructing/disposing the secondary Playback + widget set, which is GTK code MainWindow already owns. The PiP toggle is driven from the saved entry's StreamCount: multi-stream → enable PiP; single-stream → disable PiP. After the toggle, hand off to viewModel.LoadFromSaved which does the actual playlist restoration paused at resume.
    internal void OpenSavedPlaylist(Guid guid)
    {
        var entry = savedPlaylists.GetById(guid);
        if (entry == null)
        {
            // Either the GUID was stale (concurrent deletion — not currently possible in v1) or the menu got out of sync. Either way nothing to load.
            return;
        }
        bool savedIsPip = entry.StreamCount > 1;
        if (savedIsPip && !viewModel.IsPipEnabled)
        {
            pipController.Enable();
        }
        else if (!savedIsPip && viewModel.IsPipEnabled)
        {
            pipController.Disable();
        }
        viewModel.LoadFromSaved(guid, startPaused: true);
    }

    private void HandleWindowDrop(List<string> paths)
    {
        if (paths.Count == 0)
        {
            // Empty payload (typically: a directory drop that had no recognized video files inside). Don't orphan currently-playing media — UriListDropTarget already called drop.Finish, we just ignore.
            return;
        }
        viewModel.LoadPaths(paths, replace: true);
        // Auto-show the panel for multi-item playlists so first-time users see the result of their drop. Single-file drops don't reveal — the user already sees their file playing.
        if (viewModel.Playlist.Items.Count >= 2)
        {
            ShowPlaylistPanel();
        }
    }

    private void HandlePanelDrop(List<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }
        viewModel.LoadPaths(paths, replace: false);
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
                ShowPreferencesDialog();
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

    // Reload the runtime keymap and application-level toggles from the dialog's edits, persist to disk, and refresh the menu accelerator labels. Called by PreferencesDialog when the user clicks Save. Failure to persist is logged but not fatal — the in-memory map is already updated and new bindings are live; the user can retry. single_instance only takes effect on next launch (the toggle changes process-startup behavior), so we just record it.
    internal void ApplyPreferences(HotkeyMap map, bool singleInstance, ThemeMode theme)
    {
        hotkeys = map;
        userConfig.Hotkeys = UserConfig.HotkeysSection.FromDictionary(map.ToTomlForm());
        userConfig.Application.SingleInstance = singleInstance;
        userConfig.Application.Theme = ThemeModeParser.ToConfigString(theme);
        // Theme applies live, independent of whether the save below succeeds — the running app and the persisted file are separate concerns. single_instance, by contrast, only changes process-startup behavior, so it just gets recorded for next launch.
        ApplyThemePreference(theme);
        try
        {
            userConfig.Save(configPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vompl] preferences: failed to save config to {configPath}: {ex.Message}");
        }
        RefreshMenuAccels();
    }

    // Push the chosen appearance onto GTK's `gtk-application-prefer-dark-theme`. Light/Dark force it; Auto restores the launch-time system value. This is the legacy GTK3-era knob, so it only restyles themes that honor it — GTK4's built-in Adwaita does, and there it restyles the running app immediately including our chrome CSS (the @theme_bg_color / @borders named colors re-resolve to the active variant). A theme that conveys dark via a separate theme NAME instead (e.g. KDE's Breeze / Breeze-Dark) may ignore this boolean, in which case Light/Dark no-op — the documented limitation of the GTK-native approach.
    private void ApplyThemePreference(ThemeMode mode)
    {
        var settings = Gtk.Settings.GetDefault();
        if (settings == null)
        {
            Console.Error.WriteLine("[vompl] theme: Gtk.Settings.GetDefault() returned null; cannot apply theme preference");
            return;
        }
        settings.GtkApplicationPreferDarkTheme = ThemeModeParser.ResolvePreferDark(mode, systemPreferDarkDefault);
    }

    internal HotkeyMap GetHotkeysSnapshot()
    {
        return hotkeys.Clone();
    }

    internal bool GetSingleInstancePreference()
    {
        return userConfig.Application.SingleInstance;
    }

    // Current theme preference for the dialog to seed its dropdown. The warn is discarded (not a silent swallow): this same string was already parsed-and-warned at startup, and re-warning every time Preferences opens would just be noise.
    internal ThemeMode GetThemePreference()
    {
        return ThemeModeParser.Parse(userConfig.Application.Theme, _ => { });
    }

    // Entry point for files arriving from outside the app — today, the GApplication OnOpen signal fired by a remote-instance forward. Targets Primary directly rather than going through viewModel.LoadPaths (which honors SelectedSlot/SingleTarget) because a CLI second-invocation has no concept of PiP slot selection; landing the file in Primary matches what a user typing `./vomplayer foo.mp4` expects regardless of the running instance's PiP state. Same code path as `HandlePrimaryDrop`, so resume positions, recents, and autosave behave identically. Replace semantics, not append.
    internal void LoadPathsExternal(IReadOnlyList<string> paths)
    {
        if (paths == null || paths.Count == 0)
        {
            return;
        }
        viewModel.Primary.LoadPaths(paths, replace: true);
        if (viewModel.Primary.Playlist.Items.Count >= 2)
        {
            ShowPlaylistPanel();
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
        // Re-derive the stream-selector visibility from IsPipEnabled at the tail of every fullscreen transition. Mirrors the unconditional controlsBox.SetVisible(true) above: the auto-hide timer may have cleared the toolbar's Visible flag, and the reparent only handles parent/CSS — without this, toggling fullscreen while the UI is auto-hidden brings the control bar back but leaves the PiP chooser invisible.
        pipController.UpdateToolbarVisibility();
    }

    // Reparent the PiP stream-selector toolbar between rootBox (windowed: a solid layout row that displaces the video) and videoOverlay (fullscreen: floats at the top of the video). Same pattern as the controlsBox reparenting above but anchored at top. The toolbar's visibility is governed independently by IsPipEnabled.
    private void ReparentStreamSelectorForFullscreen(bool fullscreen)
    {
        var toolbar = pipController.Toolbar;
        if (fullscreen)
        {
            if (toolbar.Parent == rootBox)
            {
                rootBox.Remove(toolbar);
            }
            toolbar.SetValign(Gtk.Align.Start);
            toolbar.SetHalign(Gtk.Align.Start);
            toolbar.RemoveCssClass("vompl-chrome");
            toolbar.AddCssClass("osd");
            if (toolbar.Parent != videoOverlay)
            {
                videoOverlay.AddOverlay(toolbar);
            }
        }
        else
        {
            if (toolbar.Parent == videoOverlay)
            {
                videoOverlay.RemoveOverlay(toolbar);
            }
            toolbar.RemoveCssClass("osd");
            toolbar.AddCssClass("vompl-chrome");
            toolbar.SetValign(Gtk.Align.Fill);
            toolbar.SetHalign(Gtk.Align.Fill);
            if (toolbar.Parent != rootBox)
            {
                // Re-insert at the slot it originally occupied in rootBox: between menubar (index 0) and videoAndPlaylistRow. InsertChildAfter places it after menubar.
                rootBox.InsertChildAfter(toolbar, menuBar);
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
            if (viewModel.IsPipEnabled)
            {
                pipController.Toolbar.SetVisible(false);
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
        // Detach the autosave first — pipController.Dispose below routes through viewModel.DisablePip → autosave.UnbindSecondary, which would otherwise persist a primary-only row over the live stream_count=2 entry. After Detach, the UnbindSecondary's Persist is gated and the saved row survives shutdown intact.
        viewModel.DetachAutosave();
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
        playback.PropertyChanged -= OnPlaybackPropertyChangedForScreensaver;
        // Disposal order is load-bearing in two ways:
        //   (1) DiagnosticOverlay's 1 Hz timer reads playback + pipController — kill it first.
        //   (2) videoSurface.Dispose calls mpv_render_context_free against the primary mpv handle; the handle is terminated by primary playback.Dispose which runs inside viewModel.Dispose (Primary VideoContext now owns its IPlayback's lifetime). So videoSurface MUST dispose before viewModel — see MpvDispatcher.Dispose comment about render-surface-before-dispatcher ordering.
        // PipController internally observes the same order for the secondary stream (its own surface disposes before viewModel.DisablePip).
        diagnosticOverlay.Dispose();
        pipController.Dispose();
        seekScaleController.Dispose();
        playlistPanel.Dispose();
        videoSurface?.Dispose();
        viewModel.Dispose();
        return false;
    }
}
