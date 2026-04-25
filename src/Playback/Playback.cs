using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Vomplayer.Mpv;

namespace Vomplayer.Playback;

// Owns MpvDispatcher and the mapping from mpv events to strongly-typed observable state. No UI-framework dependency — the cross-thread coupling to the UI is injected via Action<Action> (pass a GLib.Functions.IdleAdd wrapper in production, a => a() in tests). Mpv client-API calls all route through dispatcher.Post; the ref-struct MpvHandle pattern makes direct mpv access unreachable from outside a Post callback.
public sealed partial class Playback : ObservableObject, IPlayback
{
    private readonly MpvDispatcher dispatcher;
    private readonly Action<Action> postToMainThread;

    [ObservableProperty]
    private double positionSeconds;

    [ObservableProperty]
    private double durationSeconds;

    [ObservableProperty]
    private bool isPaused = true;

    [ObservableProperty]
    private bool isSeeking;

    // Decoder mpv actually selected, as reported by the `hwdec-current` property. Authoritative: if a hwdec backend fails to initialize for a given file mpv falls back to software and updates this to "no", so the value reflects the real decode path, not the requested one. Empty string or "no" ⇒ software; names like "vaapi", "nvdec", "videotoolbox", "d3d11va" ⇒ hardware.
    [ObservableProperty]
    private string? hwdecCurrent;

    // Latest decision derived from `video-params/gamma`. Drives the Wayland subsurface's PQ image-description toggle and mpv's target-* targeting. Kept here as a plain field (no ObservableProperty) because the only consumer is MainWindow, which subscribes to SourceHdrChanged directly — a full ObservableObject property would add IPlayback surface area for a concern that's purely internal to the render path.
    private bool isSourceHdr;

    // Public read-only view of isSourceHdr. Used by DiagnosticOverlay's 1 Hz poller; transition notification still goes through SourceHdrChanged. Not promoted to IPlayback because the VM has no consumer and the overlay reads the concrete Playback directly.
    public bool IsSourceHdr
    {
        get
        {
            return isSourceHdr;
        }
    }

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
        dispatcher = new MpvDispatcher();
        // Events fire on the dispatcher thread (post-DrainEvents). Marshal onto the UI main thread before touching ObservableObject properties.
        dispatcher.PropertyChanged += c => postToMainThread(() => OnMpvPropertyChanged(c));
        dispatcher.FileLoaded += () => postToMainThread(OnMpvFileLoaded);
        dispatcher.FileEnded += e => postToMainThread(() => OnMpvFileEnded(e));
        dispatcher.Shutdown += () => postToMainThread(OnMpvShutdown);
    }

    public void Initialize()
    {
        dispatcher.Post(h =>
        {
            // vo=libmpv defers VO selection until a render context is registered — otherwise mpv picks a default VO (on Wayland that's waylandvk, which ignores our render context and spawns its own window).
            h.SetOption("vo", "libmpv");
            h.SetOption("osc", "no");
            h.SetOption("keep-open", "yes");
            h.SetOption("terminal", "no");
            // auto-safe is mpv's curated set of hwdec backends that are known to work with GL interop on the current platform/driver combination — includes vaapi, nvdec, videotoolbox, d3d11va, plus their copy-back variants where the zero-copy path is unavailable. Unlike "auto" it excludes blacklisted driver/backend combos; unlike hand-picking a backend it degrades gracefully to software when nothing is available. Must be set before Initialize(); runtime changes also work but the startup path is simpler. Fallback is automatic — if the selected backend fails on a specific file mpv drops to software and updates hwdec-current accordingly.
            h.SetOption("hwdec", "auto-safe");

            // VOMPL_LOG_MPV=<path> routes mpv's full log to that file at -v level. Needed for debugging hwdec negotiation (which backends were tried, why they were rejected) and anything else mpv normally prints to its terminal — we run with terminal=no, so there's no other way to see this output. Path is taken literally; no ~ expansion. Value must be a writable path; mpv errors out if it isn't.
            var mpvLogPath = Environment.GetEnvironmentVariable("VOMPL_LOG_MPV");
            if (!string.IsNullOrEmpty(mpvLogPath))
            {
                h.SetOption("log-file", mpvLogPath);
                h.SetOption("msg-level", "all=v");
            }

            h.Initialize();

            h.ObserveProperty("time-pos", MpvFormat.Double);
            h.ObserveProperty("duration", MpvFormat.Double);
            h.ObserveProperty("pause", MpvFormat.Flag);
            h.ObserveProperty("seeking", MpvFormat.Flag);
            // Sub-property path observation: video-params is a Node map, but mpv exposes each scalar inside it (primaries, gamma, sig-peak, …) as its own string-typed observable when addressed via the "<parent>/<key>" syntax. This sidesteps MpvClient.ReadPropertyValue not knowing how to unpack node-map payloads. Initial synthesized fire lands with null (no file loaded yet), which IsHdrGamma classifies as SDR — no spurious transition.
            h.ObserveProperty("video-params/gamma", MpvFormat.String);
            // Observe the actual decoder mpv selected, not the requested one. Fires on FileLoaded (mpv resolves hwdec after probing the file) and again if negotiation falls back mid-playback. The synthesized initial event lands with "no" or empty before any file loads.
            h.ObserveProperty("hwdec-current", MpvFormat.String);
        });
    }

    public void LoadFile(string path)
    {
        if (path == null)
        {
            throw new ArgumentNullException(nameof(path));
        }
        // Preemptive SDR reset: if the previous file was HDR, snap the surface and mpv targeting back to sRGB before the new file's params land. The observer upgrades to HDR again if the new file is PQ/HLG. Without this, an HDR→SDR playlist swap would leave the subsurface PQ-tagged while mpv decodes SDR content, producing the exact overdrive we're avoiding.
        UpdateSourceHdr(null);
        dispatcher.Post(h =>
        {
            h.Command("loadfile", path);
            h.SetProperty("pause", "no");
        });
    }

    // Called after the Wayland color-management shim attaches a PQ/BT.2020 description to the subsurface. mpv's gl_video pipeline must then emit PQ pass-through (no tone-map within mpv) so the compositor's libplacebo-backed pipeline can do PQ→SDR (or PQ→HDR pass-through, depending on output) using its own curves, which match smplayer/`vo=gpu-next` quality. If HDR is never applied (non-Linux, X11, compositor without wp-color-management-v1, or current source is SDR), we never call this and mpv keeps its `target-*=auto` defaults so gl_video can fall back to its own tone-map.
    //
    // target-peak is set to 10000 (full PQ range) rather than the file's mastering peak: gl_video tone-maps when src.max_luma > dst.max_luma, so any pin lower than the source's mastering peak (1000 for typical HDR10, up to 4000 for UHD Blu-ray, 10000 for HDR10+) would cascade gl_video's curve INTO our PQ output before the compositor's curve runs again, defeating the point of delegation. 10000 is the largest PQ-encodeable peak, so any source mastering ≤ 10000 nits passes through untouched.
    //
    // Goes through dispatcher.Post because mpv_set_property_string for target-* blocks the caller until the render context has acknowledged the change. The ack comes via mpv_render_context_render, which runs on the main thread — so calling SetProperty from main would deadlock. The dispatcher serializes these onto its worker thread, where the block is harmless because the main thread stays free to service the render callback mpv core is waiting on.
    public void EnableHdrOutput()
    {
        dispatcher.Post(h =>
        {
            h.SetProperty("target-prim", "bt.2020");
            h.SetProperty("target-trc", "pq");
            h.SetProperty("target-peak", "10000");
        });
    }

    // Inverse of EnableHdrOutput: drops the PQ targets so mpv falls back to its auto target-* defaults. Tried pinning target-trc=gamma2.2 etc. for symmetry with the surface's GAMMA22/BT.709 tag, but that empirically broke the SDR-display HDR-source case again — auto here works, gamma2.2 pinned does not, mechanism unknown (in theory both should resolve to identical dst.transfer for the gl_video pipeline; reality disagrees). Stick with auto until someone instruments the divergence. Same deadlock-avoidance rationale as EnableHdrOutput — see that comment.
    public void DisableHdrOutput()
    {
        dispatcher.Post(h =>
        {
            h.SetProperty("target-prim", "auto");
            h.SetProperty("target-trc", "auto");
            h.SetProperty("target-peak", "auto");
        });
    }

    public void TogglePause()
    {
        bool pausedNow = IsPaused;
        dispatcher.Post(h => h.SetProperty("pause", pausedNow ? "no" : "yes"));
    }

    public void Stop()
    {
        dispatcher.Post(h => h.Command("stop"));
    }

    public void Seek(double seconds)
    {
        var target = seconds.ToString("F3", CultureInfo.InvariantCulture);
        dispatcher.Post(h => h.Command("seek", target, "absolute"));
    }

    // Hands the MpvDispatcher to a render-surface attacher. Consumers call CreateRenderContext on it (which runs on the caller's GL-owning thread) rather than accessing an MpvClient directly. Internal because MpvDispatcher is internal — this seam is for same-assembly render surfaces only.
    internal void AttachRenderSurface(Action<MpvDispatcher> attach)
    {
        if (attach == null)
        {
            throw new ArgumentNullException(nameof(attach));
        }
        attach(dispatcher);
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
            case "hwdec-current":
                UpdateHwdecCurrent(change.Value.AsString);
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

    // Internal so PlaybackTests can drive the state change without spinning up mpv's event pump. Logs only on real transitions so a file-loaded spam doesn't pollute stderr; the log is the current user-visible "did hwaccel work" signal until a UI surface consumes HwdecCurrent. Null and empty are coalesced because mpv reports both forms depending on context (initial synthesized fire vs no-hwdec state).
    internal void UpdateHwdecCurrent(string? value)
    {
        var normalized = string.IsNullOrEmpty(value) ? null : value;
        if (normalized == HwdecCurrent)
        {
            return;
        }
        HwdecCurrent = normalized;
        Console.Error.WriteLine($"[vomplayer] hwdec: {normalized ?? "(none)"}");
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
        // Null our own event invocation lists so a subscriber we forward to can't fire into a torn-down state. Dispose of the dispatcher — its Dispose drops subscriptions to the underlying MpvClient, drains the queue, joins the worker, and tears down mpv.
        FileLoaded = null;
        FileEnded = null;
        SourceHdrChanged = null;
        dispatcher.Dispose();
    }
}
