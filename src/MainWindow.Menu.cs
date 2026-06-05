using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vomplayer.Playback;
using Vomplayer.UserData;
using Vomplayer.Util;
using Vomplayer.ViewModels;

namespace Vomplayer;

public sealed partial class MainWindow
{
    // Parent menus plus the index at which we re-insert each item — Gio.MenuItem is a snapshot, not live-bound to its parent, so updating the displayed accel means recreating the item and replacing it at the same slot. Recorded once during BuildMenuBar; RefreshMenuAccels iterates this without needing to know the menu structure.
    private (Gio.Menu Menu, int Index, string Label, string Action, HotkeyAction Hotkey)[] menuAccelSlots = Array.Empty<(Gio.Menu, int, string, string, HotkeyAction)>();

    // Per-kind track submenus and their backing stateful actions. Each menu is rebuilt wholesale (RemoveAll + repopulate) on every PropertyChanged for the matching track-list / current-id pair — small N (typically 0–10) makes diffing not worth the bookkeeping. Each action is stateful with parameter type "s" because the radio glyph rendering requires per-item targets matched against the action's state ("none" or the stringified track id).
    private Gio.Menu? videoMenu;
    private Gio.Menu? audioMenu;
    private Gio.Menu? subtitleMenu;
    private Gio.SimpleAction? videoAction;
    private Gio.SimpleAction? audioAction;
    private Gio.SimpleAction? subtitleAction;
    // File → Recent submenu. Surfaces saved-playlist entries (one row per autosaved session, identified by GUID, displayed by stored Title). Rebuilt wholesale via the autosave.Saved event so any save / Touch reorders the menu. Backed by a single parameterized action — each item carries its GUID stringified as the action target — so adding 10+ items doesn't pollute the action map.
    private Gio.Menu? recentMenu;
    // Cap on how many entries we pull from SQLite to feed the selector. The menu surfaces 10 with directory-coverage over the most-recent 5 distinct directories. 500 is generous enough that a binge-watch session of one season-folder (24 entries) doesn't bury other directories below the cutoff. The query is `LIMIT 500` against a `last_used_at`-indexed table — microseconds even on a multi-thousand-row history.
    private const int RecentMenuPullLimit = 500;
    private const int RecentMenuTotalSlots = 10;
    private const int RecentMenuDirectoryCoverageSlots = 5;
    // Sentinel state value for "no track selected" in any track-kind action. Using "none" rather than the empty string keeps the action state human-readable in any future debug print and matches mpv's "no" symbolic value semantically.
    private const string TrackStateNone = "none";

    // Menu bar construction. Gio.SimpleAction per item, registered on the window (ApplicationWindow implements Gio.ActionMap). Menu items carry a display-only `accel` attribute; the actual key→action mapping is the window's capture-phase EventControllerKey driven by HotkeyMap. We intentionally do NOT call Gtk.Application.SetAccelsForAction — that would set up a parallel binding path competing with the HotkeyMap, and Space (bound to play_pause by default) would double-fire with focused Gtk.Buttons.
    private Gtk.PopoverMenuBar BuildMenuBar(Gtk.Application app)
    {
        var openAction = Gio.SimpleAction.New("open", null);
        openAction.OnActivate += (_, _) => viewModel.OpenCommand.Execute(null);
        AddAction(openAction);

        var openUrlAction = Gio.SimpleAction.New("open-url", null);
        openUrlAction.OnActivate += (_, _) => viewModel.OpenUrlCommand.Execute(null);
        AddAction(openUrlAction);

        // Open/Save Playlist (File + Clipboard). The async handlers are fire-and-forget — each one
        // catches-and-reports its own exceptions internally (see MainWindow.PlaylistIo.cs), so the
        // discarded Task can never carry an unobserved exception. Save-to-clipboard is synchronous.
        var openPlaylistFileAction = Gio.SimpleAction.New("open-playlist-file", null);
        openPlaylistFileAction.OnActivate += (_, _) => { _ = OpenPlaylistFromFileAsync(); };
        AddAction(openPlaylistFileAction);

        var openPlaylistClipboardAction = Gio.SimpleAction.New("open-playlist-clipboard", null);
        openPlaylistClipboardAction.OnActivate += (_, _) => { _ = OpenPlaylistFromClipboardAsync(); };
        AddAction(openPlaylistClipboardAction);

        var savePlaylistFileAction = Gio.SimpleAction.New("save-playlist-file", null);
        savePlaylistFileAction.OnActivate += (_, _) => { _ = SavePlaylistToFileAsync(); };
        AddAction(savePlaylistFileAction);

        var savePlaylistClipboardAction = Gio.SimpleAction.New("save-playlist-clipboard", null);
        savePlaylistClipboardAction.OnActivate += (_, _) => SavePlaylistToClipboard();
        AddAction(savePlaylistClipboardAction);

        // Single parameterized action backing every Recent menu item — each menu item carries its playlist GUID stringified as the "s" target. One action vs. one-per-item keeps the action map size bounded across rebuilds.
        var openRecentAction = Gio.SimpleAction.New("open-recent-playlist", GLib.VariantType.New("s"));
        openRecentAction.OnActivate += (_, args) =>
        {
            if (args.Parameter == null)
            {
                throw new InvalidOperationException("open-recent-playlist activate signal fired with null Parameter");
            }
            string target = args.Parameter.GetString(out var _length);
            if (string.IsNullOrEmpty(target))
            {
                throw new InvalidOperationException("open-recent-playlist activate signal fired with empty target string");
            }
            if (!Guid.TryParseExact(target, "N", out var guid))
            {
                Console.Error.WriteLine($"[vompl] open-recent-playlist: ignoring unparseable GUID '{target}'");
                return;
            }
            OpenSavedPlaylist(guid);
        };
        AddAction(openRecentAction);

        var quitAction = Gio.SimpleAction.New("quit", null);
        quitAction.OnActivate += (_, _) => Close();
        AddAction(quitAction);

        var playPauseAction = Gio.SimpleAction.New("play-pause", null);
        playPauseAction.OnActivate += (_, _) => viewModel.PlayPauseCommand.Execute(null);
        AddAction(playPauseAction);

        var fullscreenAction = Gio.SimpleAction.New("fullscreen", null);
        fullscreenAction.OnActivate += (_, _) => SetFullscreen(!isFullscreen);
        AddAction(fullscreenAction);

        var aboutAction = Gio.SimpleAction.New("about", null);
        aboutAction.OnActivate += (_, _) => ShowAboutDialog();
        AddAction(aboutAction);

        // Stateful string actions backing the radio items in the per-kind track submenus. State holds the currently-selected track id stringified, or "none" when no track is selected. ChangeState fires when the user picks an item; the shared OnTrackActionChangeState handler converts the target back to int? and routes to the right VM Select method. Three actions exist because each submenu has its own state and rendering.
        videoAction = CreateTrackAction("video", viewModel.SelectVideo);
        audioAction = CreateTrackAction("audio", viewModel.SelectAudio);
        subtitleAction = CreateTrackAction("subtitle", viewModel.SelectSubtitle);

        var addAudioAction = Gio.SimpleAction.New("add-audio", null);
        addAudioAction.OnActivate += (_, _) => viewModel.LoadAudioCommand.Execute(null);
        AddAction(addAudioAction);

        var addSubtitleAction = Gio.SimpleAction.New("add-subtitle", null);
        addSubtitleAction.OnActivate += (_, _) => viewModel.LoadSubtitleCommand.Execute(null);
        AddAction(addSubtitleAction);

        var preferencesAction = Gio.SimpleAction.New("preferences", null);
        preferencesAction.OnActivate += (_, _) => ShowPreferencesDialog();
        AddAction(preferencesAction);

        // Stateful boolean action backing the Diagnostic Overlay checkbox. Gtk.PopoverMenuBar renders a check glyph automatically whenever the action's state is true; no menu-item attribute needed. GirCore's SimpleAction does NOT auto-apply the requested state on change-state — the handler must SetState explicitly, or the check glyph stays stuck on the previous value.
        diagnosticAction = Gio.SimpleAction.NewStateful(
            "toggle-diagnostic",
            parameterType: null,
            state: GLib.Variant.NewBoolean(false));
        diagnosticAction.OnChangeState += (_, args) =>
        {
            // args.Value is typed nullable on the GirCore side but the menu system always supplies a boolean GVariant for a stateful boolean action. A null here means GirCore's signal marshalling changed shape under us; fail loud rather than mask the bug.
            if (args.Value == null)
            {
                throw new InvalidOperationException("toggle-diagnostic change-state signal fired with null args.Value");
            }
            bool newState = args.Value.GetBoolean();
            diagnosticAction.SetState(args.Value);
            if (newState)
            {
                diagnosticOverlay.Show();
            }
            else
            {
                diagnosticOverlay.Hide();
            }
        };
        AddAction(diagnosticAction);

        // Stateful boolean action backing "View → Playlist". Same pattern as toggle-diagnostic. The auto-show path (OnWindowFileDrop after a multi-item drop) flips this state via ChangeState so the menu's check glyph stays in sync regardless of which path triggered visibility.
        playlistVisibleAction = Gio.SimpleAction.NewStateful(
            "toggle-playlist",
            parameterType: null,
            state: GLib.Variant.NewBoolean(false));
        playlistVisibleAction.OnChangeState += (_, args) =>
        {
            if (args.Value == null)
            {
                throw new InvalidOperationException("toggle-playlist change-state signal fired with null args.Value");
            }
            bool newState = args.Value.GetBoolean();
            playlistVisibleAction.SetState(args.Value);
            playlistPanel.Widget.SetVisible(newState);
        };
        AddAction(playlistVisibleAction);

        // View → Add Stream / Delete Stream. Activate handlers + sensitivity sync live inside PipController; here we just register the actions on the window's action map so the menu items can target them.
        var (addStreamAction, deleteStreamAction) = pipController.CreateStreamMenuActions();
        AddAction(addStreamAction);
        AddAction(deleteStreamAction);

        var fileMenu = Gio.Menu.New();
        // GirCore 0.7.0 doesn't expose gtk_menu_append_item as AppendItem, only the position-based InsertItem. Passing -1 as position appends per the gmenu contract.
        fileMenu.InsertItem(-1, Gio.MenuItem.New("Open File…", "win.open"));
        fileMenu.InsertItem(-1, Gio.MenuItem.New("Open URL…", "win.open-url"));
        // Open/Save Playlist submenus, each with a File and a Clipboard variant. Inserted AFTER Open
        // File…/Open URL… (positions 0/1) on purpose: menuAccelSlots addresses those two by fixed
        // position, so these must land at File-menu index >= 2 to leave the accel slots untouched.
        var openPlaylistMenu = Gio.Menu.New();
        openPlaylistMenu.InsertItem(-1, Gio.MenuItem.New("From File…", "win.open-playlist-file"));
        openPlaylistMenu.InsertItem(-1, Gio.MenuItem.New("From Clipboard", "win.open-playlist-clipboard"));
        fileMenu.AppendSubmenu("Open Playlist", openPlaylistMenu);
        var savePlaylistMenu = Gio.Menu.New();
        savePlaylistMenu.InsertItem(-1, Gio.MenuItem.New("To File…", "win.save-playlist-file"));
        savePlaylistMenu.InsertItem(-1, Gio.MenuItem.New("To Clipboard", "win.save-playlist-clipboard"));
        fileMenu.AppendSubmenu("Save Playlist", savePlaylistMenu);
        // Recent submenu placeholder — populated by RebuildRecentMenu and live-mutated by subsequent calls (Gio.Menu mutations propagate to the live PopoverMenuBar without rebinding). AppendSubmenu wires the menu by reference, so later RemoveAll/InsertItem calls flow through.
        recentMenu = Gio.Menu.New();
        fileMenu.AppendSubmenu("Recent", recentMenu);
        RebuildRecentMenu();
        var fileQuitSection = Gio.Menu.New();
        fileQuitSection.InsertItem(-1, Gio.MenuItem.New("Quit", "win.quit"));
        fileMenu.AppendSection(null!, fileQuitSection);

        var playbackMenu = Gio.Menu.New();
        playbackMenu.InsertItem(-1, Gio.MenuItem.New("Play / Pause", "win.play-pause"));

        // Per-kind submenu containers — populated by RebuildTrackMenu. The parent submenu references here are what AppendSubmenu wires into the menubar; we mutate their child items in place via RemoveAll + repopulate (Gio.Menu mutations propagate to the live PopoverMenuBar without rebinding).
        videoMenu = Gio.Menu.New();
        audioMenu = Gio.Menu.New();
        subtitleMenu = Gio.Menu.New();
        RebuildVideoMenu();
        RebuildAudioMenu();
        RebuildSubtitleMenu();

        var viewMenu = Gio.Menu.New();
        viewMenu.InsertItem(-1, Gio.MenuItem.New("Fullscreen", "win.fullscreen"));
        viewMenu.InsertItem(-1, Gio.MenuItem.New("Playlist", "win.toggle-playlist"));

        // PiP section. Section break renders as a separator between the existing items and the stream cluster — visually groups them. Two stateless commands: Add Stream creates the Secondary stream (PiP on); Delete Stream removes it. Sensitivities are mutually exclusive based on IsPipEnabled.
        var pipSection = Gio.Menu.New();
        pipSection.InsertItem(-1, Gio.MenuItem.New("Add Stream", "win.add-stream"));
        pipSection.InsertItem(-1, Gio.MenuItem.New("Delete Stream", "win.delete-stream"));
        viewMenu.AppendSection(null!, pipSection);

        var editMenu = Gio.Menu.New();
        editMenu.InsertItem(-1, Gio.MenuItem.New("Preferences…", "win.preferences"));

        var helpMenu = Gio.Menu.New();
        helpMenu.InsertItem(-1, Gio.MenuItem.New("Diagnostic Overlay", "win.toggle-diagnostic"));
        helpMenu.InsertItem(-1, Gio.MenuItem.New("About", "win.about"));

        // Track the (parent menu, position) for each item that gets a hotkey-driven accel label. RefreshMenuAccels iterates this to rebuild items in place. The Open File / Open URL pair sit at positions 0 and 1 in fileMenu; Quit lives in fileQuitSection at position 0; everything else in its own (sub)menu at position 0.
        menuAccelSlots = new[]
        {
            (fileMenu,       0, "Open File…",         "win.open",              HotkeyAction.Open),
            (fileMenu,       1, "Open URL…",          "win.open-url",          HotkeyAction.OpenUrl),
            (fileQuitSection,0, "Quit",               "win.quit",              HotkeyAction.Quit),
            (playbackMenu,   0, "Play / Pause",       "win.play-pause",        HotkeyAction.PlayPause),
            (viewMenu,       0, "Fullscreen",         "win.fullscreen",        HotkeyAction.ToggleFullscreen),
            (editMenu,       0, "Preferences…",       "win.preferences",       HotkeyAction.ShowPreferences),
            (helpMenu,       0, "Diagnostic Overlay", "win.toggle-diagnostic", HotkeyAction.ToggleDiagnosticOverlay),
        };
        RefreshMenuAccels();

        var root = Gio.Menu.New();
        root.AppendSubmenu("File", fileMenu);
        root.AppendSubmenu("Edit", editMenu);
        root.AppendSubmenu("Playback", playbackMenu);
        root.AppendSubmenu("Video", videoMenu);
        root.AppendSubmenu("Audio", audioMenu);
        root.AppendSubmenu("Subtitles", subtitleMenu);
        root.AppendSubmenu("View", viewMenu);
        root.AppendSubmenu("Help", helpMenu);

        var bar = Gtk.PopoverMenuBar.NewFromModel(root);
        // Without vompl-chrome the bar's CSS node (which is `menubar`, not `popovermenubar` — GTK uses the legacy name) inherits the transparent main-window background and shows desktop through. The theme's default menubar fill exists but doesn't reliably cover in our setup. Explicit class makes the opaque fill unambiguous.
        bar.AddCssClass("vompl-chrome");
        return bar;
    }

    // Factory for the per-kind stateful track action. Registers it on the window's ActionMap and wires the OnChangeState handler to parse the chosen target back to int? and dispatch through the supplied select callback. Initial state is "none" — RebuildTrackMenu re-syncs it whenever the VM's CurrentXxxId moves. GirCore's SimpleAction does not auto-apply requested state on change-state, so the handler explicitly SetState's (same caveat documented on diagnosticAction below).
    private Gio.SimpleAction CreateTrackAction(string actionName, Action<int?> select)
    {
        var action = Gio.SimpleAction.NewStateful(
            actionName,
            parameterType: GLib.VariantType.New("s"),
            state: GLib.Variant.NewString(TrackStateNone));
        action.OnChangeState += (_, args) =>
        {
            if (args.Value == null)
            {
                throw new InvalidOperationException($"{actionName} change-state signal fired with null args.Value");
            }
            string newState = args.Value.GetString(out var _length);
            action.SetState(args.Value);
            int? trackId;
            if (newState == TrackStateNone)
            {
                trackId = null;
            }
            else if (int.TryParse(newState, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            {
                trackId = id;
            }
            else
            {
                Console.Error.WriteLine($"[vompl] {actionName}: ignoring unparseable target '{newState}'");
                return;
            }
            select(trackId);
        };
        AddAction(action);
        return action;
    }

    // Rebuild the File → Recent submenu from the saved-playlists store. Pulls a generous candidate set (RecentMenuPullLimit), runs PlaylistMenuSelector to enforce the directory-coverage rule, and writes one menu item per result. Empty store ⇒ a single disabled "(no recent playlists)" placeholder so the submenu opens with a hint rather than looking broken. Called from BuildMenuBar at construction; re-called by the autosave.Saved event subscription wired in MainWindow.cs.
    private void RebuildRecentMenu()
    {
        if (recentMenu == null)
        {
            throw new InvalidOperationException("RebuildRecentMenu invoked before BuildMenuBar constructed recentMenu");
        }
        recentMenu.RemoveAll();

        // savedPlaylists may be null in the brief window between MainWindow ctor and the AttachAutosave / construction-side wiring — render the placeholder rather than crash.
        if (savedPlaylists == null)
        {
            recentMenu.InsertItem(-1, Gio.MenuItem.New("(no recent playlists)", "win.noop-recent-empty"));
            return;
        }
        var entries = savedPlaylists.GetMostRecent(RecentMenuPullLimit);
        var selected = PlaylistMenuSelector.Select(entries, RecentMenuTotalSlots, RecentMenuDirectoryCoverageSlots);
        if (selected.Count == 0)
        {
            // Disabled placeholder. Pointing the item at a non-existent action is the standard Gio dance for "not enabled" — Gtk renders the item greyed out because no action with that name is registered.
            recentMenu.InsertItem(-1, Gio.MenuItem.New("(no recent playlists)", "win.noop-recent-empty"));
            return;
        }

        foreach (var entry in selected)
        {
            // Empty title falls back to a placeholder rather than rendering a blank menu item. Doesn't normally happen because Save filters all-empty playlists out, but defensive — a row whose title was never bumped past the empty-string default would be invisible in the menu otherwise.
            string label = string.IsNullOrEmpty(entry.Title) ? "(untitled playlist)" : entry.Title;
            var item = Gio.MenuItem.New(label, null);
            item.SetActionAndTargetValue("win.open-recent-playlist", GLib.Variant.NewString(entry.Guid.ToString("N")));
            recentMenu.InsertItem(-1, item);
        }
    }

    // Per-kind rebuild entrypoints. Three thin wrappers — each one passes its kind-specific knobs to RebuildTrackMenu and, where applicable, tacks on a separator + "Add … File…" section. Called once during BuildMenuBar (after the menu/action fields are constructed) and again on every PropertyChanged for the matching track-list / current-id pair (wired in MainWindow.cs's OnViewModelPropertyChanged, which is subscribed AFTER BuildMenuBar runs).
    private void RebuildVideoMenu()
    {
        // allowNone:false — `vid=no` freezes the last decoded frame with no recovery UX (subsurface stays allocated, no audio-only mode), so we don't expose the "off" choice. Multi-video sources can switch directly between video tracks without going through "None". No "Add Video File…" entry either — multi-angle external video is rare enough not to justify the surface; an external video would arrive via the command line or scripted entry and still appear in the track list.
        RebuildTrackMenu(videoMenu, videoAction, "win.video", allowNone: false, viewModel.VideoTracks, viewModel.CurrentVideoId);
    }

    private void RebuildAudioMenu()
    {
        // allowNone:true — disabling audio (aid=no) is a benign UX outcome: the video keeps playing and the user can re-enable any track from the same menu.
        RebuildTrackMenu(audioMenu, audioAction, "win.audio", allowNone: true, viewModel.AudioTracks, viewModel.CurrentAudioId);
        AppendAddTrackSection(audioMenu!, "Add Audio File…", "win.add-audio");
    }

    private void RebuildSubtitleMenu()
    {
        RebuildTrackMenu(subtitleMenu, subtitleAction, "win.subtitle", allowNone: true, viewModel.SubtitleTracks, viewModel.CurrentSubtitleId);
        AppendAddTrackSection(subtitleMenu!, "Add Subtitle File…", "win.add-subtitle");
    }

    // Wholesale rebuild of one track-kind's submenu. Layout: optional "None" radio item, one radio per track in the list. Wrappers append any add-section themselves after this returns. RemoveAll + repopulate is the documented Gio.Menu mutation pattern for wholesale rebuilds. Throws if the menu/action haven't been constructed yet — that's a control-flow invariant (BuildMenuBar runs them before any rebuild can fire) and silently no-op'ing here would hide a refactor that broke the constructor ordering (CLAUDE.md bans silent error handling).
    private static void RebuildTrackMenu(Gio.Menu? menu, Gio.SimpleAction? action, string detailedActionName, bool allowNone, IReadOnlyList<MediaTrack> tracks, int? currentId)
    {
        if (menu == null || action == null)
        {
            throw new InvalidOperationException("RebuildTrackMenu invoked before BuildMenuBar constructed menu/action");
        }
        menu.RemoveAll();

        if (allowNone)
        {
            var noneItem = Gio.MenuItem.New("None", null);
            noneItem.SetActionAndTargetValue(detailedActionName, GLib.Variant.NewString(TrackStateNone));
            menu.AppendItem(noneItem);
        }

        foreach (var track in tracks)
        {
            var item = Gio.MenuItem.New(FormatTrackLabel(track), null);
            item.SetActionAndTargetValue(detailedActionName, GLib.Variant.NewString(track.Id.ToString(CultureInfo.InvariantCulture)));
            menu.AppendItem(item);
        }

        // Sync the action's state to the current VM selection so the radio glyph lands on the right item. SetState here is purely visual — it doesn't fire ChangeState.
        action.SetState(GLib.Variant.NewString(FormatTrackState(currentId)));
    }

    // Section break renders as a separator with no header text. AppendSection takes a MenuModel — a one-item Gio.Menu holding the "Add … File…" entry.
    private static void AppendAddTrackSection(Gio.Menu menu, string label, string detailedAction)
    {
        var addSection = Gio.Menu.New();
        addSection.InsertItem(-1, Gio.MenuItem.New(label, detailedAction));
        menu.AppendSection(null!, addSection);
    }

    // Builds the human-readable label for a track. Track ids are stable per-source but meaningless to users, so the label leads with title or language; both null falls back to "Track #<id>". External tracks (loaded via *-add) get an "(external)" suffix to distinguish from embedded ones — useful when a user has loaded a sidecar file alongside an embedded track of the same language.
    private static string FormatTrackLabel(MediaTrack track)
    {
        string main;
        if (!string.IsNullOrEmpty(track.Title) && !string.IsNullOrEmpty(track.Lang))
        {
            main = $"{track.Title} ({track.Lang})";
        }
        else if (!string.IsNullOrEmpty(track.Title))
        {
            main = track.Title;
        }
        else if (!string.IsNullOrEmpty(track.Lang))
        {
            main = track.Lang;
        }
        else
        {
            main = $"Track #{track.Id}";
        }
        return track.External ? $"{main} (external)" : main;
    }

    private static string FormatTrackState(int? trackId)
    {
        return trackId.HasValue ? trackId.Value.ToString(CultureInfo.InvariantCulture) : TrackStateNone;
    }

    // Refresh the accel labels on tracked menu items after the hotkey map changes. Gio.MenuItem isn't bound live to its parent — to update the displayed accel we re-create the item with the new attribute and replace the parent's slot. Without this, the displayed shortcuts in the menu would lag behind the user's edits in the preferences dialog. Picks the first Key trigger bound to the action; mouse triggers are skipped because the menu's accel label can't render "double-click" sensibly. If no key binding exists, the item gets no accel attribute and renders as a plain row.
    internal void RefreshMenuAccels()
    {
        foreach (var slot in menuAccelSlots)
        {
            var item = Gio.MenuItem.New(slot.Label, slot.Action);
            var accel = hotkeys.Get(slot.Hotkey).OfType<Trigger.Key>().FirstOrDefault()?.Format();
            if (accel != null)
            {
                item.SetAttributeValue("accel", GLib.Variant.NewString(accel));
            }
            // Gio.Menu has no Replace API in GirCore 0.7.0; remove + insert at the same position is the documented dance.
            slot.Menu.Remove(slot.Index);
            slot.Menu.InsertItem(slot.Index, item);
        }
    }

    // Minimal About window. Gtk.AboutDialog is deprecated in GTK 4.10+; a plain Gtk.Window with a couple of widgets avoids the deprecation and keeps us in the code-only-GTK style the rest of the UI uses.
    private void ShowAboutDialog()
    {
        var dialog = Gtk.Window.New();
        dialog.Title = "About Vomplayer";
        dialog.SetTransientFor(this);
        dialog.SetModal(true);
        dialog.SetDefaultSize(320, 160);

        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        box.SetMarginTop(16);
        box.SetMarginBottom(16);
        box.SetMarginStart(16);
        box.SetMarginEnd(16);

        var nameLabel = Gtk.Label.New("Vomplayer");
        nameLabel.AddCssClass("title-2");
        var blurbLabel = Gtk.Label.New("Cross-platform video player\nbuilt on GTK4 and libmpv.");
        blurbLabel.SetJustify(Gtk.Justification.Center);
        var licenseLabel = Gtk.Label.New("MIT License");

        var closeButton = Gtk.Button.NewWithLabel("Close");
        closeButton.SetHalign(Gtk.Align.Center);
        closeButton.OnClicked += (_, _) => dialog.Close();

        box.Append(nameLabel);
        box.Append(blurbLabel);
        box.Append(licenseLabel);
        box.Append(closeButton);
        dialog.SetChild(box);
        dialog.Present();
    }

    private void ShowPreferencesDialog()
    {
        var dialog = new PreferencesDialog(this);
        dialog.Present();
    }
}
