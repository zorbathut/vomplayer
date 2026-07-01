using System;
using Vomplayer.Services;

namespace Vomplayer.Controls;

// In-window download-status card composited over the video (an overlay child of MainWindow.videoOverlay, same mechanism as DiagnosticOverlay). Replaces the old modal progress dialog: shown immediately when a URL load starts, non-modal (never grabs focus, never blocks the playlist / other stream), anchored top-left so it clears the top-right DiagnosticOverlay, the bottom controlsBox, and — critically — the video-center double-click-to-fullscreen gesture.
//
// Not a Gtk.Box subclass: GirCore's GObject subclassing story is fragile. Composition over inheritance — we own a Gtk.Box and expose it via Widget.
//
// One overlay is shared by both VideoContexts (Primary + Secondary PiP each drive the same UrlPromptGtk). Show hands out an IUrlStatusHandle that doubles as a generation token: only the most-recent handle is "current", and Report/Dispose from a stale handle are ignored. That way a finishing load can't tear down a still-running one's status. Consequence for the rare concurrent-download case: only the most-recently-started load's status is visible at a time, and if that newer load finishes first the older one keeps downloading with the overlay hidden and no longer cancellable through it (its load still completes normally). No leak or crash — just degraded feedback for a corner nobody hits in practice.
public sealed class DownloadStatusOverlay : IDisposable
{
    // Pulse cadence for the indeterminate (probe / classify / unknown-size download) phase. GtkProgressBar.Pulse advances one step per call, so an animated "busy" bar needs a repeating tick.
    private const uint PulseIntervalMs = 120;

    private readonly Gtk.Box box;
    private readonly Gtk.Label statusLabel;
    private readonly Gtk.ProgressBar bar;
    private readonly Gtk.Button cancelButton;

    private Handle? current;
    private Action? onCancel;
    private bool indeterminate;
    private uint pulseTimeoutId;
    private bool disposed;

    public Gtk.Widget Widget
    {
        get
        {
            return box;
        }
    }

    public DownloadStatusOverlay()
    {
        box = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        box.SetHalign(Gtk.Align.Start);
        box.SetValign(Gtk.Align.Start);
        box.SetHexpand(false);
        box.SetVexpand(false);
        box.AddCssClass("vompl-download-status");
        // Unlike DiagnosticOverlay, this box does NOT SetCanTarget(false): the Cancel button has to be clickable, and a can-target=false parent can't reliably hand events to its children. The trade is that the small top-left card swallows clicks over its own area (label/bar/padding) instead of falling through to the video — acceptable because it's compact, corner-anchored, and only present during a load, well clear of the center double-click-to-fullscreen gesture.

        statusLabel = Gtk.Label.New("");
        statusLabel.SetXalign(0);
        statusLabel.SetWrap(true);
        box.Append(statusLabel);

        bar = Gtk.ProgressBar.New();
        bar.SetShowText(true);
        bar.SetText("");
        box.Append(bar);

        cancelButton = Gtk.Button.NewWithLabel("Cancel");
        cancelButton.SetHalign(Gtk.Align.End);
        // Mouse-only. Keep the button off the focus chain so a stray Space — which the window-level capture-phase key controller routes to PlayPause before any child sees it — can never activate it. This is the crux of the "Space cancels my download" fix: the old modal was a separate top-level outside that controller's reach.
        cancelButton.SetFocusable(false);
        cancelButton.OnClicked += (_, _) =>
        {
            cancelButton.SetSensitive(false);
            statusLabel.SetLabel("Cancelling…");
            onCancel?.Invoke();
        };
        box.Append(cancelButton);

        box.SetVisible(false);
    }

    // Show/refresh the overlay with statusText in an indeterminate busy state, returning a fresh handle that becomes "current". Cancel is shown iff onCancel != null. Called on the GTK main thread (from UrlPromptGtk, on the coordinator's main-thread continuation), so the handle's Progress latches the main sync context and download ticks marshal back here safely.
    public IUrlStatusHandle Show(string statusText, Action? onCancel)
    {
        if (disposed)
        {
            // Torn-down window: hand back an inert handle so the caller's Dispose is a no-op.
            return new Handle(null);
        }
        var handle = new Handle(this);
        current = handle;
        this.onCancel = onCancel;

        statusLabel.SetLabel(statusText);
        cancelButton.SetVisible(onCancel != null);
        cancelButton.SetSensitive(true);

        indeterminate = true;
        bar.SetText("");
        bar.Pulse();
        EnsurePulseTimer();

        box.SetVisible(true);
        return handle;
    }

    // A download tick from the handle's Progress sink. Ignored unless handle is the current one (generation guard).
    private void ReportFrom(Handle handle, UrlDownloadProgress p)
    {
        if (disposed || !ReferenceEquals(handle, current))
        {
            return;
        }
        if (p.Status == "finished")
        {
            // Download bytes are done, but post-processing (merge / mux / extract) can take real wall-time. Show a settled 100% during that gap so the user doesn't read the bar as hung.
            statusLabel.SetLabel("Finalizing…");
            indeterminate = false;
            StopPulseTimer();
            bar.SetFraction(1.0);
            bar.SetText("100%");
            return;
        }
        statusLabel.SetLabel("Downloading…");
        if (p.TotalBytes.HasValue && p.TotalBytes.Value > 0)
        {
            indeterminate = false;
            StopPulseTimer();
            double frac = Math.Clamp((double)p.DownloadedBytes / p.TotalBytes.Value, 0, 1);
            bar.SetFraction(frac);
            bar.SetText($"{frac * 100:F0}% — {FormatBytes(p.DownloadedBytes)} of {FormatBytes(p.TotalBytes.Value)}");
        }
        else
        {
            // Unknown total (live streams, a few extractor edge cases emit NA) — keep the bar pulsing and label the running byte count.
            indeterminate = true;
            EnsurePulseTimer();
            bar.SetText(FormatBytes(p.DownloadedBytes));
        }
    }

    // Hide request from the handle's Dispose. Ignored unless handle is the current one — a stale context finishing must not hide a newer load's live status.
    private void HideFrom(Handle handle)
    {
        if (disposed || !ReferenceEquals(handle, current))
        {
            return;
        }
        current = null;
        onCancel = null;
        indeterminate = false;
        StopPulseTimer();
        box.SetVisible(false);
    }

    private void EnsurePulseTimer()
    {
        if (pulseTimeoutId == 0)
        {
            pulseTimeoutId = GLib.Functions.TimeoutAdd((int)GLib.Constants.PRIORITY_DEFAULT, PulseIntervalMs, OnPulseTick);
        }
    }

    private void StopPulseTimer()
    {
        if (pulseTimeoutId != 0)
        {
            GLib.Functions.SourceRemove(pulseTimeoutId);
            pulseTimeoutId = 0;
        }
    }

    private bool OnPulseTick()
    {
        if (disposed || !indeterminate)
        {
            pulseTimeoutId = 0;
            return false;
        }
        bar.Pulse();
        return true;
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

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        StopPulseTimer();
    }

    // Generation token + progress sink handed to one URL-load phase. Progress is constructed on the GTK main thread (see Show) so its Report callbacks marshal there; Dispose hides the overlay iff this handle is still current.
    private sealed class Handle : IUrlStatusHandle
    {
        private readonly DownloadStatusOverlay? overlay;
        private readonly Progress<UrlDownloadProgress> progress;
        private bool disposed;

        public Handle(DownloadStatusOverlay? overlay)
        {
            this.overlay = overlay;
            progress = new Progress<UrlDownloadProgress>(p => overlay?.ReportFrom(this, p));
        }

        public IProgress<UrlDownloadProgress> Progress
        {
            get
            {
                return progress;
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            overlay?.HideFrom(this);
        }
    }
}
