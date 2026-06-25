using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Vomplayer.Util;

namespace Vomplayer;

// File ▸ Open/Save Playlist handlers. The playlist is a newline-delimited textfile (PlaylistFile owns
// the pure format); the GTK file-dialog / clipboard / disk-I/O glue lives here in the view, mirroring
// the window drag-and-drop handler (HandleWindowDrop) — both do view-level I/O then call the
// public viewModel.LoadPaths(replace:true) / read viewModel.Playlist.Items. Open replaces, and Save
// exports, the focused stream's playlist (LoadPaths/Playlist resolve to SingleTarget).
//
// Each async handler wraps its whole body in one broad catch-and-report (the only quiet exit is the
// user dismissing a dialog): these run fire-and-forget from the menu actions, so an exception escaping
// the discarded Task would become an unobserved TaskException — a silent failure, which CLAUDE.md bans.
public sealed partial class MainWindow
{
    private async Task OpenPlaylistFromFileAsync()
    {
        try
        {
            string? path;
            try
            {
                var dialog = BuildPlaylistFileDialog("Open Playlist");
                var file = await dialog.OpenAsync(this);
                path = file?.GetPath();
            }
            catch (GLib.GException ex) when (IsDialogDismissed(ex))
            {
                return;
            }
            if (path == null)
            {
                return;
            }
            var contents = File.ReadAllText(path);
            var items = PlaylistFile.Parse(contents, Path.GetDirectoryName(path));
            // Empty parse leaves the current playlist intact — VideoContext.LoadPaths early-returns on
            // an empty list rather than wiping. Reveal the panel for multi-item results (drag-drop UX).
            viewModel.LoadPaths(items, replace: true);
            if (viewModel.Playlist.Items.Count >= 2)
            {
                ShowPlaylistPanel();
            }
        }
        catch (Exception ex)
        {
            urlPrompt.ShowError("Open Playlist", $"Couldn't open the playlist: {ex.Message}");
        }
    }

    private async Task OpenPlaylistFromClipboardAsync()
    {
        try
        {
            var clipboard = Gdk.Display.GetDefault()!.GetClipboard();
            // No base directory — clipboard text has no anchoring folder, so relative entries stay as-is.
            var text = await clipboard.ReadTextAsync();
            // ReadTextAsync resolves to null when the clipboard holds no text representation — nothing
            // to import, not an error.
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            var items = PlaylistFile.Parse(text, null);
            viewModel.LoadPaths(items, replace: true);
            if (viewModel.Playlist.Items.Count >= 2)
            {
                ShowPlaylistPanel();
            }
        }
        catch (Exception ex)
        {
            urlPrompt.ShowError("Open Playlist", $"Couldn't read the clipboard: {ex.Message}");
        }
    }

    private async Task SavePlaylistToFileAsync()
    {
        // Snapshot at click time (Playlist.Items is reassigned wholesale, never mutated in place, so the
        // reference stays stable across the await). Nothing to export ⇒ don't pop a dialog.
        var items = viewModel.Playlist.Items;
        if (items.Count == 0)
        {
            return;
        }
        try
        {
            string? path;
            try
            {
                var dialog = BuildPlaylistFileDialog("Save Playlist");
                dialog.SetInitialName("playlist.m3u");
                var file = await dialog.SaveAsync(this);
                path = file?.GetPath();
            }
            catch (GLib.GException ex) when (IsDialogDismissed(ex))
            {
                return;
            }
            if (path == null)
            {
                return;
            }
            File.WriteAllText(path, PlaylistFile.Serialize(items));
        }
        catch (Exception ex)
        {
            urlPrompt.ShowError("Save Playlist", $"Couldn't save the playlist: {ex.Message}");
        }
    }

    private void SavePlaylistToClipboard()
    {
        var items = viewModel.Playlist.Items;
        if (items.Count == 0)
        {
            // Nothing to export — don't clobber the clipboard with an empty string.
            return;
        }
        Gdk.Display.GetDefault()!.GetClipboard().SetText(PlaylistFile.Serialize(items));
    }

    // Shared playlist file dialog: typed filter (m3u/m3u8/txt) preferred, plus an All-files escape —
    // mirrors FilePickerGtk.PickAsync's filter setup.
    private static Gtk.FileDialog BuildPlaylistFileDialog(string title)
    {
        var dialog = Gtk.FileDialog.New();
        dialog.SetTitle(title);

        var typedFilter = Gtk.FileFilter.New();
        typedFilter.SetName("Playlists");
        typedFilter.AddSuffix("m3u");
        typedFilter.AddSuffix("m3u8");
        typedFilter.AddSuffix("txt");

        var allFilter = Gtk.FileFilter.New();
        allFilter.SetName("All files");
        allFilter.AddPattern("*");

        var filters = Gio.ListStore.New(Gtk.FileFilter.GetGType());
        filters.Append(typedFilter);
        filters.Append(allFilter);
        dialog.SetFilters(filters);
        dialog.SetDefaultFilter(typedFilter);
        return dialog;
    }

    // Gtk.FileDialog.OpenAsync/SaveAsync throw a GException (domain "gtk-dialog-error", code DISMISSED)
    // when the user closes without choosing — the one quiet "no selection" case. Mirrors
    // FilePickerGtk.IsDismissed; any other GException is a real failure and falls through to report.
    private static bool IsDialogDismissed(GLib.GException ex)
    {
        return ex.Message != null && ex.Message.Contains("Dismissed", StringComparison.OrdinalIgnoreCase);
    }
}
