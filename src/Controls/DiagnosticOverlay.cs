using System;
using Vomplayer.Wayland;

namespace Vomplayer.Controls;

// Toggleable diagnostic panel for hwdec / source-HDR / output-HDR / VRR. The underlying Widget is added as an overlay child of MainWindow.videoOverlay, anchored top-right. Updates are timer-driven (1 Hz) rather than observable-bound: the overlay's commit cadence must stay well below KWin's VRR-engage hysteresis, and binding to per-frame properties like time-pos would defeat that. Reads data directly from the concrete Playback + (optional) VideoSurface — no VM plumbing.
//
// Not a Gtk.Box subclass: GirCore's GObject subclassing story is fragile. Composition over inheritance — we own a Gtk.Box and expose it via Widget.
public sealed class DiagnosticOverlay : IDisposable
{
    private const uint UpdateIntervalMs = 1000;

    private readonly Vomplayer.Playback.Playback playback;
    private readonly VideoSurface? videoSurface;
    private readonly Gtk.Box box;
    private readonly Gtk.Label[] labels;

    private uint timeoutId;
    private bool disposed;

    public Gtk.Widget Widget
    {
        get
        {
            return box;
        }
    }

    public DiagnosticOverlay(Vomplayer.Playback.Playback playback, VideoSurface? videoSurface)
    {
        if (playback == null)
        {
            throw new ArgumentNullException(nameof(playback));
        }
        this.playback = playback;
        this.videoSurface = videoSurface;

        box = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        box.SetHalign(Gtk.Align.End);
        box.SetValign(Gtk.Align.Start);
        box.SetHexpand(false);
        box.SetVexpand(false);
        // Clicks / pointer events fall through to widgets underneath. No EventController is attached, so we don't need a Wayland input-region dance here; CanTarget=false is enough for GTK-side hit-testing.
        box.SetCanTarget(false);
        box.AddCssClass("vom-diagnostic");

        labels = new Gtk.Label[4];
        for (int i = 0; i < labels.Length; i++)
        {
            var label = Gtk.Label.New("");
            label.SetXalign(0);
            labels[i] = label;
            box.Append(label);
        }

        box.SetVisible(false);
    }

    // Makes the overlay visible, arms the 1 Hz update timer, and refreshes the labels synchronously so the panel isn't blank for a second. Idempotent.
    public void Show()
    {
        if (disposed)
        {
            return;
        }
        Refresh();
        box.SetVisible(true);
        if (timeoutId == 0)
        {
            timeoutId = GLib.Functions.TimeoutAdd(
                (int)GLib.Constants.PRIORITY_DEFAULT,
                UpdateIntervalMs,
                OnTick);
        }
    }

    // Hides the overlay and cancels the timer. Idempotent.
    public void Hide()
    {
        if (timeoutId != 0)
        {
            GLib.Functions.SourceRemove(timeoutId);
            timeoutId = 0;
        }
        box.SetVisible(false);
    }

    private bool OnTick()
    {
        if (disposed)
        {
            timeoutId = 0;
            return false;
        }
        Refresh();
        return true;
    }

    private void Refresh()
    {
        var snapshot = Snapshot();
        var lines = DiagnosticFormatter.FormatLines(snapshot);
        for (int i = 0; i < labels.Length && i < lines.Length; i++)
        {
            labels[i].SetLabel(lines[i]);
        }
    }

    private DiagnosticSnapshot Snapshot()
    {
        VrrClassification vrrClass = VrrClassification.Unknown;
        int measuredHzCenti = 0;
        bool? outputHdr = null;
        bool isWaylandPath = videoSurface != null;
        if (videoSurface != null)
        {
            vrrClass = videoSurface.GetVrrClassification();
            measuredHzCenti = videoSurface.VrrMeasuredHzCenti;
            outputHdr = videoSurface.CurrentOutputIsHdr;
        }
        return new DiagnosticSnapshot(
            Hwdec: playback.HwdecCurrent,
            IsSourceHdr: playback.IsSourceHdr,
            OutputIsHdr: outputHdr,
            VrrClass: vrrClass,
            VrrMeasuredHzCenti: measuredHzCenti,
            IsWaylandPath: isWaylandPath);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        if (timeoutId != 0)
        {
            GLib.Functions.SourceRemove(timeoutId);
            timeoutId = 0;
        }
    }
}
