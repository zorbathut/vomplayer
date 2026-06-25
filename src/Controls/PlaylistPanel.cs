using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vomplayer.Util;
using Vomplayer.ViewModels;

namespace Vomplayer.Controls;

// Playlist sidebar: a Gtk.ScrolledWindow wrapping a Gtk.ListBox of playlist rows. Subscribes to Playlist.Changed and rebuilds rows wholesale (small N typical, imperative beats virtualized list-view ceremony). The list is multi-select (range/toggle/select-all are GTK-native in SelectionMode.Multiple); Del deletes the selection (focus-scoped key controller on the listBox); external file drops insert at the pointer position; intra-panel drags reorder the whole selection.
//
// Why ListBox, not ListView: the per-row DragSource pattern requires a real per-row widget (so we can attach the controller); ListView's factory-driven row recycling would either need us to re-attach controllers in the bind callback or use ListView.BindModel-style stable-row-state — both add bookkeeping for no real win at our scale. ListBox.RemoveAll + Append is the rebuild loop and it costs nothing for typical playlists.
//
// Selection survives the wholesale Rebuild: GTK owns the selection state in the row widgets, which RemoveAll destroys, so Rebuild reapplies it — keyed on the PlaylistChangeKind. SetCurrent/Advance/Append leave existing row positions intact, so the LIVE selection (read before RemoveAll) is reapplied as-is (this is what stops an auto-advance during playback from wiping a manual selection). Panel-initiated edits (MoveMany/Insert/RemoveMany) set `pendingSelection` to the exact post-edit indices they want selected; Replace/Prepend clear. Reading the LIVE selection (not a stale captured set) is load-bearing: a plain click in Multiple mode collapses GTK's selection to the clicked row on button-press, so a double-click's SetCurrent reapplies just the played row, not a resurrected old multi-selection.
//
// SetActivateOnSingleClick(false): the default. Single-click selects, double-click activates → PlayPlaylistItem. Single-click activate would fight drag-reorder (every press would also fire row-activated and reload the file the user was trying to drag).
public sealed class PlaylistPanel : IDisposable
{
    // Mutable so Rebind can swap them when the active VideoContext changes (PiP SelectedSlot transitions). The Changed subscription moves with the swap so the panel always tracks the bound playlist. The drop handlers read these fields live (not captured copies) so a Rebind mid-session lands edits in the right context.
    private Playlist playlist;
    private Action<int> playItem;
    private readonly Gtk.ListBox listBox;
    private readonly Gtk.ScrolledWindow scrolledWindow;
    // Captured once so subscribe and unsubscribe call the same delegate. Inline `k => Rebuild(k)` lambdas would create distinct delegate instances and the unsubscribe in Dispose / Rebind would silently no-op, leaking the subscription past the panel's lifetime.
    private readonly Action<PlaylistChangeKind> changedHandler;
    // Set by a panel-initiated mutation (MoveMany/Insert/RemoveMany) to the indices it wants selected after the resulting Rebuild; consumed by Rebuild. Always set/cleared under a try/finally around the mutation call — a no-op mutation (e.g. MoveMany drop-in-place) never fires Changed/Rebuild, so the finally is what stops a stale value leaking into the next unrelated Rebuild.
    private List<int>? pendingSelection;
    // Set by a reorder drop to the moved block's new positions; applied in the DragSource's drag-end handler. GtkListBox collapses the multi-selection to a single row during its own button-release handling, which runs AFTER the drop handler and after idle callbacks — drag-end is the first hook that fires after that collapse, so it's where we reassert the group selection.
    private List<int>? pendingDragSelection;
    // The row currently carrying the drop-insertion-line CSS class, and which edge. Cleared on drag-leave and nulled (not touched) in Rebuild since RemoveAll destroys the row.
    private Gtk.ListBoxRow? indicatorRow;
    private bool indicatorAfter;

    public PlaylistPanel(Playlist playlist, Action<int> playItem)
    {
        if (playlist == null)
        {
            throw new ArgumentNullException(nameof(playlist));
        }
        if (playItem == null)
        {
            throw new ArgumentNullException(nameof(playItem));
        }
        this.playlist = playlist;
        this.playItem = playItem;
        changedHandler = OnPlaylistChanged;

        listBox = Gtk.ListBox.New();
        // Multiple selection gives shift-click range + ctrl-click toggle, and Ctrl-A → list.select-all is a GTK-native binding gated on this mode. The window-level capture key controller leaves Ctrl-A / Delete unbound, so they reach the focused list.
        listBox.SetSelectionMode(Gtk.SelectionMode.Multiple);
        listBox.SetActivateOnSingleClick(false);
        listBox.OnRowActivated += OnRowActivated;
        listBox.AddCssClass("vompl-playlist-list");

        // Secondary-button (3) gesture for the row context menu. Attached once to the persistent listBox (survives Rebuild/Rebind); SetButton(3) keeps it off button 1, so per-row select/activate/drag are untouched. Gesture coords are listBox-local — directly consumable by GetRowAtY and the popover's SetPointingTo.
        var rightClick = Gtk.GestureClick.New();
        rightClick.SetButton(3);
        rightClick.OnPressed += OnRowRightClick;
        listBox.AddController(rightClick);

        // Focus-scoped Delete: a key controller on the listBox only fires while a row (or the list) holds keyboard focus. KP_Delete covers the numpad key.
        var keyController = Gtk.EventControllerKey.New();
        keyController.OnKeyPressed += OnListKeyPressed;
        listBox.AddController(keyController);

        // Intra-panel reorder: one listbox-level DropTarget receiving the source row index (a typed int — immune to stray text/uri drags, unlike a string payload). The pointer Y from the drop/motion signal picks the gap; the drag moves the whole live selection when the grabbed row is part of it. Per-row DragSources (created in Rebuild) supply the int payload.
        var reorderDrop = Gtk.DropTarget.New(GObject.Type.Int, Gdk.DragAction.Move);
        reorderDrop.OnDrop += OnReorderDrop;
        reorderDrop.OnMotion += OnReorderMotion;
        reorderDrop.OnLeave += OnReorderLeave;
        listBox.AddController(reorderDrop);

        // Positional external-file drop: the panel owns this (it needs row geometry to place the insertion), rather than MainWindow's window-level (replace) target. The (x,y) is frozen at drop time inside UriListDropTarget; the gap is computed from y in the callback — safe because nothing scrolls/rebuilds the list between the drop signal and the deferred idle-tick read.
        var positionalDrop = UriListDropTarget.CreatePositional(HandlePositionalDrop);
        positionalDrop.OnDragMotion += OnPositionalMotion;
        positionalDrop.OnDragLeave += OnPositionalLeave;
        listBox.AddController(positionalDrop);

        scrolledWindow = Gtk.ScrolledWindow.New();
        scrolledWindow.SetChild(listBox);
        scrolledWindow.AddCssClass("vompl-chrome");
        scrolledWindow.AddCssClass("vompl-playlist-panel");
        scrolledWindow.SetSizeRequest(240, -1);
        scrolledWindow.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);

        playlist.Changed += changedHandler;
        Rebuild(null);
    }

    // Outer widget for layout insertion.
    public Gtk.Widget Widget
    {
        get
        {
            return scrolledWindow;
        }
    }

    // Swap the bound playlist + activation callback. Used by MainWindow when the active video changes during PiP (the panel should reflect whichever video is currently active). The Changed subscription is moved atomically and a Rebuild fires immediately so the panel reflects the new playlist's state.
    public void Rebind(Playlist newPlaylist, Action<int> newPlayItem)
    {
        if (newPlaylist == null)
        {
            throw new ArgumentNullException(nameof(newPlaylist));
        }
        if (newPlayItem == null)
        {
            throw new ArgumentNullException(nameof(newPlayItem));
        }
        if (ReferenceEquals(newPlaylist, playlist))
        {
            playItem = newPlayItem;
            return;
        }
        playlist.Changed -= changedHandler;
        playlist = newPlaylist;
        playItem = newPlayItem;
        playlist.Changed += changedHandler;
        Rebuild(null);
    }

    private void OnRowActivated(Gtk.ListBox sender, Gtk.ListBox.RowActivatedSignalArgs args)
    {
        playItem(args.Row.GetIndex());
    }

    private void OnPlaylistChanged(PlaylistChangeKind kind)
    {
        Rebuild(kind);
    }

    private bool OnListKeyPressed(Gtk.EventControllerKey sender, Gtk.EventControllerKey.KeyPressedSignalArgs args)
    {
        if (args.Keyval == (uint)Gdk.Constants.KEY_Delete || args.Keyval == (uint)Gdk.Constants.KEY_KP_Delete)
        {
            return RemoveSelected();
        }
        return false;
    }

    // Read the live selection as row indices. Called before RemoveAll (so it sees the current rows) and on drop/delete. Iterates the actual rows rather than playlist.Items.Count: during a Rebuild triggered by a model mutation the model count has already changed but the old rows are still attached.
    private List<int> LiveSelection()
    {
        var result = new List<int>();
        int i = 0;
        while (true)
        {
            var row = listBox.GetRowAtIndex(i);
            if (row == null)
            {
                break;
            }
            if (row.IsSelected())
            {
                result.Add(i);
            }
            i++;
        }
        return result;
    }

    // Delete the current selection. Shared by Del and the context-menu remove. Re-scans the live selection and filters to in-range indices at call time (RemoveMany throws on out-of-range; a selected index can go stale across a popup→click PiP Rebind). Returns whether anything was deleted.
    private bool RemoveSelected()
    {
        var sel = LiveSelection().Where(i => i >= 0 && i < playlist.Items.Count).ToList();
        if (sel.Count == 0)
        {
            return false;
        }
        bool wasCurrent = sel.Contains(playlist.CurrentIndex);
        RunWithPendingSelection(new List<int>(), () => playlist.RemoveMany(sel));
        // Removing the playing row(s): play whatever slid into its place (RemoveMany already moved CurrentIndex there). Same activation path as double-click. Deleting every item leaves CurrentIndex == -1 and the last video still playing — parity with single-row remove.
        if (wasCurrent && playlist.Items.Count > 0)
        {
            playItem(playlist.CurrentIndex);
        }
        return true;
    }

    private void OnRowRightClick(Gtk.GestureClick sender, Gtk.GestureClick.PressedSignalArgs args)
    {
        var row = listBox.GetRowAtY((int)args.Y);
        if (row == null)
        {
            // Right-click below the last row (empty space) — nothing to act on.
            return;
        }
        // Right-clicking inside an existing multi-selection acts on the whole selection; right-clicking elsewhere collapses to that one row (SelectRow in Multiple mode only adds, so unselect first).
        if (!row.IsSelected())
        {
            listBox.UnselectAll();
            listBox.SelectRow(row);
        }
        ShowRowMenu(row.GetIndex(), args.X, args.Y);
    }

    // Per-click context menu. A plain Gtk.Popover of flat buttons rather than a Gio-action-backed PopoverMenu: the panel's idiom is direct callbacks (OnRowActivated, the drag OnReorderDrop), and a transient menu doesn't earn the action-map machinery the live menubar needs. Items adapt to the row kind — a URL opens in a browser, a local path reveals its folder. The remove item acts on the whole selection. Built fresh each time and unparented on close so right-clicks don't accumulate parented popovers.
    private void ShowRowMenu(int index, double x, double y)
    {
        string item = playlist.Items[index];
        bool isUrl = item.Contains("://");

        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        box.AddCssClass("vompl-playlist-menu");

        var popover = Gtk.Popover.New();
        popover.SetParent(listBox);
        popover.SetHasArrow(false);
        popover.SetPointingTo(new Gdk.Rectangle { X = (int)x, Y = (int)y, Width = 1, Height = 1 });
        popover.SetChild(box);
        popover.OnClosed += (_, _) => popover.Unparent();

        if (isUrl)
        {
            AppendMenuItem(box, popover, "Open in Browser", () => OpenInBrowser(item));
            AppendMenuItem(box, popover, "Copy URL", () => CopyToClipboard(item));
        }
        else
        {
            AppendMenuItem(box, popover, "Open Containing Folder", () => OpenContainingFolder(item));
            AppendMenuItem(box, popover, "Copy Path", () => CopyToClipboard(item));
        }
        box.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
        int selectionCount = LiveSelection().Count;
        string removeLabel = selectionCount > 1 ? $"Remove {selectionCount} items from Playlist" : "Remove from Playlist";
        AppendMenuItem(box, popover, removeLabel, () => RemoveSelected());

        popover.Popup();
    }

    private static void AppendMenuItem(Gtk.Box box, Gtk.Popover popover, string label, Action action)
    {
        var button = Gtk.Button.NewWithLabel(label);
        button.AddCssClass("flat");
        button.SetHalign(Gtk.Align.Fill);
        // NewWithLabel centers its label; left-align it so the popover reads like a menu rather than a stack of buttons.
        if (button.GetChild() is Gtk.Label labelWidget)
        {
            labelWidget.SetXalign(0.0f);
        }
        button.OnClicked += (_, _) =>
        {
            popover.Popdown();
            action();
        };
        box.Append(button);
    }

    private void OpenInBrowser(string url)
    {
        var window = scrolledWindow.GetRoot() as Gtk.Window;
        var launcher = Gtk.UriLauncher.New(url);
        _ = LaunchAndReport(launcher.LaunchAsync(window), "open in browser");
    }

    private void OpenContainingFolder(string path)
    {
        var window = scrolledWindow.GetRoot() as Gtk.Window;
        var launcher = Gtk.FileLauncher.New(Gio.Functions.FileNewForPath(path));
        _ = LaunchAndReport(launcher.OpenContainingFolderAsync(window), "open containing folder");
    }

    // Fire-and-forget for the GTK launchers. An exception escaping a discarded Task would be an unobserved TaskException — a silent failure, which CLAUDE.md bans — so we await and report to stderr (a user-dismissed portal dialog also lands here, harmlessly). Awaiting also keeps the launch Task, and thus the launcher GObject, alive until it completes.
    private static async Task LaunchAndReport(Task launch, string what)
    {
        try
        {
            await launch;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vompl] {what} failed: {ex.Message}");
        }
    }

    private void CopyToClipboard(string text)
    {
        listBox.GetClipboard().SetText(text);
    }

    // Run a panel-initiated mutation with the post-edit selection it wants restored. The finally clears pendingSelection even when the mutation is a no-op (no Changed → no Rebuild → nothing consumes it).
    private void RunWithPendingSelection(List<int> selection, Action mutate)
    {
        pendingSelection = selection;
        try
        {
            mutate();
        }
        finally
        {
            pendingSelection = null;
        }
    }

    // ---- Reorder (intra-panel drag of the selection) ----

    private bool OnReorderDrop(Gtk.DropTarget sender, Gtk.DropTarget.DropSignalArgs args)
    {
        ClearDropIndicator();
        int source = args.Value.GetInt();
        if (source < 0 || source >= playlist.Items.Count)
        {
            return false;
        }
        var selection = LiveSelection();
        // Dragging a row that's part of the selection moves the whole selection; dragging an unselected row moves just it.
        List<int> moveSet = selection.Contains(source) ? selection : new List<int> { source };
        int gap = GapFromY(args.Y);
        // The block lands contiguously at insertPos in the new list (mirrors Playlist.MoveMany's math) — select it after the rebuild.
        int below = moveSet.Count(i => i < gap);
        int insertPos = gap - below;
        var landed = Enumerable.Range(insertPos, moveSet.Count).ToList();
        RunWithPendingSelection(landed, () => playlist.MoveMany(moveSet, gap));
        // GtkListBox collapses the selection to a single row during button-release handling, which runs after this drop handler. Defer the real reselection to drag-end (the first hook after that collapse).
        pendingDragSelection = landed;
        return true;
    }

    private Gdk.DragAction OnReorderMotion(Gtk.DropTarget sender, Gtk.DropTarget.MotionSignalArgs args)
    {
        ShowDropIndicator(GapFromY(args.Y));
        return Gdk.DragAction.Move;
    }

    private void OnReorderLeave(Gtk.DropTarget sender, EventArgs args)
    {
        ClearDropIndicator();
    }

    // Fires once the drag fully completes — after GtkListBox has applied its own single-row collapse. Reassert the moved block's selection (set by OnReorderDrop) so a group drag keeps its whole selection. Null when the drag didn't end in a reorder drop on this panel (cancelled, or dropped elsewhere), in which case we leave GTK's selection alone.
    private void OnDragEnd(Gtk.DragSource sender, Gtk.DragSource.DragEndSignalArgs args)
    {
        if (pendingDragSelection == null)
        {
            return;
        }
        var selection = pendingDragSelection;
        pendingDragSelection = null;
        listBox.UnselectAll();
        ApplySelection(selection);
    }

    // ---- Positional external-file drop ----

    private void HandlePositionalDrop(List<string> paths, double x, double y)
    {
        ClearDropIndicator();
        if (paths.Count == 0)
        {
            return;
        }
        int gap = GapFromY(y);   // 0 on an empty list
        bool wasEmpty = playlist.Items.Count == 0;
        var inserted = Enumerable.Range(gap, paths.Count).ToList();
        RunWithPendingSelection(inserted, () => playlist.Insert(gap, paths));
        // Dropping onto an empty playlist starts playback of the first item (matches the append-from-empty path); inserting into a populated playlist just queues the items behind whatever is playing.
        if (wasEmpty)
        {
            playItem(0);
        }
    }

    private Gdk.DragAction OnPositionalMotion(Gtk.DropTargetAsync sender, Gtk.DropTargetAsync.DragMotionSignalArgs args)
    {
        ShowDropIndicator(GapFromY(args.Y));
        return Gdk.DragAction.Copy;
    }

    private void OnPositionalLeave(Gtk.DropTargetAsync sender, Gtk.DropTargetAsync.DragLeaveSignalArgs args)
    {
        ClearDropIndicator();
    }

    // Pointer Y (listBox-local) → insertion gap in [0, Count]. Above a row's vertical midpoint inserts before it, below inserts after it; past the last row (or empty list) inserts at the end.
    private int GapFromY(double y)
    {
        int count = playlist.Items.Count;
        var row = listBox.GetRowAtY((int)y);
        if (row == null)
        {
            return count;
        }
        int idx = row.GetIndex();
        if (row.ComputeBounds(listBox, out var bounds))
        {
            double mid = bounds.GetY() + bounds.GetHeight() / 2.0;
            return y < mid ? idx : idx + 1;
        }
        return idx;
    }

    private void ShowDropIndicator(int gap)
    {
        ClearDropIndicator();
        int count = playlist.Items.Count;
        if (count == 0)
        {
            return;
        }
        if (gap >= count)
        {
            var row = listBox.GetRowAtIndex(count - 1);
            if (row != null)
            {
                row.AddCssClass("vompl-drop-after");
                indicatorRow = row;
                indicatorAfter = true;
            }
        }
        else
        {
            var row = listBox.GetRowAtIndex(gap);
            if (row != null)
            {
                row.AddCssClass("vompl-drop-before");
                indicatorRow = row;
                indicatorAfter = false;
            }
        }
    }

    private void ClearDropIndicator()
    {
        if (indicatorRow != null)
        {
            indicatorRow.RemoveCssClass(indicatorAfter ? "vompl-drop-after" : "vompl-drop-before");
            indicatorRow = null;
        }
    }

    private void Rebuild(PlaylistChangeKind? kind)
    {
        // Decide which selection to restore after the wholesale row rebuild — must read the LIVE selection before RemoveAll destroys the rows.
        List<int> restore;
        if (pendingSelection != null)
        {
            restore = pendingSelection;
        }
        else if (kind == PlaylistChangeKind.SetCurrent || kind == PlaylistChangeKind.Advance || kind == PlaylistChangeKind.Append)
        {
            // These leave existing row positions intact, so the live selection maps unchanged into the new list.
            restore = LiveSelection();
        }
        else
        {
            // Replace / Prepend / initial build → no carry-over (Insert/Move/Remove always arrive via pendingSelection).
            restore = new List<int>();
        }

        // The rows are about to be destroyed; drop the stale indicator reference without touching it.
        indicatorRow = null;
        listBox.RemoveAll();
        // Right-align the index number to the width of the largest index in the current playlist (e.g. items 1..12 → " 1." through "12."). Keeps the title column aligned regardless of how many items are loaded; recomputed per Rebuild because Append/Replace can change the bound. Cap below: empty playlist hits zero iterations and the format is irrelevant.
        int indexWidth = playlist.Items.Count.ToString().Length;
        for (int i = 0; i < playlist.Items.Count; i++)
        {
            bool isCurrent = i == playlist.CurrentIndex;
            // Prefix the row text with "▶ " for the playing item, two spaces otherwise — the play-arrow gives an unmistakable visual cue independent of CSS theme support, and the matched-width spacing keeps the number column lined up across rows. CSS handles the additional bold/background styling on top.
            string indicator = isCurrent ? "▶ " : "  ";
            string number = (i + 1).ToString().PadLeft(indexWidth);
            string text = $"{indicator}{number}. {GetDisplayName(playlist.Items[i])}";

            var label = Gtk.Label.New(text);
            label.SetXalign(0.0f);
            label.SetMarginStart(8);
            label.SetMarginEnd(8);
            label.SetMarginTop(4);
            label.SetMarginBottom(4);
            label.SetTooltipText(playlist.Items[i]);

            var row = Gtk.ListBoxRow.New();
            row.SetChild(label);
            row.SetSelectable(true);
            if (isCurrent)
            {
                row.AddCssClass("vompl-playlist-current");
            }

            // Capture the index by value — the closure runs on drag-start, by which point the row's GetIndex() would still report the same value, but capturing locally is clearer and avoids any future refactor that introduces nullable rows.
            int rowIndex = i;
            var dragSource = Gtk.DragSource.New();
            dragSource.SetActions(Gdk.DragAction.Move);
            dragSource.OnPrepare += (_, _) =>
            {
                // Serialize the source row index through a GValue<int>. The listbox-level reorder DropTarget (created in the ctor) reads it back via DropSignalArgs.Value.GetInt() and decides whether to move the whole selection.
                var val = new GObject.Value(rowIndex);
                return Gdk.ContentProvider.NewForValue(val);
            };
            dragSource.OnDragEnd += OnDragEnd;
            row.AddController(dragSource);

            listBox.Append(row);
        }

        ApplySelection(restore);
    }

    // Select the rows at `indices` (out-of-range entries skipped). Additive — callers that need an exact selection UnselectAll first.
    private void ApplySelection(IReadOnlyList<int> indices)
    {
        foreach (int idx in indices)
        {
            if (idx >= 0 && idx < playlist.Items.Count)
            {
                var row = listBox.GetRowAtIndex(idx);
                if (row != null)
                {
                    listBox.SelectRow(row);
                }
            }
        }
    }

    // Local paths render as basename — tiny rows look nicer with just the filename. URIs render in full because their basename is rarely meaningful (e.g. a streaming URL's path component is often a hash).
    private static string GetDisplayName(string pathOrUri)
    {
        if (pathOrUri.Contains("://"))
        {
            return pathOrUri;
        }
        var name = Path.GetFileName(pathOrUri);
        return string.IsNullOrEmpty(name) ? pathOrUri : name;
    }

    public void Dispose()
    {
        playlist.Changed -= changedHandler;
    }
}
