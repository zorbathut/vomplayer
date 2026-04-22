using System;

namespace Vomplayer;

public sealed partial class MainWindow
{
    // Menu bar construction. Gio.SimpleAction per item, registered on the window (ApplicationWindow implements Gio.ActionMap). Action names are "win."-prefixed in menu item detailed-action strings but registered bare on the window. Accelerators are app-wide via Gtk.Application.SetAccelsForAction — they're global for the app's lifetime, which is fine for this single-window NonUnique app. Space is intentionally NOT an accelerator here; see OnSpaceBubbleKey for why.
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

        var stopAction = Gio.SimpleAction.New("stop", null);
        stopAction.OnActivate += (_, _) => viewModel.StopCommand.Execute(null);
        AddAction(stopAction);

        var fullscreenAction = Gio.SimpleAction.New("fullscreen", null);
        fullscreenAction.OnActivate += (_, _) => SetFullscreen(!isFullscreen);
        AddAction(fullscreenAction);

        var aboutAction = Gio.SimpleAction.New("about", null);
        aboutAction.OnActivate += (_, _) => ShowAboutDialog();
        AddAction(aboutAction);

        // Stateful boolean action backing the Diagnostic Overlay checkbox. Gtk.PopoverMenuBar renders a check glyph automatically whenever the action's state is true; no menu-item attribute needed. GirCore's SimpleAction does NOT auto-apply the requested state on change-state — the handler must SetState explicitly, or the check glyph stays stuck on the previous value.
        var diagnosticAction = Gio.SimpleAction.NewStateful(
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

        // <Primary> resolves to Ctrl on Linux/Windows, Cmd on macOS — cross-platform correct per the project's stated target platforms.
        app.SetAccelsForAction("win.open", new[] { "<Primary>O" });
        app.SetAccelsForAction("win.quit", new[] { "<Primary>Q" });
        app.SetAccelsForAction("win.fullscreen", new[] { "F11" });

        var fileMenu = Gio.Menu.New();
        // GirCore 0.7.0 doesn't expose gtk_menu_append_item as AppendItem, only the position-based InsertItem. Passing -1 as position appends per the gmenu contract.
        fileMenu.InsertItem(-1, Gio.MenuItem.New("Open…", "win.open"));
        var fileQuitSection = Gio.Menu.New();
        fileQuitSection.InsertItem(-1, Gio.MenuItem.New("Quit", "win.quit"));
        fileMenu.AppendSection(null!, fileQuitSection);

        var playbackMenu = Gio.Menu.New();
        var playPauseItem = Gio.MenuItem.New("Play / Pause", "win.play-pause");
        // Space isn't registered via SetAccelsForAction — that would double-fire with focused Gtk.Buttons, which activate on Space. Instead the window's capture-phase key controller (OnWindowKeyPressed) claims Space before focused children see it. This attribute is a display-only hint so the menu shows an accelerator label; the actual dispatch happens in the key controller.
        playPauseItem.SetAttributeValue("accel", GLib.Variant.NewString("space"));
        playbackMenu.InsertItem(-1, playPauseItem);
        playbackMenu.InsertItem(-1, Gio.MenuItem.New("Stop", "win.stop"));

        var viewMenu = Gio.Menu.New();
        viewMenu.InsertItem(-1, Gio.MenuItem.New("Fullscreen", "win.fullscreen"));

        var helpMenu = Gio.Menu.New();
        helpMenu.InsertItem(-1, Gio.MenuItem.New("Diagnostic Overlay", "win.toggle-diagnostic"));
        helpMenu.InsertItem(-1, Gio.MenuItem.New("About", "win.about"));

        var root = Gio.Menu.New();
        root.AppendSubmenu("File", fileMenu);
        root.AppendSubmenu("Playback", playbackMenu);
        root.AppendSubmenu("View", viewMenu);
        root.AppendSubmenu("Help", helpMenu);

        var bar = Gtk.PopoverMenuBar.NewFromModel(root);
        // Without vom-chrome the bar's CSS node (which is `menubar`, not `popovermenubar` — GTK uses the legacy name) inherits the transparent main-window background and shows desktop through. The theme's default menubar fill exists but doesn't reliably cover in our setup. Explicit class makes the opaque fill unambiguous.
        bar.AddCssClass("vom-chrome");
        return bar;
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
}
