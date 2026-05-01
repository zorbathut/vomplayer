using System;
using System.ComponentModel;
using System.IO;
using Vomplayer.Controls;
using Vomplayer.UserData;
using Vomplayer.ViewModels;
using Vomplayer.Wayland;

namespace Vomplayer;

// Picture-in-Picture lifecycle for MainWindow. Constructs the secondary Playback / VideoSurface or VideoView, wires it into the VM coordinator, and tears it all down on disable. Layout decision is "GTK Overlay child + Halign=Start, Valign=End, SetSizeRequest(pipW, pipH)" — sufficient for v1 fixed-corner PiP. Future drag-to-reposition swaps the alignment for explicit margins.
public partial class MainWindow
{
    private const int PipMargin = 16;
    private const int PipMinWidth = 160;
    private const int PipMinHeight = 90;
    private const string SelectedVideoCssClass = "vompl-selected-video";

    // Stream-selector toolbar fields. Constructed in Phase 4 (BuildStreamSelectorToolbar). Declared here as nullable so OnViewModelPipPropertyChanged + SyncStreamSelectorButtons can defensively no-op pre-build, and so the menu refactor in Phase 6 can null-check before calling SetEnabled. The ToggleButton[] is indexed by VideoSlot (Primary=0, Secondary=1).
    private Gtk.Box? streamSelectorToolbar;
    private Gtk.ToggleButton[]? streamSelectorButtons;
    // Re-entrancy guard for the OnToggled handler: when SyncStreamSelectorButtons pushes button state from VM to UI, the SetActive call would re-fire OnToggled and bounce back to SetSelected. The guard suppresses the inner SetSelected call during VM→UI sync.
    private bool suppressSelectorToggleSignal;
    // Menu action handles for sensitivity updates from OnViewModelPipPropertyChanged. Filled in by BuildMenuBar (Phase 6).
    private Gio.SimpleAction? addStreamAction;
    private Gio.SimpleAction? deleteStreamAction;

    // Construct the stream-selector toolbar once at window startup. Hidden by default (visibility flips on IsPipEnabled). The toolbar is a horizontal Gtk.Box holding one ToggleButton per stream slot; the buttons implement zero-or-one selection (clicking one activates it and deactivates the others; clicking the active one deactivates it, returning to broadcast/sync). GTK4's built-in radio grouping is one-of-N, so the click-active-deselect behavior is hand-rolled here.
    private void BuildStreamSelectorToolbar()
    {
        var toolbar = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        toolbar.AddCssClass("vompl-chrome");
        toolbar.AddCssClass("vompl-stream-toolbar");
        toolbar.SetVisible(false);

        // Initial labels are the static fallbacks; UpdateStreamSelectorLabels (called below) replaces them with the loaded file's basename when a file is loaded.
        var primaryButton = Gtk.ToggleButton.NewWithLabel(StreamSelectorPrimaryFallback);
        var secondaryButton = Gtk.ToggleButton.NewWithLabel(StreamSelectorSecondaryFallback);

        primaryButton.OnToggled += (sender, _) => OnStreamSelectorButtonToggled(ViewModelMain.VideoSlot.Primary, sender);
        secondaryButton.OnToggled += (sender, _) => OnStreamSelectorButtonToggled(ViewModelMain.VideoSlot.Secondary, sender);

        toolbar.Append(primaryButton);
        toolbar.Append(secondaryButton);

        streamSelectorToolbar = toolbar;
        // Indexed by VideoSlot enum order: Primary=0, Secondary=1. SyncStreamSelectorButtons relies on this ordering.
        streamSelectorButtons = new[] { primaryButton, secondaryButton };

        // Subscribe to Primary's CurrentFilePath now (Primary always exists). Secondary's subscription is hooked/unhooked by EnablePip / DisablePip. UpdateStreamSelectorLabels resolves the current state once at construction so a Primary that already had a file loaded before the toolbar built shows the right label (the StartupApply seam loads InitialFile before the window builds in some flows).
        viewModel.Primary.PropertyChanged += OnPrimaryContextPropertyChangedForToolbar;
        UpdateStreamSelectorLabels();
    }

    private const string StreamSelectorPrimaryFallback = "Video 1";
    private const string StreamSelectorSecondaryFallback = "Video 2";

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
        if (streamSelectorButtons == null)
        {
            return;
        }
        var primaryBtn = streamSelectorButtons[(int)ViewModelMain.VideoSlot.Primary];
        if (primaryBtn != null)
        {
            primaryBtn.SetLabel(LabelForContext(viewModel.Primary, StreamSelectorPrimaryFallback));
        }
        var secondaryBtn = streamSelectorButtons[(int)ViewModelMain.VideoSlot.Secondary];
        if (secondaryBtn != null)
        {
            string label = viewModel.Secondary != null
                ? LabelForContext(viewModel.Secondary, StreamSelectorSecondaryFallback)
                : StreamSelectorSecondaryFallback;
            secondaryBtn.SetLabel(label);
        }
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
                if (streamSelectorButtons != null)
                {
                    for (int i = 0; i < streamSelectorButtons.Length; i++)
                    {
                        var other = streamSelectorButtons[i];
                        if (other != null && !ReferenceEquals(other, sender) && other.GetActive())
                        {
                            other.SetActive(false);
                        }
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

    public void EnablePip()
    {
        if (viewModel.IsPipEnabled || secondaryPlayback != null)
        {
            return;
        }
        // Secondary Playback. Mirrors Program.cs's wiring — a fresh Playback instance with its own dispatcher worker thread + mpv client. The IdleAdd post-to-main-thread closure is identical to the primary's; it doesn't share state, just shape.
        var pb = new Playback.Playback(
            a => GLib.Functions.IdleAdd(
                (int)GLib.Constants.PRIORITY_DEFAULT_IDLE,
                () =>
                {
                    a();
                    return false;
                }));
        pb.Initialize();
        secondaryPlayback = pb;

        // Secondary VideoContext. Constructed here (not in the VM ctor) so the VM stays unaware of how Playback instances are minted on Linux/GTK.
        var secondaryCtx = new VideoContext(pb, filePicker, recentFiles, trackPreferences, urlDownloader, urlPrompt, forceSdr);
        viewModel.EnablePip(secondaryCtx);

        if (videoArea != null)
        {
            // Wayland subsurface path.
            BuildSecondaryVideoArea(secondaryCtx);
        }
        else if (videoView != null)
        {
            // GLArea fallback path.
            BuildSecondaryVideoView();
        }

        // PiP UI bookkeeping: re-bind the playlist panel to whichever context is active (Primary on first enable; SetActive may have been called pre-enable in tests, but in production EnablePip lands with active=Primary). Recompute the active CSS class and re-stack the OSD controlsBox if we're already fullscreen so it stays above the new PiP overlay child.
        RebindPlaylistPanelToTarget();
        UpdateSelectedVideoCss();
        ReinsertControlsOverlay();
    }

    public void DisablePip()
    {
        if (!viewModel.IsPipEnabled)
        {
            return;
        }
        // Unhook the per-source aspect handler before viewModel.DisablePip() disposes Secondary — once disposed, viewModel.Secondary becomes null and we lose the reference we'd need to unsubscribe from. Idempotent if it was never hooked (e.g. EnablePip failed mid-way).
        if (viewModel.Secondary != null)
        {
            viewModel.Secondary.PropertyChanged -= OnSecondaryContextPropertyChanged;
        }
        // VM teardown comes first: it disposes the VideoContext (which detaches the HDR sink + unsubscribes Source/Output handlers); doing this before the surface is destroyed lets the SDR pre-stage call inside DetachHdrSink land on a still-live surface.
        viewModel.DisablePip();

        if (secondaryArea != null)
        {
            videoArea!.GeometryChanged -= OnPrimaryAreaGeometryChangedForPip;
            videoOverlay.RemoveOverlay(secondaryArea);
            secondaryArea = null;
        }
        if (secondarySurface != null)
        {
            secondarySurface.RenderContextReady -= OnSecondaryRenderContextReadyWayland;
            secondarySurface.RenderFailed -= OnVideoRenderFailed;
            secondarySurface.FirstFrameRendered -= OnVideoFirstFrameRendered;
            secondarySurface.Dispose();
            secondarySurface = null;
        }
        if (secondaryView != null)
        {
            secondaryView.RenderContextReady -= OnSecondaryRenderContextReadyGLArea;
            secondaryView.RenderFailed -= OnVideoRenderFailed;
            videoOverlay.RemoveOverlay(secondaryView);
            secondaryView = null;
        }
        if (secondaryPlayback != null)
        {
            secondaryPlayback.Dispose();
            secondaryPlayback = null;
        }
        RebindPlaylistPanelToTarget();
        UpdateSelectedVideoCss();
        ReinsertControlsOverlay();
    }

    private void BuildSecondaryVideoArea(VideoContext secondaryCtx)
    {
        var area2 = new VideoArea();
        area2.SetHalign(Gtk.Align.Start);
        area2.SetValign(Gtk.Align.End);
        area2.SetHexpand(false);
        area2.SetVexpand(false);
        area2.SetMarginStart(PipMargin);
        area2.SetMarginBottom(PipMargin);
        UpdatePipSizeRequest(area2);
        videoOverlay.AddOverlay(area2);
        secondaryArea = area2;
        // Re-size the PiP whenever the secondary's loaded source's aspect changes (file load with known dwidth/dheight, or unload back to null). The PropertyChanged source is the secondary VideoContext we just built — it lives at viewModel.Secondary now that EnablePip ran.
        secondaryCtx.PropertyChanged += OnSecondaryContextPropertyChanged;

        var surface2 = new VideoSurface(this, area2);
        surface2.RenderContextReady += OnSecondaryRenderContextReadyWayland;
        surface2.RenderFailed += OnVideoRenderFailed;
        // noVideoBg is a black fill on the parent surface that hides on the FIRST primary-rendered frame. Without subscribing here too, a user who enables PiP and only loads content into the secondary would see PiP rendering correctly into its subsurface but invisible behind the still-opaque-black parent surface (subsurface is placed below parent — see hdr_helper.c). FirstFrameRendered is fire-once per surface; OnVideoFirstFrameRendered's SetVisible(false) is idempotent, so multiple sources hooking it is safe.
        surface2.FirstFrameRendered += OnVideoFirstFrameRendered;
        secondarySurface = surface2;
        secondaryPlayback!.AttachRenderSurface(d => surface2.SetMpvDispatcher(d));
        // Stack PiP above primary so the smaller surface composites on top of the larger video buffer. wl_subsurface.place_above is double-buffered, so the native shim commits the parent immediately to make the new ordering atomic — see vompl_video_surface_place_above's docstring.
        if (videoSurface != null)
        {
            surface2.PlaceAbove(videoSurface);
        }

        AttachClickToFocus(area2, ViewModelMain.VideoSlot.Secondary);
        AttachSecondaryDropTarget(area2);
        // Track primary geometry so PiP rescales when the window resizes.
        videoArea!.GeometryChanged += OnPrimaryAreaGeometryChangedForPip;
    }

    private void BuildSecondaryVideoView()
    {
        var view2 = new VideoView();
        view2.SetHalign(Gtk.Align.Start);
        view2.SetValign(Gtk.Align.End);
        view2.SetHexpand(false);
        view2.SetVexpand(false);
        view2.SetMarginStart(PipMargin);
        view2.SetMarginBottom(PipMargin);
        UpdatePipSizeRequest(view2);
        videoOverlay.AddOverlay(view2);
        secondaryView = view2;

        view2.RenderContextReady += OnSecondaryRenderContextReadyGLArea;
        view2.RenderFailed += OnVideoRenderFailed;
        secondaryPlayback!.AttachRenderSurface(d => view2.AttachDispatcher(d));

        AttachClickToFocus(view2, ViewModelMain.VideoSlot.Secondary);
        AttachSecondaryDropTarget(view2);
        // GLArea path: same per-source PiP-aspect bookkeeping as the Wayland path. viewModel.Secondary was set by EnablePip before this method runs.
        if (viewModel.Secondary != null)
        {
            viewModel.Secondary.PropertyChanged += OnSecondaryContextPropertyChanged;
        }
    }

    private void OnSecondaryContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoContext.VideoAspect))
        {
            if (secondaryArea != null)
            {
                UpdatePipSizeRequest(secondaryArea);
            }
            else if (secondaryView != null)
            {
                UpdatePipSizeRequest(secondaryView);
            }
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
        }
    }

    private void OnSecondaryRenderContextReadyGLArea()
    {
        // GLArea fallback path: HDR is not supported per ARCHITECTURE.md, so there's no IHdrSink to attach. Nothing to do beyond letting mpv take over rendering through the dispatcher attached in BuildSecondaryVideoView.
    }

    private void OnPrimaryAreaGeometryChangedForPip(int x, int y, int w, int h, int scale)
    {
        // VideoArea fires this whenever its allocation changes. Use it to recompute PiP size against the primary's new bounds.
        if (secondaryArea != null)
        {
            UpdatePipSizeRequest(secondaryArea);
        }
    }

    // PiP sizing rule: 1/4 of primary's allocation width with the secondary source's actual display aspect (mpv dwidth/dheight). Falls back to 16:9 pre-load. Floors at PipMinWidth × PipMinHeight so the corner thumbnail stays drag/click-targetable on small windows; if the aspect is so extreme that the floor blows out the other dimension (e.g. a portrait source on a tiny primary), the floor on the binding dimension wins and the other expands accordingly to preserve aspect.
    private void UpdatePipSizeRequest(Gtk.Widget secondaryWidget)
    {
        int primaryW = videoArea?.GetAllocatedWidth() ?? videoView?.GetAllocatedWidth() ?? 0;
        int primaryH = videoArea?.GetAllocatedHeight() ?? videoView?.GetAllocatedHeight() ?? 0;
        double aspect = viewModel.Secondary?.VideoAspect ?? (16.0 / 9.0);
        if (aspect <= 0)
        {
            aspect = 16.0 / 9.0;
        }
        if (primaryW <= 0 || primaryH <= 0)
        {
            // Pre-realize / pre-allocation. SetSizeRequest with a 2× floor at the source's aspect; the next geometry change will recompute against actual primary bounds.
            int initialW = PipMinWidth * 2;
            int initialH = (int)Math.Round(initialW / aspect);
            secondaryWidget.SetSizeRequest(initialW, initialH);
            return;
        }
        int pipW = Math.Max(PipMinWidth, primaryW / 4);
        int pipH = (int)Math.Round(pipW / aspect);
        if (pipH < PipMinHeight)
        {
            pipH = PipMinHeight;
            pipW = (int)Math.Round(pipH * aspect);
        }
        secondaryWidget.SetSizeRequest(pipW, pipH);
    }

    // Single-press routing: snap active to the slot for this widget. The existing HotkeyMap routing for double-click → ToggleFullscreen still fires on NPress=2 because we don't claim the press event; the gesture continues to deliver subsequent presses with incrementing NPress.
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
        // Click is pure HotkeyMap dispatch — single-click → PlayPause, double-click → ToggleFullscreen. The earlier focus-on-click side-effect is gone (the toolbar's the explicit selector now). PlayPause's coordinator implementation handles selected-vs-broadcast routing on its own.
        var trigger = new Trigger.MouseClick(button, args.NPress);
        var action = hotkeys.Lookup(trigger);
        if (action != null)
        {
            ExecuteAction(action.Value);
        }
    }

    private void AttachSecondaryDropTarget(Gtk.Widget widget)
    {
        var dropTarget = Gtk.DropTarget.New(Gdk.FileList.GetGType(), Gdk.DragAction.Copy | Gdk.DragAction.Move | Gdk.DragAction.Link);
        dropTarget.OnDrop += OnSecondaryFileDrop;
        widget.AddController(dropTarget);
    }

    private bool OnSecondaryFileDrop(Gtk.DropTarget sender, Gtk.DropTarget.DropSignalArgs args)
    {
        if (viewModel.Secondary == null)
        {
            return false;
        }
        var paths = ExtractAndExpandPaths(args.Value);
        if (paths.Count == 0)
        {
            return false;
        }
        // Drop on the PiP region always targets Secondary, regardless of which slot is currently active. (Per-widget drop target wins over the window-level drop, which routes to active.)
        viewModel.Secondary.LoadPaths(paths, replace: true);
        return true;
    }

    private bool OnPrimaryFileDrop(Gtk.DropTarget sender, Gtk.DropTarget.DropSignalArgs args)
    {
        var paths = ExtractAndExpandPaths(args.Value);
        if (paths.Count == 0)
        {
            return false;
        }
        // Drop on the primary video area always targets Primary, regardless of active. (When PiP is off this is identical to the window-level drop's target.)
        viewModel.Primary.LoadPaths(paths, replace: true);
        if (viewModel.Primary.Playlist.Items.Count >= 2)
        {
            ShowPlaylistPanel();
        }
        return true;
    }

    // Apply the selected CSS class to whichever video widget represents the currently-selected slot, or remove the highlight from all when no selection. The class adds a 2 px inset white outline so the user can see at a glance that the next input goes to that stream alone (vs. broadcast/sync when no widget is highlighted).
    private void UpdateSelectedVideoCss()
    {
        videoArea?.RemoveCssClass(SelectedVideoCssClass);
        videoView?.RemoveCssClass(SelectedVideoCssClass);
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
            selectedWidget = videoArea ?? (Gtk.Widget?)videoView;
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
        playlistPanel.Rebind(ctx.Playlist, ctx.PlayPlaylistItem);
    }

    private VideoSurface? GetTargetVideoSurfaceForDiagnostic()
    {
        if (viewModel.SelectedSlot == ViewModelMain.VideoSlot.Secondary && secondarySurface != null)
        {
            return secondarySurface;
        }
        return videoSurface;
    }

    // Re-add controlsBox to the overlay if it's already there (fullscreen). Since Gtk.Overlay renders overlay children in addition order — later AddOverlay = higher — we move controlsBox to the top of the stack so any newly-added PiP overlay child sits below the OSD. Idempotent: when controlsBox is in rootBox (windowed mode) this is a no-op.
    private void ReinsertControlsOverlay()
    {
        if (controlsBox.Parent != videoOverlay)
        {
            return;
        }
        videoOverlay.RemoveOverlay(controlsBox);
        videoOverlay.AddOverlay(controlsBox);
    }

    // Hooked to viewModel.PropertyChanged. The coordinator fires PropertyChanged for SelectedSlot / IsPipEnabled itself; we react by updating the CSS highlight + re-binding the playlist panel + syncing menu action sensitivities + syncing the toolbar selection. The diagnostic overlay re-resolves its providers every 1 Hz tick, so its data follows the target automatically without explicit poking here.
    private void OnViewModelPipPropertyChanged(object? sender, PropertyChangedEventArgs e)
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
            UpdateStreamSelectorVisibility();
            // Refresh labels on stream add/remove. EnablePip just brought Secondary into existence (its label may already need to read from a freshly-loaded file if EnablePip was triggered after a load); DisablePip just nulled it (the Secondary button reverts to "Video 2" fallback).
            UpdateStreamSelectorLabels();
            SyncStreamMenuActionSensitivity();
        }
    }

    // Phase 6 menu refactor will populate this with addStreamAction / deleteStreamAction sensitivity updates. Defined as an empty no-op for now so OnViewModelPipPropertyChanged compiles in advance of the menu rewrite.
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

    // Phase 4 will wire up the toolbar buttons and define real bodies; declared here so OnViewModelPipPropertyChanged compiles ahead of the toolbar build.
    private void UpdateStreamSelectorVisibility()
    {
        if (streamSelectorToolbar != null)
        {
            streamSelectorToolbar.SetVisible(viewModel.IsPipEnabled);
        }
    }

    private void SyncStreamSelectorButtons()
    {
        // Phase 4 fills this in. The body sets each button's Active state from viewModel.SelectedSlot with a re-entrancy guard.
        if (streamSelectorButtons == null)
        {
            return;
        }
        suppressSelectorToggleSignal = true;
        try
        {
            for (int i = 0; i < streamSelectorButtons.Length; i++)
            {
                var btn = streamSelectorButtons[i];
                if (btn == null)
                {
                    continue;
                }
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
}
