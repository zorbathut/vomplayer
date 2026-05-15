using System;
using System.Threading;
using System.Threading.Tasks;

namespace Vomplayer.Services;

// IUrlPrompt over plain Gtk.Window modals. Same style as PreferencesDialog / ShowAboutDialog — code-only, no .ui XML. The parent window is captured at construction so the dialogs land transient-for the right top-level.
public sealed class UrlPromptGtk : IUrlPrompt
{
    private readonly Gtk.Window parent;

    public UrlPromptGtk(Gtk.Window parent)
    {
        if (parent == null)
        {
            throw new ArgumentNullException(nameof(parent));
        }
        this.parent = parent;
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

    public UrlProgressHandle ShowDownloadProgress(string title, CancellationTokenSource cts)
    {
        if (cts == null)
        {
            throw new ArgumentNullException(nameof(cts));
        }
        var dialog = Gtk.Window.New();
        dialog.Title = title;
        dialog.SetTransientFor(parent);
        dialog.SetModal(true);
        dialog.SetDefaultSize(420, 140);
        // Disable the user-driven close path — Cancel is the only sanctioned exit. Without this, the user could close the window mid-download and we'd leak the running yt-dlp process. The dialog still closes from code via the IDisposable.Dispose path below.
        dialog.SetDeletable(false);

        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        box.SetMarginTop(16);
        box.SetMarginBottom(16);
        box.SetMarginStart(16);
        box.SetMarginEnd(16);

        var statusLabel = Gtk.Label.New("Starting…");
        statusLabel.SetXalign(0);
        statusLabel.SetWrap(true);
        box.Append(statusLabel);

        var bar = Gtk.ProgressBar.New();
        bar.SetShowText(true);
        bar.SetText("");
        box.Append(bar);

        var cancelButton = Gtk.Button.NewWithLabel("Cancel");
        cancelButton.SetHalign(Gtk.Align.End);
        cancelButton.OnClicked += (_, _) =>
        {
            cancelButton.SetSensitive(false);
            statusLabel.SetLabel("Cancelling…");
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // VM disposed the CTS already; nothing to do.
            }
        };
        box.Append(cancelButton);

        dialog.SetChild(box);
        dialog.Present();

        // Progress<T> latches the SynchronizationContext at construction. GirCore installs a SyncContext via Gtk.Application.RunWithSynchronizationContext, so each Report callback lands on the main thread (where it's safe to touch GTK widgets). The `closed` flag is only read/written from the main thread (Dispose runs on main; Progress callback runs on main).
        bool closed = false;
        var progress = new Progress<UrlDownloadProgress>(p =>
        {
            if (closed)
            {
                return;
            }
            UpdateProgressBar(bar, statusLabel, p);
        });

        var closer = new ProgressDialogHandle(() =>
        {
            if (closed)
            {
                return;
            }
            closed = true;
            dialog.SetDeletable(true);
            dialog.Close();
        });
        return new UrlProgressHandle(closer, progress);
    }

    private static void UpdateProgressBar(Gtk.ProgressBar bar, Gtk.Label statusLabel, UrlDownloadProgress p)
    {
        if (p.Status == "finished")
        {
            // yt-dlp moves through "downloading" → "finished" on the download phase, but post-processing (merge / extract / mux) can take a meaningful chunk of wall-time after that. The UI shows "Finalizing…" with an indeterminate spinner during that gap so the user doesn't think it's hung at 100%.
            statusLabel.SetLabel("Finalizing…");
            bar.SetFraction(1.0);
            bar.SetText("100%");
            return;
        }
        statusLabel.SetLabel("Downloading…");
        if (p.TotalBytes.HasValue && p.TotalBytes.Value > 0)
        {
            double frac = Math.Clamp((double)p.DownloadedBytes / p.TotalBytes.Value, 0, 1);
            bar.SetFraction(frac);
            bar.SetText($"{frac * 100:F0}% — {FormatBytes(p.DownloadedBytes)} of {FormatBytes(p.TotalBytes.Value)}");
        }
        else
        {
            // Unknown total — pulse the bar so the user sees activity. yt-dlp emits NA for total_bytes on live streams and a few extractor edge cases.
            bar.Pulse();
            bar.SetText(FormatBytes(p.DownloadedBytes));
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }
        double kb = bytes / 1024.0;
        if (kb < 1024)
        {
            return $"{kb:F1} KiB";
        }
        double mb = kb / 1024.0;
        if (mb < 1024)
        {
            return $"{mb:F1} MiB";
        }
        double gb = mb / 1024.0;
        return $"{gb:F2} GiB";
    }

    private sealed class ProgressDialogHandle : IDisposable
    {
        private readonly Action close;
        private bool disposed;

        public ProgressDialogHandle(Action close)
        {
            this.close = close;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            close();
        }
    }
}
