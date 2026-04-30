using System;
using System.IO;
using Vomplayer.ViewModels;

namespace Vomplayer.Controls;

// Playlist sidebar: a Gtk.ScrolledWindow wrapping a Gtk.ListBox of playlist rows. Subscribes to Playlist.Changed and rebuilds rows wholesale (small N typical, imperative beats virtualized list-view ceremony). Each row carries its own Gtk.DragSource and Gtk.DropTarget for in-panel reorder; the panel-level "drop files to append" target is wired by MainWindow on this.ListBoxWidget so the FileList P/Invoke walker stays in one place.
//
// Why ListBox, not ListView: the per-row DragSource pattern requires a real per-row widget (so we can attach the controller); ListView's factory-driven row recycling would either need us to re-attach controllers in the bind callback or use ListView.BindModel-style stable-row-state — both add bookkeeping for no real win at our scale. ListBox.RemoveAll + Append is the rebuild loop and it costs nothing for typical playlists.
//
// SetActivateOnSingleClick(false): the default. Single-click selects, double-click activates → PlayPlaylistItem. Single-click activate would fight drag-reorder (every press would also fire row-activated and reload the file the user was trying to drag).
public sealed class PlaylistPanel : IDisposable
{
    private readonly Playlist playlist;
    private readonly Action<int> playItem;
    private readonly Gtk.ListBox listBox;
    private readonly Gtk.ScrolledWindow scrolledWindow;

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

        listBox = Gtk.ListBox.New();
        listBox.SetSelectionMode(Gtk.SelectionMode.Single);
        listBox.SetActivateOnSingleClick(false);
        listBox.OnRowActivated += OnRowActivated;
        listBox.AddCssClass("vompl-playlist-list");

        scrolledWindow = Gtk.ScrolledWindow.New();
        scrolledWindow.SetChild(listBox);
        scrolledWindow.AddCssClass("vompl-chrome");
        scrolledWindow.AddCssClass("vompl-playlist-panel");
        scrolledWindow.SetSizeRequest(240, -1);
        scrolledWindow.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);

        playlist.Changed += Rebuild;
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

    private void OnRowActivated(Gtk.ListBox sender, Gtk.ListBox.RowActivatedSignalArgs args)
    {
        playItem(args.Row.GetIndex());
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
        playlist.Changed -= Rebuild;
    }
}
