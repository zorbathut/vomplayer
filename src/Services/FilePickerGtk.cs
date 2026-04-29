using System;
using System.Threading.Tasks;

namespace Vomplayer.Services;

// IFilePicker over Gtk.FileDialog. The parent Gtk.Window is captured at construction; the dialog is recreated per call so Title can be set per invocation. Returns null only when the user dismisses the dialog; any other exception propagates so real failures don't disappear into a silent "cancelled".
public sealed class FilePickerGtk : IFilePicker
{
    private readonly Gtk.Window parent;

    public FilePickerGtk(Gtk.Window parent)
    {
        if (parent == null)
        {
            throw new ArgumentNullException(nameof(parent));
        }
        this.parent = parent;
    }

    // Common video container extensions advertised in the Open dialog. AddSuffix matches case-insensitively, so lowercase here is enough. The list is deliberately short — vomplayer plays whatever libmpv plays, so the "All files" filter is the escape hatch for anything unusual.
    private static readonly string[] VideoExtensions =
    {
        "mp4", "mkv", "webm", "mov", "avi", "m4v", "ts", "mpg", "mpeg", "wmv", "flv",
    };

    // Common subtitle file extensions. Same KISS rationale as VideoExtensions: cover the formats users actually have on disk, leave "All files" as the escape hatch since mpv (via libass / its own demuxers) accepts more than this list (and even some non-subtitle files like .mkv can supply tracks via sub-add).
    private static readonly string[] SubtitleExtensions =
    {
        "srt", "ass", "ssa", "vtt", "sub", "idx", "sup", "smi", "mks",
    };

    public Task<string?> PickVideoFileAsync(string title)
    {
        return PickAsync(title, "Video files", VideoExtensions);
    }

    public Task<string?> PickSubtitleFileAsync(string title)
    {
        return PickAsync(title, "Subtitle files", SubtitleExtensions);
    }

    private async Task<string?> PickAsync(string title, string typedFilterName, string[] extensions)
    {
        var dialog = Gtk.FileDialog.New();
        dialog.SetTitle(title);

        var typedFilter = Gtk.FileFilter.New();
        typedFilter.SetName(typedFilterName);
        foreach (var ext in extensions)
        {
            typedFilter.AddSuffix(ext);
        }

        var allFilter = Gtk.FileFilter.New();
        allFilter.SetName("All files");
        allFilter.AddPattern("*");

        // Gtk.FileDialog.SetFilters takes a Gio.ListModel; the standard pattern is a Gio.ListStore typed for Gtk.FileFilter.
        var filters = Gio.ListStore.New(Gtk.FileFilter.GetGType());
        filters.Append(typedFilter);
        filters.Append(allFilter);
        dialog.SetFilters(filters);
        dialog.SetDefaultFilter(typedFilter);

        try
        {
            var file = await dialog.OpenAsync(parent);
            return file?.GetPath();
        }
        catch (GLib.GException ex) when (IsDismissed(ex))
        {
            return null;
        }
    }

    // Gtk.FileDialog.OpenAsync throws a GException with domain="gtk-dialog-error" and code=2 (GTK_DIALOG_ERROR_DISMISSED) when the user closes without selecting. Only this specific cause maps to a "no file" return; any other GException is a real failure and propagates.
    private static bool IsDismissed(GLib.GException ex)
    {
        return ex.Message != null && ex.Message.Contains("Dismissed", StringComparison.OrdinalIgnoreCase);
    }
}
