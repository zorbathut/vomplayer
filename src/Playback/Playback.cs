using System;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Vomplayer.Mpv;

namespace Vomplayer.Playback;

// Owns MpvClient and the mapping from mpv events to strongly-typed observable state. No UI-framework dependency — the one cross-thread coupling is injected via Action<Action> (pass a GLib.Functions.IdleAdd wrapper in production, a => a() in tests). Never exposes MpvClient past the service boundary; the render surface receives it only inside the AttachRenderSurface callback.
public sealed partial class Playback : ObservableObject, IPlayback
{
    private readonly MpvClient mpv;
    private readonly Action<Action> postToMainThread;

    [ObservableProperty]
    private double positionSeconds;

    [ObservableProperty]
    private double durationSeconds;

    [ObservableProperty]
    private bool isPaused = true;

    [ObservableProperty]
    private bool isSeeking;

    // Latest decision derived from `video-params/gamma`. Drives the Wayland subsurface's PQ image-description toggle and mpv's target-* targeting. Kept here as a plain field (no ObservableProperty) because the only consumer is MainWindow, which subscribes to SourceHdrChanged directly — a full ObservableObject property would add IPlayback surface area for a concern that's purely internal to the render path.
    private bool isSourceHdr;

    public event Action? FileLoaded;
    public event Action<int>? FileEnded;
    // Fires on the main thread (via the same postToMainThread pump as other mpv property changes) whenever the source's HDR status flips. Reset to false on LoadFile and FileEnded so every file starts in a known SDR-safe state; the observer upgrades to true once mpv reports a `pq` or `hlg` gamma.
    public event Action<bool>? SourceHdrChanged;

    public Playback(Action<Action> postToMainThread)
    {
        if (postToMainThread == null)
        {
            throw new ArgumentNullException(nameof(postToMainThread));
        }
        this.postToMainThread = postToMainThread;
        mpv = new MpvClient();
    }

    public void Initialize()
    {
        // vo=libmpv defers VO selection until a render context is registered — otherwise mpv picks a default VO (on Wayland that's waylandvk, which ignores our render context and spawns its own window).
        mpv.SetOption("vo", "libmpv");
        mpv.SetOption("osc", "no");
        mpv.SetOption("keep-open", "yes");
        mpv.SetOption("terminal", "no");

        mpv.EventAvailable += OnMpvEventAvailable;
        mpv.PropertyChanged += OnMpvPropertyChanged;
        mpv.FileLoaded += OnMpvFileLoaded;
        mpv.FileEnded += OnMpvFileEnded;
        mpv.Shutdown += OnMpvShutdown;

        mpv.Initialize();

        mpv.ObserveProperty("time-pos", MpvFormat.Double);
        mpv.ObserveProperty("duration", MpvFormat.Double);
        mpv.ObserveProperty("pause", MpvFormat.Flag);
        mpv.ObserveProperty("seeking", MpvFormat.Flag);
        // Sub-property path observation: video-params is a Node map, but mpv exposes each scalar inside it (primaries, gamma, sig-peak, …) as its own string-typed observable when addressed via the "<parent>/<key>" syntax. This sidesteps MpvClient.ReadPropertyValue not knowing how to unpack node-map payloads. Initial synthesized fire lands with null (no file loaded yet), which IsHdrGamma classifies as SDR — no spurious transition.
        mpv.ObserveProperty("video-params/gamma", MpvFormat.String);
    }

    public void LoadFile(string path)
    {
        if (path == null)
        {
            throw new ArgumentNullException(nameof(path));
        }
        // Preemptive SDR reset: if the previous file was HDR, snap the surface and mpv targeting back to sRGB before the new file's params land. The observer upgrades to HDR again if the new file is PQ/HLG. Without this, an HDR→SDR playlist swap would leave the subsurface PQ-tagged while mpv decodes SDR content, producing the exact overdrive we're avoiding.
        UpdateSourceHdr(null);
        mpv.Command("loadfile", path);
        mpv.SetProperty("pause", "no");
    }

    // Called after the Wayland color-management shim attaches a PQ/BT.2020 description to the subsurface — libplacebo must then render to match, or the compositor will misinterpret sRGB-encoded output as PQ. If HDR is never applied (non-Linux, X11, compositor without wp-color-management-v1, or current source is SDR), we never call this and mpv keeps its sRGB-target default.
    //
    // Dispatched to the thread pool because mpv_set_property_string for target-* blocks the caller until the render context has acknowledged the change. The acknowledgement comes from mpv_render_context_render, which we call from the main thread via an IdleAdd queue. Calling SetProperty from the main thread would block that IdleAdd from running → deadlock (observed: main thread in pthread_cond_wait inside mpv_set_property_string). The sequence of three sets must be ordered, so they run on a single background task; fire-and-forget is safe because mpv won't render with the new targets until all three land, and the next frame's render is gated on that.
    public void EnableHdrOutput()
    {
        Task.Run(() =>
        {
            mpv.SetProperty("target-prim", "bt.2020");
            mpv.SetProperty("target-trc", "pq");
            mpv.SetProperty("target-peak", "1000");
        });
    }

    // Inverse of EnableHdrOutput: drops the PQ targets so mpv falls back to its sRGB/bt.709 default for SDR output. Called when transitioning from HDR to SDR content (playlist switch) or when the --sdr override wants mpv to tonemap HDR content down to SDR. Reset is symmetric with EnableHdrOutput so the state stays coherent across transitions. Same deadlock-avoidance rationale as EnableHdrOutput — see that comment.
    public void DisableHdrOutput()
    {
        Task.Run(() =>
        {
            mpv.SetProperty("target-prim", "auto");
            mpv.SetProperty("target-trc", "auto");
            mpv.SetProperty("target-peak", "auto");
        });
    }

    public void TogglePause()
    {
        mpv.SetProperty("pause", IsPaused ? "no" : "yes");
    }

    public void Stop()
    {
        mpv.Command("stop");
    }

    public void Seek(double seconds)
    {
        var target = seconds.ToString("F3", CultureInfo.InvariantCulture);
        mpv.Command("seek", target, "absolute");
    }

    // Hands the MpvClient to a render-surface attacher. Keeps MpvClient behind the service boundary — the caller receives the client only inside the callback scope. Used by both the GLArea path (client => videoView.AttachClient(client)) and the Wayland subsurface path (client => videoSurface.SetMpvClient(client)).
    public void AttachRenderSurface(Action<MpvClient> attach)
    {
        if (attach == null)
        {
            throw new ArgumentNullException(nameof(attach));
        }
        attach(mpv);
    }

    // Callable for tests. In production it's invoked indirectly via postToMainThread.
    public void ProcessEvents()
    {
        mpv.DrainEvents();
    }

    private void OnMpvEventAvailable()
    {
        postToMainThread(ProcessEvents);
    }

    private void OnMpvPropertyChanged(PropertyChange change)
    {
        switch (change.Name)
        {
            case "time-pos":
                PositionSeconds = change.Value.AsDouble ?? 0;
                break;
            case "duration":
                DurationSeconds = change.Value.AsDouble ?? 0;
                break;
            case "pause":
                IsPaused = change.Value.AsFlag ?? true;
                break;
            case "seeking":
                IsSeeking = change.Value.AsFlag ?? false;
                break;
            case "video-params/gamma":
                UpdateSourceHdr(change.Value.AsString);
                break;
            default:
                // Log-and-skip rather than throw: MpvClient.PropertyChanged is a broadcast and an unrecognized name here would otherwise take down the whole event-drain loop. A future observer on the same client shouldn't be able to ambush us.
                Console.Error.WriteLine($"[vomplayer] unexpected mpv property change: {change.Name}");
                break;
        }
    }

    // Pure predicate: mpv normalizes all container-level HDR transfer-function tags to one of these two canonical names before exposing them through the `gamma` property. Camera-log variants (v-log, s-log*) and cinema formats (st428) are intentionally excluded — they are high-dynamic-range in a different sense (log encoding, not display-referred PQ/HLG) and tagging them as PQ would display them as absolute-luminance nonsense.
    internal static bool IsHdrGamma(string? gamma)
    {
        return gamma == "pq" || gamma == "hlg";
    }

    // Internal so PlaybackTests can drive transition behavior directly without spinning up mpv's event pump (see test file comment). Keeps the higher-level dispatcher (OnMpvPropertyChanged) private — only the minimum transition surface is exposed.
    internal void UpdateSourceHdr(string? gamma)
    {
        bool newValue = IsHdrGamma(gamma);
        if (newValue == isSourceHdr)
        {
            return;
        }
        isSourceHdr = newValue;
        // Defer the event via postToMainThread so the handler doesn't run inside mpv_wait_event's DrainEvents loop. MainWindow.OnSourceHdrChanged calls mpv.SetProperty("target-prim"/...) which synchronously waits for mpv's core thread; the core thread is waiting for the render thread to process an update-request; the render thread is the same main thread still trapped in DrainEvents. Punting the event to a fresh main-loop iteration breaks that cycle. In tests postToMainThread is a => a(), so the fire stays synchronous and assertions still see it.
        postToMainThread(() => SourceHdrChanged?.Invoke(newValue));
    }

    private void OnMpvFileLoaded()
    {
        FileLoaded?.Invoke();
    }

    private void OnMpvFileEnded(int reason)
    {
        // Reset HDR state back to SDR on every file-end, including error-path ends where no new file will follow. Keeps the subsurface from lingering in a PQ-tagged state after playback stops.
        UpdateSourceHdr(null);
        FileEnded?.Invoke(reason);
    }

    private void OnMpvShutdown()
    {
        Console.Error.WriteLine("[vomplayer] mpv signalled shutdown");
    }

    public void Dispose()
    {
        // Unsubscribe from mpv before disposing it so any late dispatch can't land on this object's handlers. Then null our own event invocation lists so a subscriber we forward to can't fire into a torn-down state. Finally dispose mpv.
        mpv.EventAvailable -= OnMpvEventAvailable;
        mpv.PropertyChanged -= OnMpvPropertyChanged;
        mpv.FileLoaded -= OnMpvFileLoaded;
        mpv.FileEnded -= OnMpvFileEnded;
        mpv.Shutdown -= OnMpvShutdown;
        FileLoaded = null;
        FileEnded = null;
        SourceHdrChanged = null;
        mpv.Dispose();
    }
}
