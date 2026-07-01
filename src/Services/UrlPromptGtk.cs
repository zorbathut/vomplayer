using System;
using System.Threading.Tasks;
using Vomplayer.Controls;

namespace Vomplayer.Services;

// IUrlPrompt over plain Gtk.Window modals (URL entry + errors) plus the in-window DownloadStatusOverlay for load progress. Same style as PreferencesDialog / ShowAboutDialog — code-only, no .ui XML. The parent window is captured at construction so the dialogs land transient-for the right top-level. The status overlay is owned by MainWindow (it lives in the video Gtk.Overlay); we just drive it.
public sealed class UrlPromptGtk : IUrlPrompt
{
    private readonly Gtk.Window parent;
    private readonly DownloadStatusOverlay statusOverlay;

    public UrlPromptGtk(Gtk.Window parent, DownloadStatusOverlay statusOverlay)
    {
        if (parent == null)
        {
            throw new ArgumentNullException(nameof(parent));
        }
        if (statusOverlay == null)
        {
            throw new ArgumentNullException(nameof(statusOverlay));
        }
        this.parent = parent;
        this.statusOverlay = statusOverlay;
    }

    public Task<string?> PromptForUrlAsync(string title)
    {
        var tcs = new TaskCompletionSource<string?>();

        var dialog = Gtk.Window.New();
        dialog.Title = title;
        dialog.SetTransientFor(parent);
        dialog.SetModal(true);
        dialog.SetDefaultSize(480, 100);

        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        box.SetMarginTop(16);
        box.SetMarginBottom(16);
        box.SetMarginStart(16);
        box.SetMarginEnd(16);

        var prompt = Gtk.Label.New("URL:");
        prompt.SetXalign(0);
        box.Append(prompt);

        var entry = Gtk.Entry.New();
        entry.SetHexpand(true);
        entry.SetPlaceholderText("https://www.youtube.com/watch?v=...");
        box.Append(entry);

        var buttonRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        buttonRow.SetHalign(Gtk.Align.End);
        var cancelButton = Gtk.Button.NewWithLabel("Cancel");
        var okButton = Gtk.Button.NewWithLabel("Open");
        okButton.AddCssClass("suggested-action");
        buttonRow.Append(cancelButton);
        buttonRow.Append(okButton);
        box.Append(buttonRow);

        dialog.SetChild(box);

        // Single-fire result helper. TCS.TrySetResult guards against the close-then-button-then-close races GTK can trigger.
        bool finished = false;
        void Finish(string? result)
        {
            if (finished)
            {
                return;
            }
            finished = true;
            tcs.TrySetResult(result);
            dialog.Close();
        }

        okButton.OnClicked += (_, _) =>
        {
            var text = entry.GetText() ?? string.Empty;
            text = text.Trim();
            Finish(string.IsNullOrEmpty(text) ? null : text);
        };
        cancelButton.OnClicked += (_, _) => Finish(null);
        // Activate fires on Enter inside the entry — submit the form. Same UX as a browser address bar.
        entry.OnActivate += (_, _) =>
        {
            var text = entry.GetText() ?? string.Empty;
            text = text.Trim();
            Finish(string.IsNullOrEmpty(text) ? null : text);
        };
        dialog.OnCloseRequest += (_, _) =>
        {
            // Window-close button (or Escape via GTK's default close shortcut). Treat as cancel if no result was committed yet. Returning false lets the close proceed.
            if (!finished)
            {
                finished = true;
                tcs.TrySetResult(null);
            }
            return false;
        };

        dialog.Present();
        entry.GrabFocus();
        return tcs.Task;
    }

    public void ShowError(string title, string message)
    {
        var dialog = Gtk.Window.New();
        dialog.Title = title;
        dialog.SetTransientFor(parent);
        dialog.SetModal(true);
        dialog.SetDefaultSize(520, 240);

        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        box.SetMarginTop(16);
        box.SetMarginBottom(16);
        box.SetMarginStart(16);
        box.SetMarginEnd(16);

        var label = Gtk.Label.New(message);
        label.SetWrap(true);
        label.SetXalign(0);
        label.SetYalign(0);
        label.SetSelectable(true);
        // yt-dlp error stderr can be multi-line and arbitrarily long (network failures, DRM, format unavailability); the label needs to live inside a scrolled window so it doesn't push the OK button off-screen on small dialogs.
        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetChild(label);
        scroll.SetHexpand(true);
        scroll.SetVexpand(true);
        scroll.SetPolicy(Gtk.PolicyType.Automatic, Gtk.PolicyType.Automatic);
        box.Append(scroll);

        var okButton = Gtk.Button.NewWithLabel("OK");
        okButton.SetHalign(Gtk.Align.End);
        okButton.OnClicked += (_, _) => dialog.Close();
        box.Append(okButton);

        dialog.SetChild(box);
        dialog.Present();
    }

    public IUrlStatusHandle ShowUrlStatus(string statusText, Action? onCancel)
    {
        return statusOverlay.Show(statusText, onCancel);
    }
}
