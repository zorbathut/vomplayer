using System;
using System.Collections.Generic;
using Vomplayer.UserData;

namespace Vomplayer;

// Modal preferences dialog. Two tabs in a Gtk.Notebook: "General" (theme, open-in-new-window, chapter-seek preroll, yt-dlp cookie source) and "Hotkeys" (the per-action shortcut grid). Snapshots the live state on open; mutations are local until the user clicks Save, at which point MainWindow.ApplyPreferences swaps the runtime map, applies the theme live, persists both sections to TOML, and refreshes menu accelerators. Cancel discards the snapshot.
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

    private readonly Gtk.Notebook notebook;
    private readonly Gtk.Grid grid;
    private readonly Gtk.Box statusBar;
    private readonly Gtk.Label statusLabel;
    private readonly Gtk.MenuButton mouseMenuButton;
    private readonly Gtk.CheckButton openInNewWindowCheck;
    private readonly Gtk.DropDown themeDropDown;
    private readonly Gtk.SpinButton chapterPrerollSpin;
    private readonly Gtk.DropDown cookiesDropDown;
    private readonly Gtk.Entry cookiesCustomEntry;
    private readonly Gtk.Label cookiesErrorLabel;

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

        notebook = Gtk.Notebook.New();
        notebook.SetVexpand(true);
        notebook.SetHexpand(true);
        outerBox.Append(notebook);

        // General tab — app-level settings: theme dropdown and the open-in-new-window checkbox.
        var generalPage = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        generalPage.SetMarginStart(12);
        generalPage.SetMarginEnd(12);
        generalPage.SetMarginTop(12);
        generalPage.SetMarginBottom(12);

        // Theme row — applies instantly on Save (no next-launch note, unlike open-in-new-window below). Label + dropdown on one line.
        var themeRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        var themeLabel = Gtk.Label.New("Theme:");
        themeLabel.SetXalign(0);
        themeRow.Append(themeLabel);
        themeDropDown = Gtk.DropDown.NewFromStrings(new[] { "Auto", "Light", "Dark" });
        themeDropDown.SetSelected((uint)Array.IndexOf(ThemeOrder, owner.GetThemePreference()));
        themeRow.Append(themeDropDown);
        generalPage.Append(themeRow);

        openInNewWindowCheck = Gtk.CheckButton.NewWithLabel("Open files in a new window when launching from the command line");
        openInNewWindowCheck.SetActive(owner.GetOpenInNewWindowPreference());
        generalPage.Append(openInNewWindowCheck);

        var openInNewWindowNote = Gtk.Label.New("Changes apply on next launch.");
        openInNewWindowNote.AddCssClass("dim-label");
        openInNewWindowNote.SetXalign(0);
        openInNewWindowNote.SetMarginStart(24);
        generalPage.Append(openInNewWindowNote);

        // Chapter-seek preroll row — applies live on Save (the running VM is updated). Label + spin button, same single-line layout as the theme row. 0.1 s steps to one decimal; 0 means "land exactly on the cue".
        var prerollRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        var prerollLabel = Gtk.Label.New("Chapter seek preroll (seconds):");
        prerollLabel.SetXalign(0);
        prerollRow.Append(prerollLabel);
        chapterPrerollSpin = Gtk.SpinButton.NewWithRange(0.0, 60.0, 0.1);
        chapterPrerollSpin.SetDigits(1);
        chapterPrerollSpin.SetValue(owner.GetChapterSeekPrerollPreference());
        chapterPrerollSpin.SetTooltipText("Start chapter jumps this many seconds before the cue (0 = exactly at the cue).");
        prerollRow.Append(chapterPrerollSpin);
        generalPage.Append(prerollRow);

        // Cookie-source row — yt-dlp's --cookies-from-browser. Applies live on Save (pushed onto the shared downloader), so no next-launch note. The custom entry only makes sense on the Custom row, so it's hidden otherwise.
        var cookiesRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        var cookiesLabel = Gtk.Label.New("Cookies from browser:");
        cookiesLabel.SetXalign(0);
        cookiesRow.Append(cookiesLabel);

        var cookieRowLabels = new List<string> { "None" };
        foreach (var browser in CookieSource.Browsers)
        {
            // yt-dlp's names are lowercase; title-case them for display only — the value written to config stays yt-dlp's spelling.
            cookieRowLabels.Add(char.ToUpperInvariant(browser[0]) + browser.Substring(1));
        }
        cookieRowLabels.Add("Custom…");

        cookiesDropDown = Gtk.DropDown.NewFromStrings(cookieRowLabels.ToArray());
        var cookieChoice = CookieSource.Parse(owner.GetCookiesFromBrowserPreference());
        cookiesDropDown.SetSelected((uint)CookieSource.RowFor(cookieChoice));
        cookiesRow.Append(cookiesDropDown);

        cookiesCustomEntry = Gtk.Entry.New();
        cookiesCustomEntry.SetHexpand(true);
        cookiesCustomEntry.SetPlaceholderText("browser[+keyring][:profile][::container]");
        cookiesCustomEntry.SetTooltipText("A full yt-dlp --cookies-from-browser spec, e.g. \"firefox:/home/you/.mozilla/firefox/abc.default\" or \"chrome+gnomekeyring\".");
        if (cookieChoice.Kind == CookieSourceKind.Custom)
        {
            cookiesCustomEntry.SetText(cookieChoice.Value);
        }
        cookiesRow.Append(cookiesCustomEntry);
        generalPage.Append(cookiesRow);

        // The Flatpak/Snap caveat belongs here, not on the entry's tooltip: the user it warns is the one who picks "Firefox" from the dropdown and therefore never sees the entry, let alone hovers it.
        var cookiesNote = Gtk.Label.New("Sends browser cookies to yt-dlp for age-gated or private videos. Applies to videos yt-dlp downloads, not to direct stream links.\nA Flatpak or Snap browser needs Custom with an explicit profile path — yt-dlp only looks in the standard location.");
        cookiesNote.AddCssClass("dim-label");
        cookiesNote.SetXalign(0);
        cookiesNote.SetWrap(true);
        // Without a width cap the label requests its full natural width and stretches the dialog well past its 720px default.
        cookiesNote.SetMaxWidthChars(60);
        cookiesNote.SetMarginStart(24);
        generalPage.Append(cookiesNote);

        // Only shown when Save rejects the typed spec; keeps the dialog open so the user can fix it in place.
        cookiesErrorLabel = Gtk.Label.New("");
        cookiesErrorLabel.AddCssClass("error");
        cookiesErrorLabel.SetXalign(0);
        cookiesErrorLabel.SetWrap(true);
        cookiesErrorLabel.SetMaxWidthChars(60);
        cookiesErrorLabel.SetMarginStart(24);
        cookiesErrorLabel.SetVisible(false);
        generalPage.Append(cookiesErrorLabel);

        cookiesDropDown.OnNotify += OnCookiesDropDownNotify;
        RefreshCookiesCustomVisibility();

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
            var cookies = CurrentCookieSpec();
            // What's checkable without a URL is the browser name — also the part a user is most likely to get wrong, and the part that would otherwise surface as an error on every URL load until they worked out why. Keep the dialog open on a bad one. Custom-with-nothing-typed is caught here too: it would otherwise save as "" and silently reappear as None.
            string? cookieError;
            if ((int)cookiesDropDown.Selected == CookieSource.CustomRow && cookies.Length == 0)
            {
                cookieError = "Enter a cookie source, or choose None.";
            }
            else
            {
                cookieError = CookieSource.ValidationError(cookies, OperatingSystem.IsMacOS());
            }
            if (cookieError != null)
            {
                cookiesErrorLabel.SetLabel(cookieError);
                cookiesErrorLabel.SetVisible(true);
                // The message lives on the General page, so a Save clicked from the Hotkeys tab would otherwise look like a dead button.
                notebook.SetCurrentPage(0);
                cookiesCustomEntry.GrabFocus();
                return;
            }
            // Selected is always 0..2 here: the model has three rows and is never deselected, so it can't be GTK_INVALID_LIST_POSITION.
            owner.ApplyPreferences(editing.Clone(), openInNewWindowCheck.Active, ThemeOrder[themeDropDown.Selected], chapterPrerollSpin.GetValue(), cookies);
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

    // The spec the current widget state means. The row->spec arithmetic (including the out-of-range folds) lives in CookieSource so it's unit-testable; this only reads widgets.
    private string CurrentCookieSpec()
    {
        return CookieSource.SpecForRow((int)cookiesDropDown.Selected, cookiesCustomEntry.GetText());
    }

    private void OnCookiesDropDownNotify(GObject.Object sender, GObject.Object.NotifySignalArgs args)
    {
        if (args.Pspec.GetName() != "selected")
        {
            return;
        }
        RefreshCookiesCustomVisibility();
    }

    // The custom entry is hidden — not cleared — off the Custom row, so flipping to a browser and back doesn't destroy a long profile path the user typed. It's still dropped on Save, since only one string is persisted.
    private void RefreshCookiesCustomVisibility()
    {
        cookiesCustomEntry.SetVisible((int)cookiesDropDown.Selected == CookieSource.CustomRow);
        cookiesErrorLabel.SetVisible(false);
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
