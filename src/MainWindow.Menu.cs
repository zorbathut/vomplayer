using System;
using System.Globalization;
using System.Linq;
using Vomplayer.Playback;
using Vomplayer.UserData;

namespace Vomplayer;

public sealed partial class MainWindow
{
    // Parent menus plus the index at which we re-insert each item — Gio.MenuItem is a snapshot, not live-bound to its parent, so updating the displayed accel means recreating the item and replacing it at the same slot. Recorded once during BuildMenuBar; RefreshMenuAccels iterates this without needing to know the menu structure.
    private (Gio.Menu Menu, int Index, string Label, string Action, HotkeyAction Hotkey)[] menuAccelSlots = Array.Empty<(Gio.Menu, int, string, string, HotkeyAction)>();

    // Subtitles submenu and its backing stateful action. The menu is rebuilt wholesale (RemoveAll + repopulate) on every PropertyChanged for SubtitleTracks/CurrentSubtitleId — small N (typically 0–10) makes diffing not worth the bookkeeping. The action is stateful with parameter type "s" because the radio glyph rendering requires per-item targets matched against the action's state ("none" or the stringified track id).
    private Gio.Menu? subtitleMenu;
    private Gio.SimpleAction? subtitleAction;
    // Sentinel state value for "no subtitle selected". Using "none" rather than the empty string keeps the action state human-readable in any future debug print and matches mpv's "no" symbolic value semantically.
    private const string SubtitleStateNone = "none";

    // Menu bar construction. Gio.SimpleAction per item, registered on the window (ApplicationWindow implements Gio.ActionMap). Menu items carry a display-only `accel` attribute; the actual key→action mapping is the window's capture-phase EventControllerKey driven by HotkeyMap. We intentionally do NOT call Gtk.Application.SetAccelsForAction — that would set up a parallel binding path competing with the HotkeyMap, and Space (bound to play_pause by default) would double-fire with focused Gtk.Buttons.
    private Gtk.PopoverMenuBar BuildMenuBar(Gtk.Application app)
    {
        var openAction = Gio.SimpleAction.New("open", null);
        openAction.OnActivate += (_, _) => viewModel.OpenCommand.Execute(null);
        AddAction(openAction);

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

        // Stateful string action backing the radio items in the Subtitles submenu. State holds the currently-selected track id stringified, or "none" when no track is selected. ChangeState fires when the user picks an item; we convert the target back to int? and route to viewModel.SelectSubtitle, then SetState (GirCore's SimpleAction does not auto-apply requested state — same caveat as toggle-diagnostic).
        subtitleAction = Gio.SimpleAction.NewStateful(
            "subtitle",
            parameterType: GLib.VariantType.New("s"),
            state: GLib.Variant.NewString(SubtitleStateNone));
        subtitleAction.OnChangeState += (_, args) =>
        {
            if (args.Value == null)
            {
                throw new InvalidOperationException("subtitle change-state signal fired with null args.Value");
            }
            string newState = args.Value.GetString(out var _length);
            subtitleAction.SetState(args.Value);
            int? trackId;
            if (newState == SubtitleStateNone)
            {
                trackId = null;
            }
            else if (int.TryParse(newState, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            {
                trackId = id;
            }
            else
            {
                Console.Error.WriteLine($"[vompl] subtitle: ignoring unparseable target '{newState}'");
                return;
            }
            viewModel.SelectSubtitle(trackId);
        };
        AddAction(subtitleAction);

        var addSubtitleAction = Gio.SimpleAction.New("add-subtitle", null);
        addSubtitleAction.OnActivate += (_, _) => viewModel.LoadSubtitleCommand.Execute(null);
        AddAction(addSubtitleAction);

        var preferencesAction = Gio.SimpleAction.New("preferences", null);
        preferencesAction.OnActivate += (_, _) => ShowHotkeysDialog();
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

        var fileMenu = Gio.Menu.New();
        // GirCore 0.7.0 doesn't expose gtk_menu_append_item as AppendItem, only the position-based InsertItem. Passing -1 as position appends per the gmenu contract.
        fileMenu.InsertItem(-1, Gio.MenuItem.New("Open…", "win.open"));
        var fileQuitSection = Gio.Menu.New();
        fileQuitSection.InsertItem(-1, Gio.MenuItem.New("Quit", "win.quit"));
        fileMenu.AppendSection(null!, fileQuitSection);

        var playbackMenu = Gio.Menu.New();
        playbackMenu.InsertItem(-1, Gio.MenuItem.New("Play / Pause", "win.play-pause"));

        // The submenu contents are rebuilt on every PropertyChanged for SubtitleTracks/CurrentSubtitleId — the parent submenu reference here is what AppendSubmenu wires into the menubar; we mutate its child items in place via RemoveAll + repopulate (Gio.Menu mutations propagate to the live PopoverMenuBar without rebinding).
        subtitleMenu = Gio.Menu.New();
        RebuildSubtitleMenu();

        var viewMenu = Gio.Menu.New();
        viewMenu.InsertItem(-1, Gio.MenuItem.New("Fullscreen", "win.fullscreen"));

        var editMenu = Gio.Menu.New();
        editMenu.InsertItem(-1, Gio.MenuItem.New("Preferences…", "win.preferences"));

        var helpMenu = Gio.Menu.New();
        helpMenu.InsertItem(-1, Gio.MenuItem.New("Diagnostic Overlay", "win.toggle-diagnostic"));
        helpMenu.InsertItem(-1, Gio.MenuItem.New("About", "win.about"));

        // Track the (parent menu, position) for each item that gets a hotkey-driven accel label. RefreshMenuAccels iterates this to rebuild items in place. Position 0 in each menu because every tracked item is the first (and often only) entry of its (sub)menu — the Quit accel slot is inside fileQuitSection, not fileMenu, so the position is still 0.
        menuAccelSlots = new[]
        {
            (fileMenu,       0, "Open…",              "win.open",              HotkeyAction.Open),
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
        root.AppendSubmenu("Subtitles", subtitleMenu);
        root.AppendSubmenu("View", viewMenu);
        root.AppendSubmenu("Help", helpMenu);

        var bar = Gtk.PopoverMenuBar.NewFromModel(root);
        // Without vompl-chrome the bar's CSS node (which is `menubar`, not `popovermenubar` — GTK uses the legacy name) inherits the transparent main-window background and shows desktop through. The theme's default menubar fill exists but doesn't reliably cover in our setup. Explicit class makes the opaque fill unambiguous.
        bar.AddCssClass("vompl-chrome");
        return bar;
    }

    // Rebuilds the Subtitles submenu from viewModel.SubtitleTracks + viewModel.CurrentSubtitleId. Called once during BuildMenuBar (after subtitleMenu and subtitleAction are constructed) and again on every PropertyChanged for either of those properties (wired in MainWindow.cs's OnViewModelPropertyChanged, which is subscribed AFTER BuildMenuBar runs). Throws if either field is null — that's a control-flow invariant, and silently no-op'ing here would hide a refactor that broke the constructor ordering (CLAUDE.md bans silent error handling). Layout: "None" radio item, one radio per sub track, separator, "Add Subtitle File…". The "None" item is always present so the user can always disable subs even when no tracks are listed. RemoveAll + repopulate is the documented Gio.Menu mutation pattern for wholesale rebuilds.
    private void RebuildSubtitleMenu()
    {
        if (subtitleMenu == null || subtitleAction == null)
        {
            throw new InvalidOperationException("RebuildSubtitleMenu invoked before BuildMenuBar constructed subtitleMenu/subtitleAction");
        }
        subtitleMenu.RemoveAll();

        var noneItem = Gio.MenuItem.New("None", null);
        noneItem.SetActionAndTargetValue("win.subtitle", GLib.Variant.NewString(SubtitleStateNone));
        subtitleMenu.AppendItem(noneItem);

        foreach (var track in viewModel.SubtitleTracks)
        {
            var item = Gio.MenuItem.New(FormatSubtitleLabel(track), null);
            item.SetActionAndTargetValue("win.subtitle", GLib.Variant.NewString(track.Id.ToString(CultureInfo.InvariantCulture)));
            subtitleMenu.AppendItem(item);
        }

        // Section break renders as a separator with no header text. AppendSection takes a MenuModel — a one-item Gio.Menu holding the "Add Subtitle File…" entry.
        var addSection = Gio.Menu.New();
        addSection.InsertItem(-1, Gio.MenuItem.New("Add Subtitle File…", "win.add-subtitle"));
        subtitleMenu.AppendSection(null!, addSection);

        // Sync the action's state to the current VM selection so the radio glyph lands on the right item. SetState here is purely visual — it doesn't fire ChangeState.
        subtitleAction.SetState(GLib.Variant.NewString(FormatSubtitleState(viewModel.CurrentSubtitleId)));
    }

    // Builds the human-readable label for a subtitle track. Track ids are stable per-source but meaningless to users, so the label leads with title or language; both null falls back to "Track #<id>". External tracks (loaded via sub-add) get an "(external)" suffix to distinguish from embedded ones — useful when a user has loaded a sidecar file alongside an embedded track of the same language.
    private static string FormatSubtitleLabel(SubtitleTrack track)
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

    private static string FormatSubtitleState(int? trackId)
    {
        return trackId.HasValue ? trackId.Value.ToString(CultureInfo.InvariantCulture) : SubtitleStateNone;
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

    private void ShowHotkeysDialog()
    {
        var dialog = new HotkeysDialog(this);
        dialog.Present();
    }
}
