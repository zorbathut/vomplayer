using System;
using System.Threading.Tasks;
using Vomplayer.Util;

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

    public Task<string?> PickVideoFileAsync(string title)
    {
        return PickAsync(title, "Video files", MediaExtensions.Video);
    }

    public Task<string?> PickAudioFileAsync(string title)
    {
        return PickAsync(title, "Audio files", MediaExtensions.Audio);
    }

    public Task<string?> PickSubtitleFileAsync(string title)
    {
        return PickAsync(title, "Subtitle files", MediaExtensions.Subtitle);
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
        catch (GLib.GException ex) when (Util.GtkDialogError.IsDismissed(ex))
        {
            // User closed without selecting (gtk-dialog-error / DISMISSED) — the one quiet "no file" case; any other GException is a real failure and propagates.
            return null;
        }
    }
}
