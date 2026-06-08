using System;
using Vomplayer.UserData;

namespace Vomplayer;

// Modal preferences dialog. Two tabs in a Gtk.Notebook: "General" (theme dropdown + single-instance checkbox) and "Hotkeys" (the per-action shortcut grid). Snapshots the live state on open; mutations are local until the user clicks Save, at which point MainWindow.ApplyPreferences swaps the runtime map, applies the theme live, persists both sections to TOML, and refreshes menu accelerators. Cancel discards the snapshot.
//
// Hotkeys tab layout: a true spreadsheet via Gtk.Grid wrapped in Gtk.ScrolledWindow. Row 0 is the header (Action, Shortcut 1, Shortcut 2, …). Each subsequent row is one action: action label in column 0, then one cell per binding slot, then trailing empty cells the user can click to add new bindings. Column count is recomputed on each rebuild as `max(MinSlots, max_bindings_across_actions + 1)` so there's always at least one trailing empty cell on every row, but the grid never grows unboundedly: a user with 8 bindings on one action makes the dialog wide, by design — the alternative (truncating with a "more…" indicator) hides what's bound.
//
// Capture flow: click any cell, including an empty one. The cell becomes armed and shows "Press a key…". The status bar at the bottom appears with a Mouse-trigger menu and instructions. Press a key to bind; pick from the Mouse menu to bind a mouse click; press Delete (in an armed *occupied* cell) to clear that slot; press Escape to cancel. Esc-as-a-binding remains reachable via hand-editing config.toml — the default ExitFullscreen ships that way. Cross-action conflicts are resolved by HotkeyMap.Bind, which removes the trigger from any other action it was previously assigned to.
internal sealed class PreferencesDialog : Gtk.Window
{
    // Minimum number of shortcut columns in the grid even when no action has any binding. Keeps the table from feeling claustrophobic on a fresh "all empty" config.
    private const int MinSlots = 4;

    private readonly MainWindow owner;
    private HotkeyMap editing;

    // Capture state. capturingFor + slotIndex describe what cell is being edited.
    //   capturingFor == null → idle.
    //   capturingFor != null, slotIndex < bindings.Count → "Replace" mode (next event replaces that binding; Delete clears it).
    //   capturingFor != null, slotIndex >= bindings.Count → "Add" mode (next event appends; Delete is a no-op).
    private HotkeyAction? capturingFor;
    private int slotIndex;

    private readonly Gtk.Grid grid;
    private readonly Gtk.Box statusBar;
    private readonly Gtk.Label statusLabel;
    private readonly Gtk.MenuButton mouseMenuButton;
    private readonly Gtk.CheckButton singleInstanceCheck;
    private readonly Gtk.DropDown themeDropDown;

    // Dropdown row order. Indices are the wire contract between the DropDown's `Selected` uint and ThemeMode — keep aligned with the labels passed to NewFromStrings below.
    private static readonly ThemeMode[] ThemeOrder = { ThemeMode.Auto, ThemeMode.Light, ThemeMode.Dark };

    public PreferencesDialog(MainWindow owner) : base()
    {
        if (owner == null)
        {
            throw new ArgumentNullException(nameof(owner));
        }
        this.owner = owner;
        this.editing = owner.GetHotkeysSnapshot();

        Title = "Preferences";
        SetTransientFor(owner);
        SetModal(true);
        SetDefaultSize(720, 540);

        var outerBox = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        SetChild(outerBox);

        var notebook = Gtk.Notebook.New();
        notebook.SetVexpand(true);
        notebook.SetHexpand(true);
        outerBox.Append(notebook);

        // General tab — app-level settings: theme dropdown and the single-instance checkbox.
        var generalPage = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        generalPage.SetMarginStart(12);
        generalPage.SetMarginEnd(12);
        generalPage.SetMarginTop(12);
        generalPage.SetMarginBottom(12);

        // Theme row — applies instantly on Save (no next-launch note, unlike single-instance below). Label + dropdown on one line.
        var themeRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        var themeLabel = Gtk.Label.New("Theme:");
        themeLabel.SetXalign(0);
        themeRow.Append(themeLabel);
        themeDropDown = Gtk.DropDown.NewFromStrings(new[] { "Auto", "Light", "Dark" });
        themeDropDown.SetSelected((uint)Array.IndexOf(ThemeOrder, owner.GetThemePreference()));
        themeRow.Append(themeDropDown);
        generalPage.Append(themeRow);

        singleInstanceCheck = Gtk.CheckButton.NewWithLabel("Open files in the running window when launching from the command line");
        singleInstanceCheck.SetActive(owner.GetSingleInstancePreference());
        generalPage.Append(singleInstanceCheck);

        var singleInstanceNote = Gtk.Label.New("Changes apply on next launch.");
        singleInstanceNote.AddCssClass("dim-label");
        singleInstanceNote.SetXalign(0);
        singleInstanceNote.SetMarginStart(24);
        generalPage.Append(singleInstanceNote);

        notebook.AppendPage(generalPage, Gtk.Label.New("General"));

        // Hotkeys tab.
        var hotkeysPage = Gtk.Box.New(Gtk.Orientation.Vertical, 0);

        grid = Gtk.Grid.New();
        grid.AddCssClass("vompl-hotkey-grid");
        grid.SetRowHomogeneous(false);
        grid.SetColumnHomogeneous(false);

        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetChild(grid);
        scroll.SetVexpand(true);
        scroll.SetHexpand(true);
        scroll.SetMarginStart(12);
        scroll.SetMarginEnd(12);
        scroll.SetMarginTop(12);
        scroll.SetMarginBottom(0);
        hotkeysPage.Append(scroll);

        // Status bar — visible only while capturing. Status text on the left, Mouse menu and Cancel on the right.
        statusBar = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        statusBar.SetMarginStart(12);
        statusBar.SetMarginEnd(12);
        statusBar.SetMarginTop(8);
        statusBar.SetMarginBottom(4);

        statusLabel = Gtk.Label.New("");
        statusLabel.SetXalign(0);
        statusLabel.SetHexpand(true);
        statusBar.Append(statusLabel);

        mouseMenuButton = Gtk.MenuButton.New();
        mouseMenuButton.SetLabel("Mouse…");
        mouseMenuButton.SetTooltipText("Bind a mouse click instead of a key");
        mouseMenuButton.SetPopover(BuildMousePopover());
        statusBar.Append(mouseMenuButton);

        var cancelCaptureButton = Gtk.Button.NewWithLabel("Cancel");
        cancelCaptureButton.OnClicked += (_, _) => EndCapture();
        statusBar.Append(cancelCaptureButton);

        hotkeysPage.Append(statusBar);

        // Restore-Defaults lives inside the Hotkeys tab — it only applies to hotkeys, so it would be misleading sitting next to Save/Cancel which apply to both sections.
        var hotkeyDefaultsRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        hotkeyDefaultsRow.SetMarginStart(12);
        hotkeyDefaultsRow.SetMarginEnd(12);
        hotkeyDefaultsRow.SetMarginTop(4);
        hotkeyDefaultsRow.SetMarginBottom(8);

        var defaultsButton = Gtk.Button.NewWithLabel("Restore Defaults");
        defaultsButton.OnClicked += (_, _) =>
        {
            editing = HotkeyMap.Default();
            EndCapture();
        };
        hotkeyDefaultsRow.Append(defaultsButton);
        hotkeysPage.Append(hotkeyDefaultsRow);

        notebook.AppendPage(hotkeysPage, Gtk.Label.New("Hotkeys"));

        // Bottom button row — applies to the whole dialog.
        var buttonRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        buttonRow.SetMarginStart(12);
        buttonRow.SetMarginEnd(12);
        buttonRow.SetMarginTop(6);
        buttonRow.SetMarginBottom(12);

        var spacer = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        spacer.SetHexpand(true);
        buttonRow.Append(spacer);

        var cancelButton = Gtk.Button.NewWithLabel("Cancel");
        cancelButton.OnClicked += (_, _) => Close();
        buttonRow.Append(cancelButton);

        var saveButton = Gtk.Button.NewWithLabel("Save");
        saveButton.AddCssClass("suggested-action");
        saveButton.OnClicked += (_, _) =>
        {
            // Selected is always 0..2 here: the model has three rows and is never deselected, so it can't be GTK_INVALID_LIST_POSITION.
            owner.ApplyPreferences(editing.Clone(), singleInstanceCheck.Active, ThemeOrder[themeDropDown.Selected]);
            Close();
        };
        buttonRow.Append(saveButton);

        outerBox.Append(buttonRow);

        // Capture-phase key controller — preempts focused widgets when capturing. When idle, only Esc-to-close is intercepted; everything else propagates so dialog buttons keep their normal keyboard activation.
        var keyController = Gtk.EventControllerKey.New();
        keyController.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        keyController.OnKeyPressed += OnKeyPressed;
        AddController(keyController);

        InstallCss();
        RebuildGrid();
    }

    private Gtk.Popover BuildMousePopover()
    {
        var popover = Gtk.Popover.New();
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        box.AddCssClass("vompl-mouse-popover");

        AddMouseChoice(box, popover, "Click — Left",          button: 1, clickCount: 1);
        AddMouseChoice(box, popover, "Double-click — Left",   button: 1, clickCount: 2);
        AddMouseChoice(box, popover, "Click — Middle",        button: 2, clickCount: 1);
        AddMouseChoice(box, popover, "Double-click — Middle", button: 2, clickCount: 2);
        AddMouseChoice(box, popover, "Click — Right",         button: 3, clickCount: 1);
        AddMouseChoice(box, popover, "Double-click — Right",  button: 3, clickCount: 2);

        popover.SetChild(box);
        return popover;
    }

    private void AddMouseChoice(Gtk.Box parent, Gtk.Popover popover, string label, uint button, int clickCount)
    {
        var b = Gtk.Button.NewWithLabel(label);
        b.AddCssClass("vompl-mouse-choice");
        b.SetHalign(Gtk.Align.Fill);
        b.OnClicked += (_, _) =>
        {
            popover.Popdown();
            CommitBinding(new Trigger.MouseClick(button, clickCount));
        };
        parent.Append(b);
    }

    // Compute how many shortcut columns the grid needs. Always at least MinSlots, and always one more than the largest bound row so every row has at least one trailing empty cell to click for adding a new binding.
    private int ComputeSlotCount()
    {
        int max = 0;
        foreach (var action in HotkeyMap.AllActions)
        {
            int n = editing.Get(action).Count;
            if (n > max)
            {
                max = n;
            }
        }
        int needed = max + 1;
        return needed > MinSlots ? needed : MinSlots;
    }

    // Tear down and rebuild from scratch on every change. Gtk.Grid has no clean "swap children" API in GirCore 0.7.0; iterating GetFirstChild + Remove is the documented dance. With ~7 actions × ~5 cells the cost is trivial, and it keeps the column count, capture-armed cell highlight, and Delete-vs-no-op affordances all derivable from a single rebuild pass.
    private void RebuildGrid()
    {
        var child = grid.GetFirstChild();
        while (child != null)
        {
            var next = child.GetNextSibling();
            grid.Remove(child);
            child = next;
        }

        int slotCount = ComputeSlotCount();

        // Header row.
        grid.Attach(BuildHeaderCell("Action"), column: 0, row: 0, width: 1, height: 1);
        for (int s = 0; s < slotCount; s++)
        {
            grid.Attach(BuildHeaderCell($"Shortcut {s + 1}"), column: s + 1, row: 0, width: 1, height: 1);
        }

        // Action rows.
        int rowIndex = 1;
        foreach (var action in HotkeyMap.AllActions)
        {
            grid.Attach(BuildActionLabelCell(action), column: 0, row: rowIndex, width: 1, height: 1);
            var bindings = editing.Get(action);
            for (int s = 0; s < slotCount; s++)
            {
                grid.Attach(BuildShortcutCell(action, s, s < bindings.Count ? bindings[s] : null),
                    column: s + 1, row: rowIndex, width: 1, height: 1);
            }
            rowIndex++;
        }

        UpdateStatusBar();
    }

    private Gtk.Widget BuildHeaderCell(string text)
    {
        var label = Gtk.Label.New(text);
        label.AddCssClass("vompl-hotkey-cell");
        label.AddCssClass("vompl-hotkey-header");
        label.SetXalign(0);
        return label;
    }

    private Gtk.Widget BuildActionLabelCell(HotkeyAction action)
    {
        var label = Gtk.Label.New(HotkeyMap.ActionDisplayName(action));
        label.AddCssClass("vompl-hotkey-cell");
        label.AddCssClass("vompl-hotkey-action");
        label.SetXalign(0);
        return label;
    }

    // A shortcut cell is a Gtk.Button styled flat so it looks like a spreadsheet cell rather than a button. Empty cells render with no label (hint shown via tooltip). Armed cells (the one currently being captured) render the prompt and an accent background. The button click activates the cell — entering capture mode for that (action, slot).
    private Gtk.Widget BuildShortcutCell(HotkeyAction action, int slot, Trigger? trigger)
    {
        bool armed = capturingFor == action && slotIndex == slot;
        string text;
        if (armed)
        {
            text = "Press a key…";
        }
        else if (trigger == null)
        {
            text = "";
        }
        else
        {
            text = FormatTriggerForDisplay(trigger);
        }

        var button = Gtk.Button.NewWithLabel(text);
        button.AddCssClass("vompl-hotkey-cell");
        button.AddCssClass("vompl-hotkey-shortcut");
        button.AddCssClass("flat");
        if (armed)
        {
            button.AddCssClass("vompl-hotkey-armed");
        }
        else if (trigger == null)
        {
            button.AddCssClass("vompl-hotkey-empty");
        }
        button.SetTooltipText(armed
            ? "Press a key, or use the Mouse menu. Press Delete to clear, Esc to cancel."
            : (trigger == null
                ? "Click to add a shortcut here"
                : "Click to rebind. Press Delete after clicking to clear."));
        button.SetHalign(Gtk.Align.Fill);
        button.OnClicked += (_, _) => BeginCapture(action, slot);
        return button;
    }

    private static string FormatTriggerForDisplay(Trigger t)
    {
        if (t is Trigger.MouseClick mc)
        {
            string buttonName;
            switch (mc.Button)
            {
                case 1: buttonName = "Left"; break;
                case 2: buttonName = "Middle"; break;
                case 3: buttonName = "Right"; break;
                default: buttonName = $"Button {mc.Button}"; break;
            }
            if (mc.ClickCount >= 2)
            {
                return $"Dbl-click {buttonName}";
            }
            return $"Click {buttonName}";
        }
        return t.Format();
    }

    private void BeginCapture(HotkeyAction action, int slot)
    {
        capturingFor = action;
        slotIndex = slot;
        RebuildGrid();
    }

    private void EndCapture()
    {
        capturingFor = null;
        slotIndex = 0;
        RebuildGrid();
    }

    private void UpdateStatusBar()
    {
        if (capturingFor.HasValue)
        {
            var bindings = editing.Get(capturingFor.Value);
            string slotKind = slotIndex < bindings.Count ? "Replacing slot" : "Adding to";
            statusLabel.SetLabel($"{slotKind} “{HotkeyMap.ActionDisplayName(capturingFor.Value)}”: press a key, use Mouse…, press Delete to clear, Esc to cancel.");
            statusBar.SetVisible(true);
        }
        else
        {
            statusLabel.SetLabel("");
            statusBar.SetVisible(false);
        }
    }

    private bool OnKeyPressed(Gtk.EventControllerKey sender, Gtk.EventControllerKey.KeyPressedSignalArgs args)
    {
        if (!capturingFor.HasValue)
        {
            // Idle: Esc closes the dialog. Other keys propagate so dialog buttons keep their normal keyboard behavior.
            if (args.Keyval == (uint)Gdk.Constants.KEY_Escape)
            {
                Close();
                return true;
            }
            return false;
        }
        // Capturing: Esc cancels without binding. Esc-as-a-binding is reachable by hand-editing config.toml — the default ExitFullscreen ships that way. Without an Esc-cancel, the only abort path is the status-bar Cancel button, which is awkward when the user's hands are already on the keyboard.
        if (args.Keyval == (uint)Gdk.Constants.KEY_Escape)
        {
            EndCapture();
            return true;
        }
        // Delete clears the slot if it's currently bound. For an empty slot, Delete is a no-op (nothing to clear) and capture stays armed so the user can still bind something.
        if (args.Keyval == (uint)Gdk.Constants.KEY_Delete || args.Keyval == (uint)Gdk.Constants.KEY_BackSpace)
        {
            ClearArmedSlot();
            return true;
        }
        // Modifier-only keys (Shift / Ctrl / Alt / Meta on their own) don't make useful bindings. Skip and keep capturing so the user can hold a modifier and then press the actual key.
        if (IsModifierKey(args.Keyval))
        {
            return true;
        }
        var trigger = Trigger.MakeKey(args.Keyval, args.State);
        CommitBinding(trigger);
        return true;
    }

    private void ClearArmedSlot()
    {
        if (!capturingFor.HasValue)
        {
            return;
        }
        var action = capturingFor.Value;
        var bindings = editing.Get(action);
        if (slotIndex < bindings.Count)
        {
            editing.Remove(action, bindings[slotIndex]);
        }
        EndCapture();
    }

    private void CommitBinding(Trigger trigger)
    {
        if (!capturingFor.HasValue)
        {
            return;
        }
        var action = capturingFor.Value;
        var bindings = editing.Get(action);
        if (slotIndex < bindings.Count)
        {
            // Replace mode: drop the old binding at this slot first so the new one lands in roughly the same column position after Bind appends. Without the explicit Remove, Bind would append the new trigger to the end and the old one would still be there.
            editing.Remove(action, bindings[slotIndex]);
        }
        // Bind atomically removes the trigger from any other action that owned it.
        editing.Bind(action, trigger);
        EndCapture();
    }

    private static bool IsModifierKey(uint keyval)
    {
        return keyval == (uint)Gdk.Constants.KEY_Shift_L
            || keyval == (uint)Gdk.Constants.KEY_Shift_R
            || keyval == (uint)Gdk.Constants.KEY_Control_L
            || keyval == (uint)Gdk.Constants.KEY_Control_R
            || keyval == (uint)Gdk.Constants.KEY_Alt_L
            || keyval == (uint)Gdk.Constants.KEY_Alt_R
            || keyval == (uint)Gdk.Constants.KEY_Super_L
            || keyval == (uint)Gdk.Constants.KEY_Super_R
            || keyval == (uint)Gdk.Constants.KEY_Meta_L
            || keyval == (uint)Gdk.Constants.KEY_Meta_R
            || keyval == (uint)Gdk.Constants.KEY_Hyper_L
            || keyval == (uint)Gdk.Constants.KEY_Hyper_R;
    }

    private static bool cssInstalled;
    private static void InstallCss()
    {
        if (cssInstalled)
        {
            return;
        }
        cssInstalled = true;
        var provider = Gtk.CssProvider.New();
        // Spreadsheet styling. Per-cell borders on the right + bottom only, plus a left + top border on the grid container, gives a single-line cell separator without double-thick edges. Header row gets bold text and a slightly stronger background. Empty cells stay subtle (no foreground content), and the armed cell gets the accent color so it's clear which one is active.
        provider.LoadFromString(
            ".vompl-hotkey-grid { border-top: 1px solid alpha(@theme_fg_color, 0.30); border-left: 1px solid alpha(@theme_fg_color, 0.30); }" +
            ".vompl-hotkey-cell { border-right: 1px solid alpha(@theme_fg_color, 0.30); border-bottom: 1px solid alpha(@theme_fg_color, 0.30); padding: 6px 10px; min-height: 26px; }" +
            ".vompl-hotkey-header { font-weight: bold; background-color: alpha(@theme_fg_color, 0.06); }" +
            ".vompl-hotkey-action { background-color: alpha(@theme_fg_color, 0.03); }" +
            ".vompl-hotkey-shortcut { background: transparent; min-width: 110px; }" +
            ".vompl-hotkey-shortcut:hover { background-color: alpha(@theme_fg_color, 0.10); }" +
            ".vompl-hotkey-empty label { color: alpha(@theme_fg_color, 0.45); }" +
            ".vompl-hotkey-armed { background-color: alpha(@accent_bg_color, 0.30); }" +
            ".vompl-hotkey-armed:hover { background-color: alpha(@accent_bg_color, 0.40); }" +
            ".vompl-mouse-popover button { padding: 6px 10px; }");
        var display = Gdk.Display.GetDefault();
        if (display != null)
        {
            Gtk.StyleContext.AddProviderForDisplay(display, provider, (uint)Gtk.Constants.STYLE_PROVIDER_PRIORITY_APPLICATION);
        }
    }
}
