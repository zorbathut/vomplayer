using System;
using Vomplayer.ViewModels;
using Vomplayer.Wayland;

namespace Vomplayer.Controls;

// Toggleable diagnostic panel for hwdec / source-HDR / output-HDR / VRR. The underlying Widget is added as an overlay child of MainWindow.videoOverlay, anchored top-right. Updates are timer-driven (1 Hz) rather than observable-bound: the overlay's commit cadence must stay well below KWin's VRR-engage hysteresis, and binding to per-frame properties like time-pos would defeat that.
//
// Reads from a Func<VideoContext> provider so MainWindow can swap which context the overlay reflects when the SelectedSlot moves (today's provider returns viewModel.SingleTarget). The VideoSurface accessor is similarly callback-shaped so the surface follows the active context too.
//
// Not a Gtk.Box subclass: GirCore's GObject subclassing story is fragile. Composition over inheritance — we own a Gtk.Box and expose it via Widget.
public sealed class DiagnosticOverlay : IDisposable
{
    private const uint UpdateIntervalMs = 1000;

    private readonly Func<VideoContext> activeContextProvider;
    private readonly Func<VideoSurface?> activeVideoSurfaceProvider;
    private readonly Func<PipSyncDiagnostic> syncDiagnosticProvider;
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

    public DiagnosticOverlay(Func<VideoContext> activeContextProvider, Func<VideoSurface?> activeVideoSurfaceProvider, Func<PipSyncDiagnostic> syncDiagnosticProvider)
    {
        if (activeContextProvider == null)
        {
            throw new ArgumentNullException(nameof(activeContextProvider));
        }
        if (activeVideoSurfaceProvider == null)
        {
            throw new ArgumentNullException(nameof(activeVideoSurfaceProvider));
        }
        if (syncDiagnosticProvider == null)
        {
            throw new ArgumentNullException(nameof(syncDiagnosticProvider));
        }
        this.activeContextProvider = activeContextProvider;
        this.activeVideoSurfaceProvider = activeVideoSurfaceProvider;
        this.syncDiagnosticProvider = syncDiagnosticProvider;

        box = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        box.SetHalign(Gtk.Align.End);
        box.SetValign(Gtk.Align.Start);
        box.SetHexpand(false);
        box.SetVexpand(false);
        // Clicks / pointer events fall through to widgets underneath. No EventController is attached, so we don't need a Wayland input-region dance here; CanTarget=false is enough for GTK-side hit-testing.
        box.SetCanTarget(false);
        box.AddCssClass("vompl-diagnostic");

        labels = new Gtk.Label[9];
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
        DumpHwdecTranscript();
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

    // Prints the active context's hwdec transcript to stdout — the negotiation trail mpv reported during the current file's load (or live since that load if hwdec re-negotiated). Useful for diagnosing hwdec=no on a file when smplayer / vlc / etc. handle it: the lines explain which backends were tried and why they were rejected. Will move into the overlay UI once the format is compacted; for now stdout is the cheap path and the user opted into seeing it by toggling the overlay.
    private void DumpHwdecTranscript()
    {
        var ctx = activeContextProvider();
        var transcript = ctx.Playback.HwdecTranscript;
        Console.WriteLine($"[vomplayer] hwdec transcript ({transcript.Count} lines):");
        if (transcript.Count == 0)
        {
            Console.WriteLine("  (empty — no hwdec-related log lines captured for the current file)");
            return;
        }
        foreach (var line in transcript)
        {
            Console.WriteLine($"  {line}");
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
        var ctx = activeContextProvider();
        var surface = activeVideoSurfaceProvider();
        VrrClassification vrrClass = VrrClassification.Unknown;
        int measuredHzCenti = 0;
        bool? displayHdr = ctx.HdrSink?.CurrentOutputIsHdr;
        bool isWaylandPath = surface != null;
        if (surface != null)
        {
            vrrClass = surface.GetVrrClassification();
            measuredHzCenti = surface.VrrMeasuredHzCenti;
        }
        VrrRange? outputVrrRange = ctx.VrrSink?.CurrentOutputVrrRange;
        string? connectorName = surface?.CurrentOutputConnectorName;
        return new DiagnosticSnapshot(
            Hwdec: ctx.Playback.HwdecCurrent,
            IsSourceHdr: ctx.Playback.IsSourceHdr,
            DisplayIsHdr: displayHdr,
            HdrActive: ctx.ActiveHdrState == VideoContext.HdrActiveState.Hdr,
            VrrClass: vrrClass,
            VrrMeasuredHzCenti: measuredHzCenti,
            IsWaylandPath: isWaylandPath,
            SourceFps: ctx.Playback.VideoFps,
            EstimatedVfFps: ctx.Playback.EstimatedVfFps,
            IsSourceFpsTrusted: ctx.Playback.IsSourceFpsTrusted,
            FpsTrustReason: ctx.Playback.FpsTrustReason,
            OutputVrrRange: outputVrrRange,
            ConnectorName: connectorName,
            LastDecision: ctx.LastVrrDecision,
            Sync: syncDiagnosticProvider());
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
