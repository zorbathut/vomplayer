using System;
using System.ComponentModel;
using System.IO;
using Vomplayer.Controls;
using Vomplayer.Playback;
using Vomplayer.Services;
using Vomplayer.UserData;
using Vomplayer.Util;
using Vomplayer.ViewModels;
using Vomplayer.Wayland;

namespace Vomplayer;

// Picture-in-Picture controller. Owns the secondary Playback / VideoSurface / VideoView, the stream-selector toolbar (widget construction only — MainWindow handles its placement in rootBox and fullscreen reparenting), the PiP layout machinery (drag-to-move, aspect-locked corner resize, hover-driven grip visibility), and the drift-controller tick timer. Subscribes to the VM's IsPipEnabled / SelectedSlot changes to keep the UI in sync; subscribes to each VideoContext's PropertyChanged for toolbar labels and PiP aspect.
//
// The secondary widget is wrapped in a `pipContainer` Gtk.Overlay; the container carries the absolute layout (Halign=Start, Valign=Start, MarginStart, MarginTop, SizeRequest) and hosts a small bottom-right resize grip. Drag gestures on the secondary move the container; on the grip, aspect-locked resize. Layout math (defaults, clamping, aspect projection) is in PipLayoutCalc — kept pure for unit tests.
//
// Coupling to MainWindow flows through IPipHost. The interface is intentionally wide — PiP reaches into the video overlay, the primary widget set, the playlist panel, the hotkey dispatch path, and the controlsBox restack — but routing through the interface keeps PipController out of MainWindow's other concerns (seek scale, screensaver inhibit, autohide timing, file picker invocation, fullscreen-layout decisions for chrome other than the PiP container).
public sealed class PipController : IDisposable
{
    private const int PipMargin = 16;
    private const int PipMinWidth = 160;
    private const int PipMinHeight = 90;
    private const int PipResizeGripSize = 18;
    private const string SelectedVideoCssClass = "vompl-selected-video";
    // App-level commit threshold for drag-to-move. Larger than GTK's gtk-dnd-drag-threshold (default 8 px) because GTK's threshold is empirically crossed by hand tremor / mouse jitter during what the user considers a normal click — under that threshold, GestureDrag fires drag-begin, our handler claims the sequence, and the sibling GestureClick's `released` signal is denied → PlayPause never runs. Bumping this app-side gates the SetState(Claimed) call until motion is unambiguous enough to commit. Picked at 16 px (~1/8" on typical DPI) — comfortably above tremor, comfortably below intentional drag motion.
    private const double PipMoveClaimThresholdPx = 16.0;
    // Drift-controller tick period. 5 Hz balances responsiveness (overshoot detection, sudden-jump catch) against SetSpeed churn; the controller gates internally when paused/seeking/idle. See ViewModelMain.ApplyDriftCorrection.
    private const uint SyncTickIntervalMs = 200;
    private const string StreamSelectorPrimaryFallback = "Video 1";
    private const string StreamSelectorSecondaryFallback = "Video 2";

    private readonly IPipHost host;
    private readonly ViewModelMain viewModel;
    private readonly IRecentFiles recentFiles;
    private readonly ITrackPreferences trackPreferences;
    private readonly IFilePicker filePicker;
    private readonly IUrlDownloader urlDownloader;
    private readonly IUrlPrompt urlPrompt;

    // Secondary (PiP) widget set. All null when PiP is off; populated by Enable / torn down by Disable. The secondary Playback's lifetime is owned by the secondary VideoContext (constructed in Enable, disposed by viewModel.DisablePip → VideoContext.Dispose → playback.Dispose), so PipController doesn't track a field for it.
    private VideoArea? secondaryArea;
    private VideoView? secondaryView;
    private VideoSurface? secondarySurface;

    // Wrapper hosting (secondary widget + resize grip). Carries the layout (margins/size). Null when PiP is off.
    private Gtk.Overlay? pipContainer;
    // The corner resize handle. Held as a field only for SyncPipResizeGripVisibility; construction and event-wiring stays local to AttachPipContainer.
    private Gtk.DrawingArea? pipResizeGripWidget;
    // Hover controller on the wrapper; queried on drag-end to decide whether the pointer is still inside the wrapper (keep grip visible) or outside (hide it). Without this, the leave path that fired during the drag was suppressed by the active flag and would not re-fire after release.
    private Gtk.EventControllerMotion? pipHoverController;

    // User-driven layout overrides, stored as fractions of the primary video region's allocation (width-fraction for width/marginStart, height-fraction for marginTop). Each remains null until the user drags (margins) or resizes (width). Storing fractions — not pixels — means a subsequent window resize keeps the PiP at the same proportional size and position the user picked. Cleared on Disable so a re-enable starts at the default corner placement.
    private double? pipUserWidthFraction;
    private double? pipUserMarginStartFraction;
    private double? pipUserMarginTopFraction;

    // Captured at drag-begin. Margins/width are the layout snapshot at that moment; the overlay-local point is the press position translated into videoOverlay coordinates, which is the reference frame we read deltas in. Why not just `OffsetX`/`OffsetY`? Those are reported in the dragged widget's local frame, and we MOVE the widget during the drag — so the local frame slides under the pointer and OffsetX collapses back toward zero each time we apply our update, producing slow + jittery motion. videoOverlay doesn't move; deltas read in its frame are stable. (Same problem affects the resize grip: as the wrapper grows, the grip — pinned to the wrapper's bottom-right — also moves under the pointer.)
    private int pipDragStartMarginStart;
    private int pipDragStartMarginTop;
    private int pipResizeStartWidth;
    private double pipGestureOverlayStartX;
    private double pipGestureOverlayStartY;

    // Coalescer for the post-ApplyPipLayout geometry refresh. ApplyPipLayout fires per drag-update event (potentially many times per frame); we only need one refresh per frame.
    private bool pipGeometryRefreshScheduled;

    // Repeating GLib timeout that drives the continuous PiP drift controller (ViewModelMain.ApplyDriftCorrection). Armed in Enable, disarmed in Disable. 0 = not armed.
    private uint syncTimeoutId;

    private bool pipMoveClaimed;
    // True while a corner-resize drag is active. Used alongside pipMoveClaimed to keep the grip visible mid-drag even if the pointer briefly slips outside the wrapper's bounds (which would otherwise fire EventControllerMotion::leave and hide the grip).
    private bool pipResizeActive;

    // Stream-selector toolbar. The toolbar is a horizontal Gtk.Box holding one ToggleButton per stream slot; the buttons implement zero-or-one selection (clicking one activates it and deactivates the others; clicking the active one deactivates it, returning to broadcast/sync). GTK4's built-in radio grouping is one-of-N, so the click-active-deselect behavior is hand-rolled here. Indexed by VideoSlot (Primary=0, Secondary=1).
    private readonly Gtk.Box streamSelectorToolbar;
    private readonly Gtk.ToggleButton[] streamSelectorButtons;
    // Re-entrancy guard for the OnToggled handler: when SyncStreamSelectorButtons pushes button state from VM to UI, the SetActive call would re-fire OnToggled and bounce back to SetSelected.
    private bool suppressSelectorToggleSignal;

    // Menu action handles. Populated by CreateStreamMenuActions; null until BuildMenuBar wires them.
    private Gio.SimpleAction? addStreamAction;
    private Gio.SimpleAction? deleteStreamAction;

    // Latches true on the primary's first rendered frame (the host pushes via NotifyPrimaryFirstFrameRendered); never reset. Read by OnSecondaryFirstFrameRendered to decide whether to restack the secondary above the parent.
    private bool primaryFirstFrameRendered;

    // Exposed for MainWindow's layout (insert into rootBox between the menu bar and the video row) and for fullscreen-aware visibility/reparenting.
    public Gtk.Widget Toolbar
    {
        get
        {
            return streamSelectorToolbar;
        }
    }

    public PipController(IPipHost host, ViewModelMain viewModel, IRecentFiles recentFiles, ITrackPreferences trackPreferences, IFilePicker filePicker, IUrlDownloader urlDownloader, IUrlPrompt urlPrompt)
    {
        if (host == null)
        {
            throw new ArgumentNullException(nameof(host));
        }
        if (viewModel == null)
        {
            throw new ArgumentNullException(nameof(viewModel));
        }
        if (recentFiles == null)
        {
            throw new ArgumentNullException(nameof(recentFiles));
        }
        if (trackPreferences == null)
        {
            throw new ArgumentNullException(nameof(trackPreferences));
        }
        if (filePicker == null)
        {
            throw new ArgumentNullException(nameof(filePicker));
        }
        if (urlDownloader == null)
        {
            throw new ArgumentNullException(nameof(urlDownloader));
        }
        if (urlPrompt == null)
        {
            throw new ArgumentNullException(nameof(urlPrompt));
        }
        this.host = host;
        this.viewModel = viewModel;
        this.recentFiles = recentFiles;
        this.trackPreferences = trackPreferences;
        this.filePicker = filePicker;
        this.urlDownloader = urlDownloader;
        this.urlPrompt = urlPrompt;

        // Build the stream-selector toolbar. Hidden by default; visibility flips on IsPipEnabled.
        streamSelectorToolbar = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        streamSelectorToolbar.AddCssClass("vompl-chrome");
        streamSelectorToolbar.AddCssClass("vompl-stream-toolbar");
        streamSelectorToolbar.SetVisible(false);

        // Initial labels are the static fallbacks; UpdateStreamSelectorLabels (called below) replaces them with the loaded file's basename when a file is loaded.
        var primaryButton = Gtk.ToggleButton.NewWithLabel(StreamSelectorPrimaryFallback);
        var secondaryButton = Gtk.ToggleButton.NewWithLabel(StreamSelectorSecondaryFallback);

        primaryButton.OnToggled += (sender, _) => OnStreamSelectorButtonToggled(ViewModelMain.VideoSlot.Primary, sender);
        secondaryButton.OnToggled += (sender, _) => OnStreamSelectorButtonToggled(ViewModelMain.VideoSlot.Secondary, sender);

        streamSelectorToolbar.Append(primaryButton);
        streamSelectorToolbar.Append(secondaryButton);

        streamSelectorButtons = new[] { primaryButton, secondaryButton };

        // Subscribe to Primary's CurrentFilePath now (Primary always exists). Secondary's subscription is hooked/unhooked by Enable / Disable. Resolve the current state once at construction so a Primary that already had a file loaded before the controller built shows the right label.
        viewModel.Primary.PropertyChanged += OnPrimaryContextPropertyChangedForToolbar;
        UpdateStreamSelectorLabels();

        // Subscribe to VM PropertyChanged for IsPipEnabled / SelectedSlot.
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    // Build the Add Stream / Delete Stream menu actions; the host's BuildMenuBar registers them on the action map and inserts them into the menu. We retain refs internally for sensitivity sync.
    public (Gio.SimpleAction Add, Gio.SimpleAction Delete) CreateStreamMenuActions()
    {
        addStreamAction = Gio.SimpleAction.New("add-stream", null);
        addStreamAction.OnActivate += (_, _) => Enable();
        addStreamAction.SetEnabled(!viewModel.IsPipEnabled);

        deleteStreamAction = Gio.SimpleAction.New("delete-stream", null);
        deleteStreamAction.OnActivate += (_, _) => Disable();
        deleteStreamAction.SetEnabled(viewModel.IsPipEnabled);

        return (addStreamAction, deleteStreamAction);
    }

    public void Enable()
    {
        if (viewModel.IsPipEnabled)
        {
            return;
        }
        // Secondary Playback. Mirrors Program.cs's wiring — a fresh Playback instance with its own dispatcher worker thread + mpv client. The IdleAdd post-to-main-thread closure is identical to the primary's; it doesn't share state, just shape. Lifetime is owned by the secondary VideoContext we hand it to below.
        var pb = new Playback.Playback(
            a => Util.IdleSafe.Add((int)GLib.Constants.PRIORITY_DEFAULT_IDLE, a));
        pb.Initialize();

        // Secondary VideoContext. Constructed here (not in the VM ctor) so the VM stays unaware of how Playback instances are minted on Linux/GTK.
        var secondaryCtx = new VideoContext(pb, filePicker, recentFiles, trackPreferences, urlDownloader, urlPrompt);
        viewModel.EnablePip(secondaryCtx);

        if (host.PrimaryArea != null)
        {
            // Wayland subsurface path.
            BuildSecondaryVideoArea(secondaryCtx, pb);
        }
        else if (host.PrimaryView != null)
        {
            // GLArea fallback path.
            BuildSecondaryVideoView(pb);
        }

        // Track the primary's video aspect: a primary file load/unload changes the letterbox/pillarbox layout, which moves the video display rect the PiP is positioned relative to. ApplyPipLayout reads the current rect on each call, so we just need to re-trigger it when the aspect flips.
        viewModel.Primary.PropertyChanged += OnPrimaryContextPropertyChangedForPip;
        // Arm the repeating drift controller for the PiP-on window. Enable early-returns on IsPipEnabled above, so this can't double-arm.
        syncTimeoutId = GLib.Functions.TimeoutAdd(
            (int)GLib.Constants.PRIORITY_DEFAULT,
            SyncTickIntervalMs,
            OnSyncTick);

        // PiP UI bookkeeping: re-bind the playlist panel to whichever context is active (Primary on first enable). Recompute the active CSS class and re-stack the OSD controlsBox if we're already fullscreen so it stays above the new PiP overlay child.
        RebindPlaylistPanelToTarget();
        UpdateSelectedVideoCss();
        ReinsertControlsOverlay();
    }

    private void OnPrimaryContextPropertyChangedForPip(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoContext.VideoAspect))
        {
            ApplyPipLayout();
        }
    }

    public void Disable()
    {
        if (!viewModel.IsPipEnabled)
        {
            return;
        }
        // Unhook the per-source aspect handler before viewModel.DisablePip() disposes Secondary — once disposed, viewModel.Secondary becomes null and we lose the reference we'd need to unsubscribe from. Idempotent if it was never hooked (e.g. Enable failed mid-way).
        if (viewModel.Secondary != null)
        {
            viewModel.Secondary.PropertyChanged -= OnSecondaryContextPropertyChanged;
        }
        viewModel.Primary.PropertyChanged -= OnPrimaryContextPropertyChangedForPip;
        // Disarm the drift controller BEFORE viewModel.DisablePip() disposes Secondary, so a queued tick can't land on a half-disposed context. GLib is single-threaded, so no tick can interleave with this removal.
        if (syncTimeoutId != 0)
        {
            GLib.Functions.SourceRemove(syncTimeoutId);
            syncTimeoutId = 0;
        }
        if (host.PrimaryArea != null)
        {
            host.PrimaryArea.GeometryChanged -= OnPrimaryAreaGeometryChangedForPip;
        }
        // Dispose the secondary render surface BEFORE viewModel.DisablePip — the surface's Dispose calls mpv_render_context_free against the secondary mpv handle, and viewModel.DisablePip → Secondary VideoContext.Dispose → secondary playback.Dispose terminates that handle. See MpvDispatcher.Dispose's "render surface must be disposed before the dispatcher" comment.
        if (secondarySurface != null)
        {
            secondarySurface.RenderContextReady -= OnSecondaryRenderContextReadyWayland;
            secondarySurface.RenderFailed -= OnSecondaryRenderFailed;
            secondarySurface.FirstFrameRendered -= OnSecondaryFirstFrameRendered;
            secondarySurface.Dispose();
            secondarySurface = null;
        }
        if (secondaryView != null)
        {
            secondaryView.RenderContextReady -= OnSecondaryRenderContextReadyGLArea;
            secondaryView.RenderFailed -= OnSecondaryRenderFailed;
            // Same ordering contract as secondarySurface above: free the render context before viewModel.DisablePip terminates the secondary mpv core. Waiting for the widget's unrealize (RemoveOverlay below) would free it after the core is gone — documented UB in libmpv.
            secondaryView.TeardownRenderContext();
            secondaryView = null;
        }
        // VM teardown now: disposes Secondary VideoContext (which detaches HDR/VRR sinks, unsubscribes Playback events, and disposes secondary Playback).
        viewModel.DisablePip();
        if (pipContainer != null)
        {
            // Removing the wrapper from videoOverlay walks the whole subtree (secondary widget + grip), so no per-child RemoveOverlay calls.
            host.VideoOverlay.RemoveOverlay(pipContainer);
            pipContainer = null;
            pipResizeGripWidget = null;
            pipHoverController = null;
        }
        secondaryArea = null;
        // Reset user-set layout so a future Enable starts at the default corner placement again. Persisting across enable/disable was considered and rejected — re-enabling a previously-dragged PiP at a stale absolute position is more disorienting than re-anchoring to the corner.
        pipUserWidthFraction = null;
        pipUserMarginStartFraction = null;
        pipUserMarginTopFraction = null;
        RebindPlaylistPanelToTarget();
        UpdateSelectedVideoCss();
        ReinsertControlsOverlay();
    }

    // Called by the host when the primary VideoSurface fires FirstFrameRendered. Latches the flag (for OnSecondaryFirstFrameRendered's race-against-primary check) and, if the secondary surface was previously re-stacked above the parent (because secondary rendered before primary), restores it to its normal "above primary, below parent" stacking now that primary's first frame is visible. Idempotent in the common case where it was never restacked above the parent.
    public void NotifyPrimaryFirstFrameRendered()
    {
        primaryFirstFrameRendered = true;
        if (secondarySurface != null && host.PrimarySurface != null)
        {
            secondarySurface.PlaceAbove(host.PrimarySurface);
        }
    }

    public VideoSurface? GetTargetVideoSurfaceForDiagnostic()
    {
        if (viewModel.SelectedSlot == ViewModelMain.VideoSlot.Secondary && secondarySurface != null)
        {
            return secondarySurface;
        }
        return host.PrimarySurface;
    }

    private void OnSecondaryRenderFailed(int code)
    {
        Console.Error.WriteLine($"[vomplayer] mpv render failed with code {code}; video rendering stopped.");
    }

    // `pb` is passed in by Enable so we can wire AttachRenderSurface (an internal method on the concrete Playback type, not visible through IPlayback). VideoContext owns its lifetime — we don't retain a field.
    private void BuildSecondaryVideoArea(VideoContext secondaryCtx, Playback.Playback pb)
    {
        var area2 = new VideoArea();
        // The secondary widget itself stays at default fill alignment inside its wrapper; the wrapper carries the layout.
        secondaryArea = area2;
        AttachPipContainer(area2);
        // Re-size the PiP whenever the secondary's loaded source's aspect changes (file load with known dwidth/dheight, or unload back to null). The PropertyChanged source is the secondary VideoContext we just built — it lives at viewModel.Secondary now that Enable ran.
        secondaryCtx.PropertyChanged += OnSecondaryContextPropertyChanged;

        var surface2 = new VideoSurface(host.Window, area2);
        surface2.RenderContextReady += OnSecondaryRenderContextReadyWayland;
        surface2.RenderFailed += OnSecondaryRenderFailed;
        // Asymmetric with primary's hook (which just hides noVideoBg). See OnSecondaryFirstFrameRendered for the rationale.
        surface2.FirstFrameRendered += OnSecondaryFirstFrameRendered;
        secondarySurface = surface2;
        pb.AttachRenderSurface(d => surface2.SetMpvDispatcher(d));
        // Stack PiP above primary so the smaller surface composites on top of the larger video buffer. wl_subsurface.place_above is double-buffered, so the native shim commits the parent immediately to make the new ordering atomic — see vompl_video_surface_place_above's docstring.
        if (host.PrimarySurface != null)
        {
            surface2.PlaceAbove(host.PrimarySurface);
        }

        AttachPipBodyInputs(area2);
        AttachSecondaryDropTarget(area2);
        // Track primary geometry so PiP rescales when the window resizes.
        host.PrimaryArea!.GeometryChanged += OnPrimaryAreaGeometryChangedForPip;
    }

    private void BuildSecondaryVideoView(Playback.Playback pb)
    {
        var view2 = new VideoView();
        secondaryView = view2;
        AttachPipContainer(view2);

        view2.RenderContextReady += OnSecondaryRenderContextReadyGLArea;
        view2.RenderFailed += OnSecondaryRenderFailed;
        pb.AttachRenderSurface(d => view2.AttachDispatcher(d));

        AttachPipBodyInputs(view2);
        AttachSecondaryDropTarget(view2);
        // GLArea path: same per-source PiP-aspect bookkeeping as the Wayland path. viewModel.Secondary was set by Enable before this method runs.
        if (viewModel.Secondary != null)
        {
            viewModel.Secondary.PropertyChanged += OnSecondaryContextPropertyChanged;
        }
    }

    // Wrap the secondary video widget in a Gtk.Overlay (`pipContainer`) and add the wrapper as the videoOverlay's PiP overlay child. The wrapper carries the layout (Halign=Start, Valign=Start, MarginStart, MarginTop, SizeRequest). A small DrawingArea is layered as the wrapper's overlay child in the bottom-right corner to act as the resize grip.
    private void AttachPipContainer(Gtk.Widget secondaryWidget)
    {
        var container = Gtk.Overlay.New();
        container.SetHalign(Gtk.Align.Start);
        container.SetValign(Gtk.Align.Start);
        container.SetHexpand(false);
        container.SetVexpand(false);
        container.SetChild(secondaryWidget);

        var grip = Gtk.DrawingArea.New();
        grip.SetSizeRequest(PipResizeGripSize, PipResizeGripSize);
        grip.SetHalign(Gtk.Align.End);
        grip.SetValign(Gtk.Align.End);
        grip.SetCanTarget(true);
        grip.SetCursorFromName("se-resize");
        grip.SetDrawFunc(DrawPipResizeGrip);
        grip.SetVisible(false);

        var resizeDrag = Gtk.GestureDrag.New();
        resizeDrag.OnDragBegin += OnPipResizeDragBegin;
        resizeDrag.OnDragUpdate += OnPipResizeDragUpdate;
        resizeDrag.OnDragEnd += OnPipResizeDragEnd;
        grip.AddController(resizeDrag);

        container.AddOverlay(grip);

        var moveDrag = Gtk.GestureDrag.New();
        moveDrag.OnDragBegin += OnPipMoveDragBegin;
        moveDrag.OnDragUpdate += OnPipMoveDragUpdate;
        moveDrag.OnDragEnd += OnPipMoveDragEnd;
        secondaryWidget.AddController(moveDrag);

        // Hover-only grip visibility. EventControllerMotion exposes a `contains-pointer` boolean property that's true whenever the pointer is in the controller's widget OR any descendant. We listen to its property-changed notification (not the Enter/Leave signals) — Enter/Leave fire on EVERY intra-tree crossing the pointer makes (e.g., wrapper ↔ secondary ↔ grip), so they're noisy and the contains-pointer state at the moment of OnLeave can still be True. The notify path fires exactly when contains-pointer transitions, which is what we actually want for "hovering anywhere in the PiP region". Visibility is forced true while a drag is active so a fast drag — where the pointer can momentarily slip outside the wrapper bounds before layout catches up — doesn't flicker the grip away mid-gesture.
        var hoverController = Gtk.EventControllerMotion.New();
        hoverController.OnNotify += OnPipHoverControllerNotify;
        container.AddController(hoverController);

        pipContainer = container;
        pipResizeGripWidget = grip;
        pipHoverController = hoverController;

        host.VideoOverlay.AddOverlay(container);
        ApplyPipLayout();
    }

    // Two diagonal hairlines hugging the bottom-right corner of the grip. Drawn in white at moderate alpha so they read against both bright and dark video. The pattern (two parallel lines in the south-east corner) is the standard "resize handle" idiom (cf. CSS `resize: both`, GNOME WindowResizeGripStyle pre-3.20).
    private static void DrawPipResizeGrip(Gtk.DrawingArea area, Cairo.Context cr, int width, int height)
    {
        cr.SetSourceRgba(1.0, 1.0, 1.0, 0.85);
        cr.LineWidth = 2.0;
        // Outer line — goes from (0, height) up to (width, 0)-ish, but inset 3 px so it sits inside the grip.
        cr.MoveTo(width - 1, height * 0.45);
        cr.LineTo(width * 0.45, height - 1);
        cr.Stroke();
        cr.MoveTo(width - 1, height * 0.75);
        cr.LineTo(width * 0.75, height - 1);
        cr.Stroke();
    }

    private void OnSecondaryContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoContext.VideoAspect))
        {
            ApplyPipLayout();
            return;
        }
        if (e.PropertyName == nameof(VideoContext.CurrentFilePath))
        {
            UpdateStreamSelectorLabels();
        }
    }

    private void OnSecondaryRenderContextReadyWayland()
    {
        if (secondarySurface != null && viewModel.Secondary != null)
        {
            viewModel.Secondary.AttachHdrSink(secondarySurface);
            viewModel.Secondary.AttachVrrSink(secondarySurface);
        }
    }

    private void OnSecondaryRenderContextReadyGLArea()
    {
        // GLArea fallback path: HDR is not supported per ARCHITECTURE.md, so there's no IHdrSink to attach. Nothing to do beyond letting mpv take over rendering through the dispatcher attached in BuildSecondaryVideoView.
    }

    private void OnSecondaryFirstFrameRendered()
    {
        // If primary already rendered, noVideoBg is gone and the secondary is correctly stacked above primary (still below parent). Nothing to do.
        if (primaryFirstFrameRendered)
        {
            return;
        }
        // Primary hasn't rendered yet but the secondary just produced its first frame. Hiding noVideoBg here (the symmetric thing the host does on its primary first frame) would expose the transparent parent main surface in the primary's region — desktop shows through. Instead, restack the secondary subsurface above the parent so the PiP composites over the still-visible noVideoBg. NotifyPrimaryFirstFrameRendered eventually restores the secondary to its normal "above primary, below parent" stacking when primary actually renders.
        secondarySurface?.PlaceAboveParent();
    }

    private void OnPrimaryAreaGeometryChangedForPip(int x, int y, int w, int h, int scale)
    {
        // VideoArea fires this whenever its allocation changes. Recompute PiP layout against the primary's new bounds (also re-clamps user-set margins so a window shrink can't strand the PiP off-screen).
        ApplyPipLayout();
    }

    // Resolve the effective PiP layout from PipLayoutCalc and push it to pipContainer. Skips when pipContainer is null (PiP off / mid-teardown).
    private void ApplyPipLayout()
    {
        if (pipContainer == null)
        {
            return;
        }
        var rect = ComputePrimaryVideoRect();
        double aspect = viewModel.Secondary?.VideoAspect ?? (16.0 / 9.0);
        var r = PipLayoutCalc.Compute(rect, aspect, PipMargin, pipUserWidthFraction, pipUserMarginStartFraction, pipUserMarginTopFraction, PipMinWidth, PipMinHeight);
        // Note: we deliberately DO NOT write the post-clamp values back into pipUser*Fraction here. ApplyPipLayout fires on every layout-relevant change including passive window resizes, and round-tripping pixels↔fractions accumulates rounding error each pass — over many small resizes the PiP would walk inward and lose precision. Likewise, when the window shrinks enough to trigger Compute's bounds-clamp, writing the clamped fraction back would erase the user's original placement so a later window-grow couldn't restore it. Instead, the stored fractions are written ONLY by the drag handlers (where the user's intent is unambiguous); Compute clamps for rendering each call, so a stored fraction that's currently out of range (e.g. user dragged to right edge, window then shrank) renders correctly inside bounds AND springs back to its original spot when the window grows again. Subsequent drags resume from a valid position because OnPipMoveDragBegin / OnPipResizeDragBegin capture pipContainer.GetMargin*() / GetAllocatedWidth() — the rendered (clamped) screen position — not the stored fraction.
        pipContainer.SetSizeRequest(r.Width, r.Height);
        pipContainer.SetMarginStart(r.MarginStart);
        pipContainer.SetMarginTop(r.MarginTop);
        // The wrapper uses Halign/Valign=Start with Margin{Start,Top}; explicitly zero the other margins in case anything previously set them on this widget.
        pipContainer.SetMarginEnd(0);
        pipContainer.SetMarginBottom(0);

        // VideoArea.OnResize fires on size changes only — pure-position changes from a wrapper-margin update (drag-to-move, or window-resize-induced clamp where width is fixed) leave the wl_subsurface stranded at the previous screen position. Schedule a deferred RefreshGeometry so it fires after the queued layout pass settles (DEFAULT_IDLE runs after GTK's HIGH_IDLE layout phase). Coalesced so a flurry of drag updates is one refresh per frame, not N. RefreshGeometry's lastX/Y dedupe makes redundant calls free when geometry didn't actually change.
        SchedulePipGeometryRefresh();
    }

    // Resolve the primary's video display rect in videoOverlay-space (== widget-space; the videoArea/videoView fills the overlay so the two share an origin). Combines the primary widget's allocation with the primary VideoContext's display aspect to compute the inner rect mpv actually paints into — which the user perceives as "the video" and the PiP is positioned relative to.
    private PipLayoutCalc.VideoRect ComputePrimaryVideoRect()
    {
        int primaryW = host.PrimaryArea?.GetAllocatedWidth() ?? host.PrimaryView?.GetAllocatedWidth() ?? 0;
        int primaryH = host.PrimaryArea?.GetAllocatedHeight() ?? host.PrimaryView?.GetAllocatedHeight() ?? 0;
        double? primaryAspect = viewModel.Primary.VideoAspect;
        return PipLayoutCalc.ComputeVideoRect(primaryW, primaryH, primaryAspect);
    }

    private void SchedulePipGeometryRefresh()
    {
        if (pipGeometryRefreshScheduled)
        {
            return;
        }
        if (secondaryArea == null)
        {
            // GLArea path: GTK draws the FBO at the widget's actual screen position so there's no separate subsurface position to keep in sync. Nothing to schedule.
            return;
        }
        pipGeometryRefreshScheduled = true;
        IdleSafe.Add((int)GLib.Constants.PRIORITY_DEFAULT_IDLE, () =>
        {
            pipGeometryRefreshScheduled = false;
            secondaryArea?.RefreshGeometry();
        });
    }

    // Repeating drift-controller tick. Runs on the GLib main thread (same thread that owns the VM and mpv observers), so it touches VM state directly. return true keeps it firing; the VM gates internally each tick.
    private bool OnSyncTick()
    {
        viewModel.ApplyDriftCorrection();
        return true;
    }

    private void OnPipMoveDragBegin(Gtk.GestureDrag sender, Gtk.GestureDrag.DragBeginSignalArgs args)
    {
        if (pipContainer == null)
        {
            return;
        }
        // Capture state but DON'T claim yet — claiming here denies the sibling click gesture even when the user only meant to click. We claim from drag-update once motion exceeds PipMoveClaimThresholdPx, leaving small-motion "clicks" untouched so GestureClick.Released can fire normally.
        pipMoveClaimed = false;
        pipDragStartMarginStart = pipContainer.GetMarginStart();
        pipDragStartMarginTop = pipContainer.GetMarginTop();
        TryCaptureGestureStartInOverlay(sender);
    }

    private void OnPipMoveDragUpdate(Gtk.GestureDrag sender, Gtk.GestureDrag.DragUpdateSignalArgs args)
    {
        if (pipContainer == null)
        {
            return;
        }
        if (!pipMoveClaimed)
        {
            // Motion is reported in the dragged widget's local frame. We haven't moved the widget yet (no claim → no apply), so widget-local offsets are equivalent to screen-space offsets here — fine for thresholding. Once we claim and start applying moves, we switch to overlay-translated coords (TryGetGesturePointerInOverlay) for the stable reference frame.
            if (Math.Abs(args.OffsetX) < PipMoveClaimThresholdPx && Math.Abs(args.OffsetY) < PipMoveClaimThresholdPx)
            {
                return;
            }
            pipMoveClaimed = true;
            sender.SetState(Gtk.EventSequenceState.Claimed);
        }
        if (!TryGetGesturePointerInOverlay(sender, args.OffsetX, args.OffsetY, out double overlayX, out double overlayY))
        {
            return;
        }
        var rect = ComputePrimaryVideoRect();
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }
        double dx = overlayX - pipGestureOverlayStartX;
        double dy = overlayY - pipGestureOverlayStartY;
        // Compute the new widget-space pixel margin from the captured start, subtract the video rect's offset to get an in-rect position, then convert to a fraction of the rect. Storing fractions relative to the *video rect* — not the surrounding widget — lets the PiP stay glued to the visible video region across both window resizes and viewport-aspect changes (e.g. 16:9 widget showing a 4:3 video has pillarbox bars that the PiP must not drift into).
        double newMarginStart = pipDragStartMarginStart + dx;
        double newMarginTop = pipDragStartMarginTop + dy;
        pipUserMarginStartFraction = (newMarginStart - rect.X) / rect.Width;
        pipUserMarginTopFraction = (newMarginTop - rect.Y) / rect.Height;
        ApplyPipLayout();
    }

    private void OnPipResizeDragBegin(Gtk.GestureDrag sender, Gtk.GestureDrag.DragBeginSignalArgs args)
    {
        if (pipContainer == null)
        {
            return;
        }
        pipResizeStartWidth = pipContainer.GetAllocatedWidth();
        TryCaptureGestureStartInOverlay(sender);
        sender.SetState(Gtk.EventSequenceState.Claimed);
        pipResizeActive = true;
        // Anchoring choice: resize keeps the top-left fixed (pipContainer's margins don't change). The grip is in the bottom-right corner so the standard "drag the corner outward" UX maps to growing width/height. This is consistent with most window-manager corner-resize behaviors.
    }

    private void OnPipResizeDragUpdate(Gtk.GestureDrag sender, Gtk.GestureDrag.DragUpdateSignalArgs args)
    {
        if (pipContainer == null)
        {
            return;
        }
        if (!TryGetGesturePointerInOverlay(sender, args.OffsetX, args.OffsetY, out double overlayX, out double overlayY))
        {
            return;
        }
        var rect = ComputePrimaryVideoRect();
        if (rect.Width <= 0)
        {
            return;
        }
        double dx = overlayX - pipGestureOverlayStartX;
        double dy = overlayY - pipGestureOverlayStartY;
        double aspect = viewModel.Secondary?.VideoAspect ?? (16.0 / 9.0);
        double dw = PipLayoutCalc.ProjectAspectResize(dx, dy, aspect);
        // Convert the new pixel width to a fraction of the video rect's width — see pipUserWidthFraction's docstring for why fractions and why relative to the rect rather than the widget.
        double newWidth = pipResizeStartWidth + dw;
        pipUserWidthFraction = newWidth / rect.Width;
        ApplyPipLayout();
    }

    private void OnPipResizeDragEnd(Gtk.GestureDrag sender, Gtk.GestureDrag.DragEndSignalArgs args)
    {
        pipResizeActive = false;
        SyncPipResizeGripVisibility();
    }

    private void OnPipMoveDragEnd(Gtk.GestureDrag sender, Gtk.GestureDrag.DragEndSignalArgs args)
    {
        pipMoveClaimed = false;
        SyncPipResizeGripVisibility();
    }

    private void OnPipHoverControllerNotify(GObject.Object sender, GObject.Object.NotifySignalArgs args)
    {
        // Only the contains-pointer change drives the grip; ignore is-pointer (which fires whenever pointer transitions between the wrapper and its own descendants) and any other property notifications.
        if (args.Pspec.GetName() != "contains-pointer")
        {
            return;
        }
        SyncPipResizeGripVisibility();
    }

    // Resolve the grip's visibility against the latest hover state + active-drag flags. Called from drag-end (to belatedly apply a hide that was suppressed mid-drag) and from the hover controller's contains-pointer notify handler. If any drag is active OR the pointer is currently inside the wrapper or a descendant, keep the grip visible; otherwise hide.
    private void SyncPipResizeGripVisibility()
    {
        if (pipResizeGripWidget == null)
        {
            return;
        }
        bool keepVisible = pipMoveClaimed || pipResizeActive || (pipHoverController?.GetContainsPointer() ?? false);
        pipResizeGripWidget.SetVisible(keepVisible);
    }

    // Capture the gesture's press point translated into videoOverlay-local coordinates. videoOverlay is a stationary common ancestor of every widget the PiP gestures attach to, so its coordinate space is invariant under the moves we apply during the drag.
    private void TryCaptureGestureStartInOverlay(Gtk.GestureDrag sender)
    {
        sender.GetStartPoint(out double localX, out double localY);
        var widget = sender.GetWidget();
        if (widget == null)
        {
            return;
        }
        if (widget.TranslateCoordinates(host.VideoOverlay, localX, localY, out double overlayX, out double overlayY))
        {
            pipGestureOverlayStartX = overlayX;
            pipGestureOverlayStartY = overlayY;
        }
    }

    // Translate the current pointer position (start point + signal offsets, both in widget-local) into videoOverlay-local coordinates. The widget the gesture is on may have moved between begin and now; TranslateCoordinates uses the widget's CURRENT allocation, so the return value is the pointer's true overlay-local position regardless of how many moves we've already applied. Returns false on the rare case where the widget has been unparented (gesture firing late during teardown).
    private bool TryGetGesturePointerInOverlay(Gtk.GestureDrag sender, double offsetX, double offsetY, out double overlayX, out double overlayY)
    {
        sender.GetStartPoint(out double localX, out double localY);
        var widget = sender.GetWidget();
        if (widget == null)
        {
            overlayX = 0;
            overlayY = 0;
            return false;
        }
        return widget.TranslateCoordinates(host.VideoOverlay, localX + offsetX, localY + offsetY, out overlayX, out overlayY);
    }

    // Click + drag wiring for the PiP body. Click fires on RELEASE so it doesn't pre-empt the drag-to-move gesture: when motion exceeds PipMoveClaimThresholdPx the drag-update handler calls SetState(Claimed), which transitions this GestureClick's view of the same sequence to Denied — and a Denied sequence's `released` signal does not fire. Sub-threshold motion never claims → release fires normally and runs the bound action (default: MouseClick1 → PlayPause, MouseDoubleClick1 → ToggleFullscreen).
    private void AttachPipBodyInputs(Gtk.Widget widget)
    {
        var clickGesture = Gtk.GestureClick.New();
        clickGesture.Button = 0;
        clickGesture.OnReleased += OnPipBodyReleased;
        widget.AddController(clickGesture);
    }

    private void OnPipBodyReleased(Gtk.GestureClick sender, Gtk.GestureClick.ReleasedSignalArgs args)
    {
        uint button = sender.GetCurrentButton();
        if (button == 0)
        {
            return;
        }
        // A click on the PiP video is activity too — reveal the (possibly sticky-hidden) fullscreen cursor/controls, matching the primary video's click behavior. A click-drag is claimed by the move gesture so this `released` won't fire, but the window-level motion controller covers the drag motion.
        host.NotifyFullscreenActivity();
        var trigger = new Trigger.MouseClick(button, args.NPress);
        var action = host.Hotkeys.Lookup(trigger);
        if (action != null)
        {
            host.ExecuteAction(action.Value);
        }
    }

    private void AttachSecondaryDropTarget(Gtk.Widget widget)
    {
        var dropTarget = UriListDropTarget.Create(paths => HandleSecondaryDrop(paths));
        widget.AddController(dropTarget);
    }

    private void HandleSecondaryDrop(System.Collections.Generic.List<string> paths)
    {
        if (viewModel.Secondary == null || paths.Count == 0)
        {
            return;
        }
        // Drop on the PiP region always targets Secondary, regardless of which slot is currently active. (Per-widget drop target wins over the window-level drop, which routes to active.)
        viewModel.Secondary.LoadPaths(paths, replace: true);
    }

    // Apply the selected CSS class to whichever video widget represents the currently-selected slot, or remove the highlight from all when no selection. The class adds a 2 px inset white outline so the user can see at a glance that the next input goes to that stream alone (vs. broadcast/sync when no widget is highlighted).
    private void UpdateSelectedVideoCss()
    {
        host.PrimaryArea?.RemoveCssClass(SelectedVideoCssClass);
        host.PrimaryView?.RemoveCssClass(SelectedVideoCssClass);
        secondaryArea?.RemoveCssClass(SelectedVideoCssClass);
        secondaryView?.RemoveCssClass(SelectedVideoCssClass);
        if (!viewModel.IsPipEnabled)
        {
            return;
        }
        Gtk.Widget? selectedWidget;
        if (viewModel.SelectedSlot == ViewModelMain.VideoSlot.Secondary)
        {
            selectedWidget = secondaryArea ?? (Gtk.Widget?)secondaryView;
        }
        else if (viewModel.SelectedSlot == ViewModelMain.VideoSlot.Primary)
        {
            selectedWidget = host.PrimaryArea ?? (Gtk.Widget?)host.PrimaryView;
        }
        else
        {
            // No selection — broadcast/sync; nothing highlighted.
            return;
        }
        selectedWidget?.AddCssClass(SelectedVideoCssClass);
    }

    // Re-bind the playlist panel to whichever context the chrome should show — SelectedContext when set, Primary otherwise. The panel re-subscribes its Playlist.Changed listener atomically so the click-to-play handler points at the right context.
    private void RebindPlaylistPanelToTarget()
    {
        var ctx = viewModel.SingleTarget;
        host.PlaylistPanel.Rebind(ctx.Playlist, ctx.PlayPlaylistItem);
    }

    // Re-add controlsBox to the overlay if it's already there (fullscreen). Since Gtk.Overlay renders overlay children in addition order — later AddOverlay = higher — we move controlsBox to the top of the stack so any newly-added PiP overlay child sits below the OSD. Idempotent: when controlsBox is in rootBox (windowed mode) this is a no-op.
    private void ReinsertControlsOverlay()
    {
        if (host.ControlsBox.Parent != host.VideoOverlay)
        {
            return;
        }
        host.VideoOverlay.RemoveOverlay(host.ControlsBox);
        host.VideoOverlay.AddOverlay(host.ControlsBox);
    }

    private void OnPrimaryContextPropertyChangedForToolbar(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoContext.CurrentFilePath))
        {
            UpdateStreamSelectorLabels();
        }
    }

    // Replace each toolbar button's label with the loaded file's basename, or fall back to the static "Video N" label when no file is loaded. Path.GetFileName degrades gracefully on URI-shaped strings (returns the last URL segment; YouTube URLs land on the watch?v=… segment, which is informative enough until URL-aware labeling becomes a follow-up). Idempotent — SetLabel is safe to call repeatedly with the same string.
    private void UpdateStreamSelectorLabels()
    {
        var primaryBtn = streamSelectorButtons[(int)ViewModelMain.VideoSlot.Primary];
        primaryBtn.SetLabel(LabelForContext(viewModel.Primary, StreamSelectorPrimaryFallback));

        var secondaryBtn = streamSelectorButtons[(int)ViewModelMain.VideoSlot.Secondary];
        string label = viewModel.Secondary != null
            ? LabelForContext(viewModel.Secondary, StreamSelectorSecondaryFallback)
            : StreamSelectorSecondaryFallback;
        secondaryBtn.SetLabel(label);
    }

    private static string LabelForContext(VideoContext ctx, string fallback)
    {
        var path = ctx.CurrentFilePath;
        if (string.IsNullOrEmpty(path))
        {
            return fallback;
        }
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name))
        {
            return fallback;
        }
        return name;
    }

    // OnToggled handler for a stream-selector button. Implements the zero-or-one behavior: clicking one activates it (and deactivates all others); clicking the active one deactivates it (selection → null). The suppressSelectorToggleSignal guard suppresses the inner deactivation calls so the cleared button's OnToggled doesn't bounce back into SetSelected.
    private void OnStreamSelectorButtonToggled(ViewModelMain.VideoSlot slot, Gtk.ToggleButton sender)
    {
        if (suppressSelectorToggleSignal)
        {
            return;
        }
        if (sender.GetActive())
        {
            // User activated this button — deactivate all others, then push selection to VM.
            suppressSelectorToggleSignal = true;
            try
            {
                for (int i = 0; i < streamSelectorButtons.Length; i++)
                {
                    var other = streamSelectorButtons[i];
                    if (!ReferenceEquals(other, sender) && other.GetActive())
                    {
                        other.SetActive(false);
                    }
                }
            }
            finally
            {
                suppressSelectorToggleSignal = false;
            }
            viewModel.SetSelected(slot);
        }
        else
        {
            // User clicked the active button → deselect.
            viewModel.SetSelected(null);
        }
    }

    // Hooked to viewModel.PropertyChanged. The coordinator fires PropertyChanged for SelectedSlot / IsPipEnabled itself; we react by updating the CSS highlight + re-binding the playlist panel + syncing menu action sensitivities + syncing the toolbar selection. The diagnostic overlay re-resolves its providers every 1 Hz tick, so its data follows the target automatically without explicit poking here.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModelMain.SelectedSlot))
        {
            RebindPlaylistPanelToTarget();
            UpdateSelectedVideoCss();
            SyncStreamSelectorButtons();
            return;
        }
        if (e.PropertyName == nameof(ViewModelMain.IsPipEnabled))
        {
            UpdateSelectedVideoCss();
            UpdateToolbarVisibility();
            // Refresh labels on stream add/remove. Enable just brought Secondary into existence (its label may already need to read from a freshly-loaded file if Enable was triggered after a load); Disable just nulled it (the Secondary button reverts to "Video 2" fallback).
            UpdateStreamSelectorLabels();
            SyncStreamMenuActionSensitivity();
        }
    }

    // Mutually-exclusive enable: Add Stream when PiP is off, Delete Stream when PiP is on. The action handles are populated by CreateStreamMenuActions; null-guarded so an early invocation pre-menu-build is a no-op.
    private void SyncStreamMenuActionSensitivity()
    {
        if (addStreamAction != null)
        {
            addStreamAction.SetEnabled(!viewModel.IsPipEnabled);
        }
        if (deleteStreamAction != null)
        {
            deleteStreamAction.SetEnabled(viewModel.IsPipEnabled);
        }
    }

    // Toolbar visibility is governed solely by IsPipEnabled — a single-stream session never shows it.
    public void UpdateToolbarVisibility()
    {
        streamSelectorToolbar.SetVisible(viewModel.IsPipEnabled);
    }

    // VM → UI sync: set each button's Active state from viewModel.SelectedSlot. The re-entrancy guard prevents the resulting OnToggled fires from bouncing back into the VM.
    private void SyncStreamSelectorButtons()
    {
        suppressSelectorToggleSignal = true;
        try
        {
            for (int i = 0; i < streamSelectorButtons.Length; i++)
            {
                var btn = streamSelectorButtons[i];
                bool shouldBeActive = viewModel.SelectedSlot.HasValue && (int)viewModel.SelectedSlot.Value == i;
                if (btn.GetActive() != shouldBeActive)
                {
                    btn.SetActive(shouldBeActive);
                }
            }
        }
        finally
        {
            suppressSelectorToggleSignal = false;
        }
    }

    public void Dispose()
    {
        if (viewModel.IsPipEnabled)
        {
            Disable();
        }
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        viewModel.Primary.PropertyChanged -= OnPrimaryContextPropertyChangedForToolbar;
    }
}

// Surface the host's GTK state to PipController. MainWindow implements this and PipController only sees the bits it needs. Intentionally wide — PiP touches the video overlay, the primary widget set, the playlist panel, the hotkey dispatch path, and the controlsBox restack — but narrower than direct field access into the host.
public interface IPipHost
{
    Gtk.Window Window { get; }
    Gtk.Overlay VideoOverlay { get; }
    Gtk.Box ControlsBox { get; }
    VideoArea? PrimaryArea { get; }
    VideoView? PrimaryView { get; }
    VideoSurface? PrimarySurface { get; }
    PlaylistPanel PlaylistPanel { get; }
    HotkeyMap Hotkeys { get; }
    void ExecuteAction(HotkeyAction action);
    void NotifyFullscreenActivity();
}
