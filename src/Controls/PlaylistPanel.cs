using System;
using System.IO;
using System.Threading.Tasks;
using Vomplayer.ViewModels;

namespace Vomplayer.Controls;

// Playlist sidebar: a Gtk.ScrolledWindow wrapping a Gtk.ListBox of playlist rows. Subscribes to Playlist.Changed and rebuilds rows wholesale (small N typical, imperative beats virtualized list-view ceremony). Each row carries its own Gtk.DragSource and Gtk.DropTarget for in-panel reorder; the panel-level "drop files to append" target is wired by MainWindow on this.ListBoxWidget so the FileList P/Invoke walker stays in one place.
//
// Why ListBox, not ListView: the per-row DragSource pattern requires a real per-row widget (so we can attach the controller); ListView's factory-driven row recycling would either need us to re-attach controllers in the bind callback or use ListView.BindModel-style stable-row-state — both add bookkeeping for no real win at our scale. ListBox.RemoveAll + Append is the rebuild loop and it costs nothing for typical playlists.
//
// SetActivateOnSingleClick(false): the default. Single-click selects, double-click activates → PlayPlaylistItem. Single-click activate would fight drag-reorder (every press would also fire row-activated and reload the file the user was trying to drag).
public sealed class PlaylistPanel : IDisposable
{
    // Mutable so Rebind can swap them when the active VideoContext changes (PiP SelectedSlot transitions). The Changed subscription moves with the swap so the panel always tracks the bound playlist.
    private Playlist playlist;
    private Action<int> playItem;
    private readonly Gtk.ListBox listBox;
    private readonly Gtk.ScrolledWindow scrolledWindow;
    // Captured once so subscribe and unsubscribe call the same delegate. Inline `_ => Rebuild()` lambdas would create distinct delegate instances and the unsubscribe in Dispose / Rebind would silently no-op, leaking the subscription past the panel's lifetime.
    private readonly Action<PlaylistChangeKind> changedHandler;

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
        changedHandler = _ => Rebuild();

        listBox = Gtk.ListBox.New();
        listBox.SetSelectionMode(Gtk.SelectionMode.Single);
        listBox.SetActivateOnSingleClick(false);
        listBox.OnRowActivated += OnRowActivated;
        listBox.AddCssClass("vompl-playlist-list");

        // Secondary-button (3) gesture for the row context menu. Attached once to the persistent listBox (survives Rebuild/Rebind); SetButton(3) keeps it off button 1, so per-row select/activate/drag are untouched. Gesture coords are listBox-local — directly consumable by GetRowAtY and the popover's SetPointingTo.
        var rightClick = Gtk.GestureClick.New();
        rightClick.SetButton(3);
        rightClick.OnPressed += OnRowRightClick;
        listBox.AddController(rightClick);

        scrolledWindow = Gtk.ScrolledWindow.New();
        scrolledWindow.SetChild(listBox);
        scrolledWindow.AddCssClass("vompl-chrome");
        scrolledWindow.AddCssClass("vompl-playlist-panel");
        scrolledWindow.SetSizeRequest(240, -1);
        scrolledWindow.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);

        playlist.Changed += changedHandler;
        Rebuild();
    }

    // Outer widget for layout insertion.
    public Gtk.Widget Widget
    {
        get
        {
            return scrolledWindow;
        }
    }

    // Inner widget for MainWindow to attach the file-drop "append" DropTarget to. Targeting the ListBox specifically (rather than the ScrolledWindow) keeps drops on the scrollbar from accidentally consuming, and allows the user to drop on empty space below the rows.
    public Gtk.Widget DropArea
    {
        get
        {
            return listBox;
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
        Rebuild();
    }

    private void OnRowActivated(Gtk.ListBox sender, Gtk.ListBox.RowActivatedSignalArgs args)
    {
        playItem(args.Row.GetIndex());
    }

    private void OnRowRightClick(Gtk.GestureClick sender, Gtk.GestureClick.PressedSignalArgs args)
    {
        var row = listBox.GetRowAtY((int)args.Y);
        if (row == null)
        {
            // Right-click below the last row (empty space) — nothing to act on.
            return;
        }
        listBox.SelectRow(row);
        ShowRowMenu(row.GetIndex(), args.X, args.Y);
    }

    // Per-click context menu. A plain Gtk.Popover of flat buttons rather than a Gio-action-backed PopoverMenu: the panel's idiom is direct callbacks (OnRowActivated, the drag OnPrepare/OnRowDrop), and a transient 4-item menu doesn't earn the action-map machinery the live menubar needs. Items adapt to the row kind — a URL opens in a browser, a local path reveals its folder. Built fresh each time and unparented on close so right-clicks don't accumulate parented popovers.
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
        AppendMenuItem(box, popover, "Remove from Playlist", () => RemoveRow(index));

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

    private void RemoveRow(int index)
    {
        // The captured index can in principle go stale between popup and click (a PiP Rebind, or another mutation). Guard so a stale index reports rather than throwing out of the button handler.
        if (index < 0 || index >= playlist.Items.Count)
        {
            Console.Error.WriteLine($"[vompl] playlist remove: stale index {index} (count {playlist.Items.Count})");
            return;
        }
        bool wasCurrent = index == playlist.CurrentIndex;
        playlist.Remove(index);
        // Removing the playing row: play whatever slid into its place (Remove already moved CurrentIndex there). Same activation path as double-click.
        if (wasCurrent && playlist.Items.Count > 0)
        {
            playItem(playlist.CurrentIndex);
        }
    }

    private void Rebuild()
    {
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
                // Serialize the source row index through a GValue. Gdk.ContentProvider.NewForValue(GValue<int>) is matched by Gtk.DropTarget.New(GObject.Type.Int, …) on the receiving end; the int travels through GTK's content negotiation cleanly, no out-of-band state required.
                var val = new GObject.Value(rowIndex);
                return Gdk.ContentProvider.NewForValue(val);
            };
            row.AddController(dragSource);

            var rowDrop = Gtk.DropTarget.New(GObject.Type.Int, Gdk.DragAction.Move);
            rowDrop.OnDrop += OnRowDrop;
            row.AddController(rowDrop);

            listBox.Append(row);
        }
    }

    private bool OnRowDrop(Gtk.DropTarget sender, Gtk.DropTarget.DropSignalArgs args)
    {
        // The dropped row index — sourced from the row's DragSource.OnPrepare.
        int from = args.Value.GetInt();
        // Resolve the target row index from the controller's widget (the row receiving the drop). GirCore's GestureSingle.Widget returns the controller's owning widget; we cast and read GetIndex().
        var targetRow = sender.Widget as Gtk.ListBoxRow;
        if (targetRow == null)
        {
            return false;
        }
        int to = targetRow.GetIndex();
        if (from < 0 || from >= playlist.Items.Count || from == to)
        {
            return false;
        }
        // Reentrancy: playlist.Move fires Changed → Rebuild → RemoveAll, which destroys the very row whose DropTarget is dispatching this OnDrop. GTK4's signal emission holds a refcount on the widget for the duration of the handler so the destroy-during-emit is safe; the row is finalized only after we return. If a future GTK release breaks that contract, switch to GLib.Functions.IdleAdd to defer the Move out of the signal frame.
        playlist.Move(from, to);
        return true;
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
