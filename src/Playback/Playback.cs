using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Vomplayer.Mpv;

namespace Vomplayer.Playback;

// Owns MpvDispatcher and the mapping from mpv events to strongly-typed observable state. No UI-framework dependency — the cross-thread coupling to the UI is injected via Action<Action> (pass a GLib.Functions.IdleAdd wrapper in production, a => a() in tests). Mpv client-API calls all route through dispatcher.Post; the ref-struct MpvHandle pattern makes direct mpv access unreachable from outside a Post callback.
public sealed partial class Playback : ObservableObject, IPlayback
{
    private readonly MpvDispatcher dispatcher;
    private readonly Action<Action> postToMainThread;
    // Set in Dispose. Gates OnMpvPropertyChanged because that handler calls dispatcher.Post directly (via ReloadTracks/ReloadChapters); the other main-thread handlers reach dispatcher.Post only through event subscribers, which Dispose nulls out before tearing down the dispatcher.
    private int disposed;

    [ObservableProperty]
    private double positionSeconds;

    [ObservableProperty]
    private double durationSeconds;

    [ObservableProperty]
    private bool isPaused = true;

    [ObservableProperty]
    private bool isSeeking;

    // Defaults to true: pre-Initialize / pre-LoadFile mpv has no file loaded and is "idle" by definition. The first synthesized property fire from mpv after Initialize() lands with the actual core-idle value (true until a file is loaded). Diverges from IsPaused — see IPlayback.IsCoreIdle for the rationale.
    [ObservableProperty]
    private bool isCoreIdle = true;

    // Mirror of mpv's `eof-reached`. Reset synchronously in LoadFile (preempting a stale carry-over from the prior file) so the playlist auto-advance handler's rising-edge gate is meaningful across file boundaries.
    [ObservableProperty]
    private bool isEofReached;

    // mpv reports `volume` in percent. Default 100 mirrors mpv's own default so the slider lands at full pre-Initialize and the first synthesized observe fire (which carries the real value) doesn't visibly jump.
    [ObservableProperty]
    private double volume = 100;

    [ObservableProperty]
    private bool isMuted;

    // Decoder mpv actually selected, as reported by the `hwdec-current` property. Authoritative: if a hwdec backend fails to initialize for a given file mpv falls back to software and updates this to "no", so the value reflects the real decode path, not the requested one. Empty string or "no" ⇒ software; names like "vaapi", "nvdec", "videotoolbox", "d3d11va" ⇒ hardware.
    [ObservableProperty]
    private string? hwdecCurrent;

    // Bounded transcript of mpv log lines whose prefix root matches HwdecLogPrefixRoots — i.e. the messages that explain *why* a hwdec backend was tried/picked/rejected. Captured at "v" level so the full negotiation path is visible. Cleared on LoadFile (via dispatcher epoch — see below) so each file's transcript starts fresh; bounded to TranscriptMaxLines via FIFO eviction so a chatty source can't grow it unbounded. The list is touched only on the main thread (LogMessageReceived is forwarded through postToMainThread upstream); the IPlayback getter has the same constraint.
    private const int TranscriptMaxLines = 256;
    private readonly Queue<string> hwdecTranscript = new(TranscriptMaxLines);
    public IReadOnlyList<string> HwdecTranscript
    {
        get
        {
            return hwdecTranscript.ToArray();
        }
    }

    // Per-LoadFile epoch, incremented on the dispatcher worker just before the loadfile command is issued, stamped onto each LogMessage at the moment dispatcher.LogMessageReceived fires (also dispatcher worker), and compared against mainLogEpoch on the main thread when a stamped message lands. The race this fixes: a synchronous main-thread Clear in LoadFile interleaves wrong with in-flight log events — A's tail messages are already queued to main idle by the time main calls Clear, then they land *after* the clear and pollute the new file's transcript. With the epoch carrier, ordering is enforced by the dispatcher worker FIFO (which sequences mpv emissions vs the loadfile command), and main only clears when an epoch-bumped message arrives.
    private int dispatcherLogEpoch;
    private int mainLogEpoch;

    // Prefix roots for mpv components that participate in hwdec negotiation. Match is "prefix == root || prefix starts with root + '/'", so `ffmpeg` catches `ffmpeg/h264`, `ffmpeg/vaapi_hwaccel`, etc. The hwdec rejection reason can land under any of these depending on the failure point: top-level "Trying X / X failed" lines under `vd`; codec-init rejections under `ffmpeg/<codec>`; backend-init rejections under the backend's own prefix or `ffmpeg/<hwaccel>`; format-conversion failures under `autoconvert`; VO consumer rejections under `vo`. Mac/Windows backends (`videotoolbox`, `d3d11va`) are unused on Linux but cost nothing to keep listed.
    private static readonly string[] HwdecLogPrefixRoots =
    {
        "vd",
        "vo",
        "ffmpeg",
        "lavc",
        "vaapi",
        "vdpau",
        "nvdec",
        "videotoolbox",
        "d3d11va",
        "cuda",
        "hwdec",
        "autoconvert",
    };

    internal static bool IsHwdecLogPrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return false;
        }
        foreach (var root in HwdecLogPrefixRoots)
        {
            if (prefix.Length == root.Length)
            {
                if (prefix == root)
                {
                    return true;
                }
            }
            else if (prefix.Length > root.Length
                && prefix[root.Length] == '/'
                && prefix.AsSpan(0, root.Length).SequenceEqual(root))
            {
                return true;
            }
        }
        return false;
    }

    // Snapshots of mpv's tracks per kind. Replaced wholesale (not mutated) so PropertyChanged signals "re-read everything". Driven by an observation on `track-list/count` — every add/remove of any track kind fires the count change, and we re-walk track-list/N/* on the dispatcher thread. A single dispatcher post drives the read so the three per-kind buckets come out of one walk, avoiding three round-trip passes that would each re-open the mid-walk-reorder window described on ReadTracksFromMpv. (Per-kind UI consumers still see the three property updates land sequentially on the main thread, not atomically — the comment used to claim otherwise.)
    [ObservableProperty]
    private IReadOnlyList<MediaTrack> videoTracks = Array.Empty<MediaTrack>();

    [ObservableProperty]
    private IReadOnlyList<MediaTrack> audioTracks = Array.Empty<MediaTrack>();

    [ObservableProperty]
    private IReadOnlyList<MediaTrack> subtitleTracks = Array.Empty<MediaTrack>();

    // Snapshot of mpv's chapter-list. Re-walked on chapter-list/count changes (covers file load + add/remove). Reset to empty on LoadFile / FileEnded so a stale chapter set from the previous file can never linger past a swap.
    [ObservableProperty]
    private IReadOnlyList<MediaChapter> chapters = Array.Empty<MediaChapter>();

    // Mirrors of mpv's `current-tracks/{video,audio,sub}/id`. Null when no track of that kind is active. Observed independently of track-list/count so the radio selection in each menu can update the moment the user picks a different track without waiting for a track-list change.
    [ObservableProperty]
    private int? currentVideoId;

    [ObservableProperty]
    private int? currentAudioId;

    [ObservableProperty]
    private int? currentSubtitleId;

    // Display aspect ratio of the loaded source's video stream, computed from mpv's `dwidth`/`dheight` properties (display dimensions, with sample aspect ratio applied — i.e. the rectangle to render the video into for square-pixel output). Null when no file is loaded or the file has no video stream. Consumed by the PiP layout to size the secondary video widget to its content's aspect.
    [ObservableProperty]
    private double? videoAspect;

    // Source's container frame rate from mpv's `container-fps` property. Null when no file is loaded, no video stream is present, or mpv reports an unusable value (≤ 0 or unavailable). Reset on LoadFile to null so the coordinator's StepFrame absolute-delta math doesn't briefly use the previous file's fps in the gap between LoadFile and the new file's first container-fps observation.
    [ObservableProperty]
    private double? videoFps;

    // Mirror of mpv's `estimated-vf-fps`, the rolling-average decoded frame rate. Drives the FPS trust monitor; surfaced via EstimatedVfFps for the diagnostic overlay. Null pre-load and on audio-only files.
    [ObservableProperty]
    private double? estimatedVfFps;

    // Mirror of mpv's `media-title`. Null on no-file-loaded; reset preemptively in LoadFile so the previous file's title can't survive past a swap (it would otherwise linger until mpv's first observation lands on the new file).
    [ObservableProperty]
    private string? mediaTitle;

    // Most-recent dwidth / dheight values, used to recompute VideoAspect when either lands. Both must be present and positive for the aspect to be derivable; otherwise VideoAspect goes back to null.
    private long? lastDwidth;
    private long? lastDheight;

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

    // Trust monitor for the declared container-fps. Reset at LoadFile (declared=0 makes it inert) and again when container-fps lands with a real value. Estimated-vf-fps observations feed into it; transitions to Untrusted fire IsSourceFpsTrustedChanged (deduped against the last published bool).
    private readonly FpsTrustMonitor fpsTrustMonitor = new();
    private readonly Func<double> nowSecondsProvider;
    // Mirror of fpsTrustMonitor.State for cheap reads from outside (no lock; main-thread-only). Defaults true so the initial state matches a freshly-loaded CFR file. Synced into the monitor by InvalidateTrustState whenever the underlying state could have changed.
    private bool isSourceFpsTrusted = true;
    // Tracks the previous IsSeeking value across observations so we can detect the true→false edge (seek-end) and feed the monitor's warmup reset.
    private bool wasSeeking;

    public bool IsSourceFpsTrusted
    {
        get
        {
            return isSourceFpsTrusted;
        }
    }

    public string FpsTrustReason
    {
        get
        {
            return fpsTrustMonitor.LastReason;
        }
    }

    public event Action? FileLoaded;
    public event Action<int>? FileEnded;
    public event Action? TracksReloaded;
    // Fires on the main thread (via the same postToMainThread pump as other mpv property changes) whenever the source's HDR status flips. Reset to false on LoadFile and FileEnded so every file starts in a known SDR-safe state; the observer upgrades to true once mpv reports a `pq` or `hlg` gamma.
    public event Action<bool>? SourceHdrChanged;
    // Fires on the main thread when the FPS trust state transitions. Sticky-Untrusted within a file load means at most one transition per file in the trusted→untrusted direction; the inverse (re-trust on a new LoadFile) also fires.
    public event Action<bool>? IsSourceFpsTrustedChanged;

    public Playback(Action<Action> postToMainThread)
        : this(postToMainThread, MakeDefaultClock())
    {
    }

    // Test seam: tests pass a synthetic clock so trust-monitor warmup/sustained-disagreement timing is deterministic. Production default is a per-instance Stopwatch (Primary and Secondary PiP get independent origins, but each monitor reads only relative-to-its-own-origin durations so it doesn't matter).
    internal Playback(Action<Action> postToMainThread, Func<double> nowSecondsProvider)
    {
        if (postToMainThread == null)
        {
            throw new ArgumentNullException(nameof(postToMainThread));
        }
        if (nowSecondsProvider == null)
        {
            throw new ArgumentNullException(nameof(nowSecondsProvider));
        }
        this.postToMainThread = postToMainThread;
        this.nowSecondsProvider = nowSecondsProvider;
        dispatcher = new MpvDispatcher();
        // Events fire on the dispatcher thread (post-DrainEvents). Marshal onto the UI main thread before touching ObservableObject properties.
        dispatcher.PropertyChanged += c => postToMainThread(() => OnMpvPropertyChanged(c));
        dispatcher.FileLoaded += () => postToMainThread(OnMpvFileLoaded);
        dispatcher.FileEnded += e => postToMainThread(() => OnMpvFileEnded(e));
        dispatcher.LogMessageReceived += OnDispatcherLogMessage;
        dispatcher.Shutdown += () => postToMainThread(OnMpvShutdown);
    }

    private static Func<double> MakeDefaultClock()
    {
        var sw = Stopwatch.StartNew();
        return () => sw.Elapsed.TotalSeconds;
    }

    // Runs on the dispatcher worker. Filters by prefix root *before* paying for the cross-thread post — at "v" level mpv emits dozens-to-hundreds of lines per second on a malformed source, and hwdec-only matches are typically <1% of that. Stamps the current epoch onto each accepted message so main-thread receipt can correlate it against the LoadFile boundary (see the dispatcherLogEpoch / mainLogEpoch comments for the race rationale).
    private void OnDispatcherLogMessage(LogMessage message)
    {
        if (!IsHwdecLogPrefix(message.Prefix))
        {
            return;
        }
        var epoch = dispatcherLogEpoch;
        postToMainThread(() => OnMpvLogMessage(message, epoch));
    }

    public void Initialize()
    {
        dispatcher.Post(h =>
        {
            // vo=libmpv defers VO selection until a render context is registered — otherwise mpv picks a default VO (on Wayland that's waylandvk, which ignores our render context and spawns its own window).
            h.SetOption("vo", "libmpv");
            // `osc` is registered by mpv's built-in Lua scripts. Builds without Lua (e.g. our flatpak's slim libmpv) compile osc out and return OPTION_NOT_FOUND. Lua-less mpv defaults to no OSC anyway, so the desired behavior is already in place — silently swallow the not-found error rather than aborting initialization.
            try { h.SetOption("osc", "no"); } catch (MpvException ex) when (ex.Code == (int)MpvErrorCode.OptionNotFound) { }
            h.SetOption("keep-open", "yes");
            h.SetOption("terminal", "no");
            // auto-safe is mpv's curated set of hwdec backends that are known to work with GL interop on the current platform/driver combination — includes vaapi, nvdec, videotoolbox, d3d11va, plus their copy-back variants where the zero-copy path is unavailable. Unlike "auto" it excludes blacklisted driver/backend combos; unlike hand-picking a backend it degrades gracefully to software when nothing is available. Must be set before Initialize(); runtime changes also work but the startup path is simpler. Fallback is automatic — if the selected backend fails on a specific file mpv drops to software and updates hwdec-current accordingly.
            h.SetOption("hwdec", "auto-safe");

            // Default video-sync=audio is preserved deliberately for VRR — display-resample and interpolation both pin presentation to display refresh and defeat VRR per mpv issues #6137 / #15748. The `vf=fps=...` filter is applied at runtime by VideoContext.ApplyVrrPolicy via Command("vf", "set", ...) once we know the source FPS and the current output's VRR window; nothing is set at init.

            // Pin volume-max to 100 so the [0, 100] slider range is the authoritative contract: any `add volume +5` past 100 clamps in mpv (rather than letting mpv's default 130 amplify past what the UI can display, which would produce silent gain-above-1 the user couldn't see). If a future per-source amplification feature wants headroom, raise volume-max and widen the slider together.
            h.SetOption("volume-max", "100");

            // mpv has a per-prefix `msg-level` filter that runs BEFORE mpv_request_log_messages decides what to deliver to client-API consumers, and the default is `ffmpeg=warn` — so AV_LOG_VERBOSE messages from ffmpeg's hwdec backends (which is where the actual rejection reasons live: "Cannot open DRM device /dev/dri/renderD128: <errno>", "vaInitialize returned -3 (unknown libva error)", etc.) are dropped server-side. Setting all=v lifts that filter so the transcript actually surfaces the *why* of a failed hwdec negotiation. Cost is some extra noise from non-hwdec subsystems, but the dispatcher-side prefix filter on the client side drops those before they cross threads.
            h.SetOption("msg-level", "all=v");

            // VOMPL_LOG_MPV=<path> additionally routes the full v-level log to that file. Needed for offline debugging or for capturing logs across a session that's longer than the in-process transcript ring. Path is taken literally; no ~ expansion. Value must be a writable path; mpv errors out if it isn't.
            var mpvLogPath = Environment.GetEnvironmentVariable("VOMPL_LOG_MPV");
            if (!string.IsNullOrEmpty(mpvLogPath))
            {
                h.SetOption("log-file", mpvLogPath);
            }

            // Stream log messages to us at "v" level so the hwdec transcript can capture the full negotiation trail (vd / backend / ffmpeg lines that explain why a backend was picked or rejected). Captured into a bounded ring inside OnMpvLogMessage; the diagnostic overlay surfaces it. Independent of VOMPL_LOG_MPV — the env var routes to a file via mpv's log-file option, this routes via the client-API event stream and stays in-process. Called BEFORE Initialize per mpv's documentation, which explicitly notes "You can call this on a uninitialized handle" and that doing so is required to receive any messages emitted during initialization itself (which includes the first hwdec auto-probe lines on `vo=libmpv` / `hwdec=auto-safe` setup).
            h.RequestLogMessages("v");

            h.Initialize();

            h.ObserveProperty("time-pos", MpvFormat.Double);
            h.ObserveProperty("duration", MpvFormat.Double);
            h.ObserveProperty("pause", MpvFormat.Flag);
            h.ObserveProperty("seeking", MpvFormat.Flag);
            // Volume + mute are user-controlled but also writable from anywhere (CLI keybinds, lua scripts, mpv's own ao-volume tracking) — observe so the UI follows external changes too. The synthesized initial fire lands with mpv's real defaults (volume=100, mute=no).
            h.ObserveProperty("volume", MpvFormat.Double);
            h.ObserveProperty("mute", MpvFormat.Flag);
            // core-idle differs from `pause` precisely at end-of-file with keep-open=yes: pause stays no, but the playback core stops advancing. Consumers that need "actually decoding/displaying right now" (e.g. screensaver inhibit) should track this rather than IsPaused.
            h.ObserveProperty("core-idle", MpvFormat.Flag);
            // eof-reached observed for playlist auto-advance — see IPlayback.IsEofReached. With keep-open=yes mpv doesn't fire MPV_EVENT_END_FILE on natural EOF, so this property (which DOES flip true at EOF regardless of keep-open) is the reliable signal.
            h.ObserveProperty("eof-reached", MpvFormat.Flag);
            // Sub-property path observation: video-params is a Node map, but mpv exposes each scalar inside it (primaries, gamma, sig-peak, …) as its own string-typed observable when addressed via the "<parent>/<key>" syntax. This sidesteps MpvClient.ReadPropertyValue not knowing how to unpack node-map payloads. Initial synthesized fire lands with null (no file loaded yet), which IsHdrGamma classifies as SDR — no spurious transition.
            h.ObserveProperty("video-params/gamma", MpvFormat.String);
            // Observe the actual decoder mpv selected, not the requested one. Fires on FileLoaded (mpv resolves hwdec after probing the file) and again if negotiation falls back mid-playback. The synthesized initial event lands with "no" or empty before any file loads.
            h.ObserveProperty("hwdec-current", MpvFormat.String);
            // track-list/count fires for any track add/remove (audio, video, sub). One observation drives the re-walk for all three kinds — ReloadTracks below classifies entries by type in a single pass.
            h.ObserveProperty("track-list/count", MpvFormat.Int64);
            // current-tracks/{video,audio,sub}/id fires whenever the active track of that kind changes — including from a *-add command selecting the new track, an explicit vid/aid/sid write, or the file-load auto-selection. Property-unavailable (no track of that kind active) lands as null AsInt64.
            h.ObserveProperty("current-tracks/video/id", MpvFormat.Int64);
            h.ObserveProperty("current-tracks/audio/id", MpvFormat.Int64);
            h.ObserveProperty("current-tracks/sub/id", MpvFormat.Int64);
            // chapter-list/count fires on file load and on any chapter add/remove. We re-walk the list on each fire. Observing /count rather than chapter-list itself is the same accommodation as track-list — MpvClient.ReadPropertyValue can't unpack NodeArray. The known edge case (a same-count list replacement, e.g. mpv-script chapter-add immediately followed by chapter-remove) is accepted: chapters change far less than tracks, and any file load changes the count anyway.
            h.ObserveProperty("chapter-list/count", MpvFormat.Int64);
            // Display dimensions, fired together by mpv after a file's video stream is decoded enough to know its display aspect (sample aspect ratio applied). Either property changing recomputes VideoAspect; the synthesized initial fires land null and clear it. mpv reports them in pixel-correct units regardless of decoder, so VideoAspect stays correct for anamorphic sources.
            h.ObserveProperty("dwidth", MpvFormat.Int64);
            h.ObserveProperty("dheight", MpvFormat.Int64);
            // container-fps fires after the source's video stream is decoded enough to know its frame rate. Initial synthesized fire pre-load lands null and clears VideoFps.
            h.ObserveProperty("container-fps", MpvFormat.Double);
            // estimated-vf-fps is mpv's rolling-average measurement from decoded frames. Updates a few times per second once decoding is underway; null pre-load and on audio-only files. Drives the FPS trust monitor's divergence check; surfaced via EstimatedVfFps for the diagnostic overlay.
            h.ObserveProperty("estimated-vf-fps", MpvFormat.Double);
            // mpv's media-title falls back to the filename when no metadata title tag is present; the synthesized initial fire lands null pre-LoadFile.
            h.ObserveProperty("media-title", MpvFormat.String);
        });
    }

    public void LoadFile(string path, bool startPaused)
    {
        if (path == null)
        {
            throw new ArgumentNullException(nameof(path));
        }
        // Preemptive SDR reset: if the previous file was HDR, snap the surface and mpv targeting back to sRGB before the new file's params land. The observer upgrades to HDR again if the new file is PQ/HLG. Without this, an HDR→SDR playlist swap would leave the subsurface PQ-tagged while mpv decodes SDR content, producing the exact overdrive we're avoiding.
        UpdateSourceHdr(null);
        // Preemptive chapter clear: drop any chapters from the previous file before the new file's count event lands, so a chapter-less follow-up can't render with stale markers in the gap between LoadFile and the first chapter-list/count fire.
        UpdateChapters(Array.Empty<MediaChapter>());
        // Preemptive eof-reached clear: same race rationale as the chapter clear. The previous file's eof-reached=true could otherwise survive past LoadFile and trip the playlist auto-advance handler's rising-edge gate against the just-loaded file.
        UpdateIsEofReached(false);
        // Drop any cached dwidth/dheight from the previous file so VideoAspect goes null until the new file's properties land. Otherwise PiP geometry would briefly use the previous source's aspect for the period between LoadFile and mpv's first dwidth/dheight observation on the new file.
        lastDwidth = null;
        lastDheight = null;
        VideoAspect = null;
        // Same race rationale: the coordinator's StepFrame uses Primary.VideoFps to compute the absolute-seconds delta, and a stale value from the previous file would mis-step Secondary.
        VideoFps = null;
        EstimatedVfFps = null;
        // Same race rationale as the dwidth/dheight clear: stop the previous file's title from surviving past a load while mpv works out the new file's media-title.
        MediaTitle = null;
        // Reset the trust monitor with declared=0 so any in-flight Untrusted state from the previous file does not survive into this one. The container-fps observation will re-init with the real declared rate (and pick the well-known-CFR tolerance) once it lands. Specifically covers two scenarios that the per-property-change path doesn't: (a) audio-only files after a VFR file — container-fps never lands so OnFileLoaded(realFps,…) is never called, and without this preempt the monitor would stay Untrusted for the audio file's lifetime (cosmetic, since ApplyVrrPolicy bails on null sourceFps anyway, but the diagnostic overlay would lie); (b) two consecutive video files where the per-property-change container-fps observer happens to fire before a stale tail event has been drained.
        fpsTrustMonitor.OnFileLoaded(0, nowSecondsProvider());
        EmitTrustStateIfChanged();
        // Reset wasSeeking so a LoadFile that arrives while the previous file was seeking doesn't leave us with a stale true→false edge waiting to fire spuriously against the new file.
        wasSeeking = false;
        // Clear the runtime fps filter preemptively so the previous file's multiplier doesn't briefly apply to the new file's first decoded frames before VideoContext.ApplyVrrPolicy re-decides.
        ClearFrameMultiplier();
        dispatcher.Post(h =>
        {
            // Bump the per-file epoch BEFORE issuing loadfile so any log-message events that fire from this point forward are stamped with the new epoch. Tail messages from the previous file that mpv emitted before this point have already been observed by the dispatcher worker (FIFO between events and posted commands) and were stamped with the old epoch — so when both land on main, mainLogEpoch can tell them apart and clear the transcript at the boundary. Also post a clear-marker to main so we still flip the transcript even if the new file produces no log messages of its own (cached hwdec decision, audio-only file, etc.).
            dispatcherLogEpoch++;
            var epochSnapshot = dispatcherLogEpoch;
            postToMainThread(() => AdvanceLogEpoch(epochSnapshot));
            h.Command("loadfile", path);
            // Set pause AFTER loadfile so mpv applies it to the file being loaded, not the (about-to-be-released) previous one. startPaused=false (the common case: open new file from picker / playlist row / auto-advance) issues pause=no so a previously-paused state from the old file doesn't carry over; startPaused=true is the restoration opt-in so saved playlists load paused at the resume position.
            h.SetProperty("pause", startPaused ? "yes" : "no");
        });
    }

    // Main-thread epoch sync. Called both from LoadFile's posted marker (so a load that produces no log messages still clears the previous file's transcript) and implicitly from OnMpvLogMessage when an epoch-bumped message lands. Idempotent on equal epochs; clears the transcript on advance.
    private void AdvanceLogEpoch(int epoch)
    {
        if (epoch <= mainLogEpoch)
        {
            return;
        }
        mainLogEpoch = epoch;
        hwdecTranscript.Clear();
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

    // Label our runtime fps entry so we can identify and remove it later without disturbing any other vf authors. Using `vf add @label:filter` + `vf remove @label` is more robust than `vf set` / `vf clr`: (a) it doesn't clobber other entries (future filter UI, user mpv.conf, orientation-from-EXIF rotate), and (b) `vf clr` was empirically failing to remove the previously-set entry across LoadFile boundaries — exact mechanism unclear, but the labelled form sidesteps it by referring to our entry by name.
    private const string FrameMultiplierVfLabel = "vompl-fps";
    // Tracks whether we currently have a labelled entry in mpv's chain. Read+written on the main thread only (Set/Clear are called from there). Used to skip a redundant `vf remove` when no entry exists, which mpv would otherwise log as an error. The actual mpv chain state may briefly diverge from this flag in flight (a posted Set hasn't been processed yet) but every Set/Clear posts a self-consistent sequence so the chain converges to match the flag's value.
    private bool frameMultiplierApplied;

    // Apply (or replace) our labelled fps entry so mpv emits frames at outputFps. The dispatcher post is a remove-then-add pair when a previous entry exists; the remove is wrapped in try/catch because mpv returns a (non-fatal) error when the named filter isn't present. Format the rate with three decimals so 47.952 (NTSC ×2) round-trips cleanly through mpv's expression parser.
    public void SetFrameMultiplier(double outputFps)
    {
        if (!double.IsFinite(outputFps) || outputFps <= 0)
        {
            return;
        }
        // Tell the trust monitor: estimated-vf-fps should converge to outputFps now, not the declared source rate. Without this update, applying our own ×N filter would be detected as sustained divergence (mpv reads N×source post-filter), the monitor would flip Untrusted, and ApplyVrrPolicy would unwind the filter — a loop. Restarts warmup so the rebuilt filter chain has time to stabilize.
        fpsTrustMonitor.SetExpectedFps(outputFps, nowSecondsProvider());
        var s = outputFps.ToString("0.000", CultureInfo.InvariantCulture);
        bool wasApplied = frameMultiplierApplied;
        frameMultiplierApplied = true;
        dispatcher.Post(h =>
        {
            if (wasApplied)
            {
                TryRemoveLabelledFilter(h);
            }
            h.Command("vf", "add", "@" + FrameMultiplierVfLabel + ":fps=fps=" + s);
        });
    }

    // Drop our labelled fps entry, leaving any other vf entries (none today, but defended for future) intact.
    public void ClearFrameMultiplier()
    {
        // Filter chain reverts to passthrough; estimated-vf-fps should now converge to declared source rate. VideoFps may be null (LoadFile preempt before container-fps lands) — pass 0 in that case to keep the monitor inert until the next OnFileLoaded with a real declared rate.
        fpsTrustMonitor.SetExpectedFps(VideoFps ?? 0.0, nowSecondsProvider());
        if (!frameMultiplierApplied)
        {
            return;
        }
        frameMultiplierApplied = false;
        dispatcher.Post(h => TryRemoveLabelledFilter(h));
    }

    private static void TryRemoveLabelledFilter(MpvHandle h)
    {
        try
        {
            h.Command("vf", "remove", "@" + FrameMultiplierVfLabel);
        }
        catch (MpvException ex)
        {
            // Removing a label that isn't currently in the chain returns an error from mpv. That can happen in narrow races (the user's mpv config cleared our label out from under us, or a previous remove succeeded but our state tracking missed it). Benign — the goal state is "label not present" and that's what we have.
            Console.Error.WriteLine($"[vomplayer] vf remove @{FrameMultiplierVfLabel} returned {ex.Code}: {ex.Message}");
        }
    }

    public void TogglePause()
    {
        bool pausedNow = IsPaused;
        dispatcher.Post(h => h.SetProperty("pause", pausedNow ? "no" : "yes"));
    }

    public void SetPaused(bool paused)
    {
        dispatcher.Post(h => h.SetProperty("pause", paused ? "yes" : "no"));
    }

    public void SetVolume(double percent)
    {
        var value = percent.ToString("F2", CultureInfo.InvariantCulture);
        dispatcher.Post(h => h.SetProperty("volume", value));
    }

    // Relative volume change. Routed through mpv's `add volume <delta>` command, not a cached read-modify-write here, so rapid VolumeUp keypresses don't race against mpv's property echo: each `add` is RMW-atomic inside mpv's playback loop and accumulates at full delta. Clamping happens inside mpv against [0, volume-max] (we pin volume-max=100 in Initialize).
    public void AdjustVolume(double deltaPercent)
    {
        var delta = deltaPercent.ToString("F2", CultureInfo.InvariantCulture);
        dispatcher.Post(h => h.Command("add", "volume", delta));
    }

    public void ToggleMute()
    {
        bool mutedNow = IsMuted;
        dispatcher.Post(h => h.SetProperty("mute", mutedNow ? "no" : "yes"));
    }

    public void LoadAudio(string path)
    {
        if (path == null)
        {
            throw new ArgumentNullException(nameof(path));
        }
        // mpv's `audio-add` defaults to flag=select, mirroring sub-add's behavior — the new track activates and the usual track-list / current-tracks observations fire.
        dispatcher.Post(h => h.Command("audio-add", path));
    }

    public void LoadSubtitle(string path)
    {
        if (path == null)
        {
            throw new ArgumentNullException(nameof(path));
        }
        // mpv's `sub-add` defaults to flag=select, which auto-activates the new track and triggers the usual track-list / current-tracks/sub/id observations. No explicit SetSubtitle follow-up needed.
        dispatcher.Post(h => h.Command("sub-add", path));
    }

    public void SetVideo(int? trackId)
    {
        SetTrackProperty("vid", trackId);
    }

    public void SetAudio(int? trackId)
    {
        SetTrackProperty("aid", trackId);
    }

    public void SetSubtitle(int? trackId)
    {
        SetTrackProperty("sid", trackId);
    }

    // mpv accepts integer ids and the symbolic "no" / "auto" for the vid/aid/sid properties; we only emit the explicit forms (id or "no") so toggling tracks is deterministic with respect to the current track-list.
    private void SetTrackProperty(string property, int? trackId)
    {
        var value = trackId.HasValue ? trackId.Value.ToString(CultureInfo.InvariantCulture) : "no";
        dispatcher.Post(h => h.SetProperty(property, value));
    }

    public void Seek(double seconds)
    {
        // libmpv aborts the host process (`free(): invalid pointer`) if a `seek` command runs before any file is loaded. Gate on DurationSeconds, which is 0 pre-load and positive once mpv has probed a real file. Streams and unseekable inputs report duration=0 too, where mpv itself wouldn't honor the seek anyway.
        if (DurationSeconds <= 0)
        {
            return;
        }
        var target = seconds.ToString("F3", CultureInfo.InvariantCulture);
        // `+exact`: mpv's `seek` command defaults to `keyframes`, which lands at the nearest keyframe at-or-before the requested target (NOT at the target itself). For PiP sync mode this is fatal — Primary and Secondary have independent keyframe schedules, so commanding each to its own absolute target (Primary → primaryTarget, Secondary → primaryTarget + offset) would still land them at different keyframes and the streams would drift apart on every seek. This is the load-bearing low-level half of the absolute-pin sync in ViewModelMain.SeekTo/StepChapter: `exact` forces the precise hr-seek path so both videos land on exactly the commanded time. Cost is the demuxer rewinds to the previous keyframe and silently decodes forward to the target — adds latency, but for typical 1–10s keyframe intervals the user never notices.
        dispatcher.Post(h => h.Command("seek", target, "absolute+exact"));
    }

    // Same pre-load gate rationale as Seek (see that comment for the libmpv-abort rationale): relative seek shares the same crash hazard. Relative seeks pass the delta directly to mpv, which clamps at the file boundaries.
    public void SeekRelative(double seconds)
    {
        if (DurationSeconds <= 0)
        {
            return;
        }
        var target = seconds.ToString("F3", CultureInfo.InvariantCulture);
        // `+exact`: see the rationale on Seek above. The keyframe-snap default is even more visible on relative seeks because the user's mental model is "go back N seconds" — landing 1–10s further back than asked is a UX regression even in single-video mode.
        dispatcher.Post(h => h.Command("seek", target, "relative+exact"));
    }

    // DurationSeconds gate here is a UX no-op (do nothing when no file is loaded), not crash-defense — frame-step doesn't have Seek's pre-load abort hazard. Kept for parity with Seek so all the per-frame inputs are uniformly inert pre-load.
    public void StepFrameForward()
    {
        if (DurationSeconds <= 0)
        {
            return;
        }
        dispatcher.Post(h => h.Command("frame-step"));
    }

    public void StepFrameBack()
    {
        if (DurationSeconds <= 0)
        {
            return;
        }
        dispatcher.Post(h => h.Command("frame-back-step"));
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
        // Gate against the dispatcher's PropertyChanged still being queued in the GLib idle pump when Dispose runs — ReloadTracks/ReloadChapters would otherwise hit dispatcher.Post on the disposed dispatcher and throw ObjectDisposedException on shutdown.
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }
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
                bool nowSeeking = change.Value.AsFlag ?? false;
                bool wasSeekingPrev = wasSeeking;
                wasSeeking = nowSeeking;
                IsSeeking = nowSeeking;
                if (wasSeekingPrev && !nowSeeking)
                {
                    // Seek-end: restart the trust monitor's warmup so post-seek estimated-vf-fps spikes (mpv re-stabilizes its rolling average over the next second or two) don't accumulate as disagreement.
                    fpsTrustMonitor.OnSeekEnded(nowSecondsProvider());
                    // No state transition possible from OnSeekEnded — sticky-Untrusted stays sticky, accumulator just resets — so no IsSourceFpsTrustedChanged emission needed.
                }
                break;
            case "core-idle":
                IsCoreIdle = change.Value.AsFlag ?? true;
                break;
            case "eof-reached":
                UpdateIsEofReached(change.Value.AsFlag);
                break;
            case "volume":
                UpdateVolume(change.Value.AsDouble);
                break;
            case "mute":
                UpdateMute(change.Value.AsFlag);
                break;
            case "video-params/gamma":
                UpdateSourceHdr(change.Value.AsString);
                break;
            case "hwdec-current":
                UpdateHwdecCurrent(change.Value.AsString);
                break;
            case "track-list/count":
                // OnMpvPropertyChanged runs on the main thread (the dispatcher's PropertyChanged is forwarded through postToMainThread upstream in the constructor). Re-walking the track-list requires mpv client-API calls, which are pinned to the dispatcher worker — so ReloadTracks Post()s back onto the worker for the read, then re-marshals the snapshot to the main thread for the property assignment.
                ReloadTracks();
                break;
            case "chapter-list/count":
                ReloadChapters();
                break;
            case "current-tracks/video/id":
                UpdateCurrentVideoId((int?)change.Value.AsInt64);
                break;
            case "current-tracks/audio/id":
                UpdateCurrentAudioId((int?)change.Value.AsInt64);
                break;
            case "current-tracks/sub/id":
                UpdateCurrentSubtitleId((int?)change.Value.AsInt64);
                break;
            case "dwidth":
                lastDwidth = change.Value.AsInt64;
                UpdateVideoAspect();
                break;
            case "dheight":
                lastDheight = change.Value.AsInt64;
                UpdateVideoAspect();
                break;
            case "container-fps":
                // Filter ≤ 0 to null: mpv reports 0 (or property-unavailable, which AsDouble surfaces as null) for audio-only files / pre-load / sources where the container omits a frame rate. Coordinator treats null as "fall back to fan-out" rather than dividing by zero.
                double? fps = change.Value.AsDouble;
                double? validFps = fps.HasValue && fps.Value > 0 ? fps : null;
                bool fpsValueChanged = !Nullable.Equals(VideoFps, validFps);
                VideoFps = validFps;
                if (fpsValueChanged && validFps.HasValue)
                {
                    // Re-init the trust monitor with the freshly-known declared rate. The LoadFile preemptive reset set declared=0; this is where we set the real tolerance band and warmup origin. Any prior-file Untrusted state gets reset to Trusted here, which is correct — a new file deserves fresh evaluation.
                    fpsTrustMonitor.OnFileLoaded(validFps.Value, nowSecondsProvider());
                    EmitTrustStateIfChanged();
                }
                break;
            case "estimated-vf-fps":
                double? est = change.Value.AsDouble;
                EstimatedVfFps = est.HasValue && est.Value > 0 ? est : null;
                if (EstimatedVfFps.HasValue)
                {
                    fpsTrustMonitor.OnEstimatedFps(EstimatedVfFps.Value, nowSecondsProvider());
                    EmitTrustStateIfChanged();
                }
                break;
            case "media-title":
                UpdateMediaTitle(change.Value.AsString);
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

    // Posts a track-list re-walk onto the dispatcher worker (the only thread allowed to touch mpv client-API methods), then hands the snapshot back to the main thread for the per-kind property assignments so PropertyChanged subscribers stay on the UI thread. We re-walk the whole list rather than diff-ing because mpv's per-track ids can be reordered after a *-remove, and the list is small (typically <10 tracks per kind).
    private void ReloadTracks()
    {
        dispatcher.Post(h =>
        {
            var snapshot = ReadTracksFromMpv(h);
            postToMainThread(() =>
            {
                UpdateVideoTracks(snapshot.Video);
                UpdateAudioTracks(snapshot.Audio);
                UpdateSubtitleTracks(snapshot.Subtitle);
                // Fire after the three updates land so consumers that need "all three settled" (the per-directory preferences applier) only do work once per re-walk. Per-kind PropertyChanged would over-fire (one per kind that changed) AND under-fire (the dedup gate suppresses empty→empty, so a kind with no tracks emits nothing).
                TracksReloaded?.Invoke();
            });
        });
    }

    // Per-kind buckets for a single track-list walk. Keeping them as one struct lets ReloadTracks avoid three separate dispatcher Posts (and their associated mid-walk races).
    private readonly record struct TrackSnapshot(IReadOnlyList<MediaTrack> Video, IReadOnlyList<MediaTrack> Audio, IReadOnlyList<MediaTrack> Subtitle);

    // Walks mpv's track-list via the per-index scalar accessors (track-list/N/...) so we don't need NodeArray support in MpvClient.ReadPropertyValue. count read as a string and parsed because the scalar reads return strings anyway — keeps a single code path. One walk classifies entries into three lists by their `type` field.
    //
    // Known non-atomicity: each property read sees mpv's live state, not a snapshot, so a track add/remove that races us mid-walk can produce a single MediaTrack with mismatched fields (id from one slot, title from another that just shifted in). Self-healing — mpv fires another track-list/count change for the mutation, which re-walks. Acceptable for KISS until it actually surfaces; the fix would be NodeArray support to read track-list as a single snapshot.
    private static TrackSnapshot ReadTracksFromMpv(MpvHandle h)
    {
        var empty = Array.Empty<MediaTrack>();
        var countStr = h.GetPropertyString("track-list/count");
        if (countStr == null)
        {
            // Pre-initialize / shutdown race; treat as empty without logging — common on the synthesized initial fire.
            return new TrackSnapshot(empty, empty, empty);
        }
        if (!int.TryParse(countStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
        {
            // mpv documents track-list/count as int — a non-parseable value here means mpv broke its contract or returned an unexpected form. Log loudly per CLAUDE.md (silent error handling banned) and treat as empty.
            Console.Error.WriteLine($"[vomplayer] tracks: unparseable track-list/count='{countStr}'");
            return new TrackSnapshot(empty, empty, empty);
        }
        if (count <= 0)
        {
            return new TrackSnapshot(empty, empty, empty);
        }
        List<MediaTrack>? video = null;
        List<MediaTrack>? audio = null;
        List<MediaTrack>? subtitle = null;
        for (int i = 0; i < count; i++)
        {
            var type = h.GetPropertyString($"track-list/{i}/type");
            List<MediaTrack> bucket;
            if (type == "video")
            {
                bucket = video ??= new List<MediaTrack>();
            }
            else if (type == "audio")
            {
                bucket = audio ??= new List<MediaTrack>();
            }
            else if (type == "sub")
            {
                bucket = subtitle ??= new List<MediaTrack>();
            }
            else
            {
                continue;
            }
            var idStr = h.GetPropertyString($"track-list/{i}/id");
            if (!int.TryParse(idStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            {
                // Every mpv track has an id; missing or unparseable on a typed entry means we hit the mid-walk race or mpv broke its contract. Log and skip rather than silently drop.
                Console.Error.WriteLine($"[vomplayer] tracks: unparseable track-list/{i}/id='{idStr}' (type={type}), skipping");
                continue;
            }
            var title = h.GetPropertyString($"track-list/{i}/title");
            var lang = h.GetPropertyString($"track-list/{i}/lang");
            var external = h.GetPropertyFlag($"track-list/{i}/external") == true;
            // External-filename is the full path mpv loaded the sidecar from. We strip to the basename so the per-directory matcher can compare across siblings ("commentary.ac3" in /a vs /b is "the same kind of file"); the directory part will diverge by definition for any cross-file lookup.
            var externalFilename = external ? Path.GetFileName(h.GetPropertyString($"track-list/{i}/external-filename")) : null;
            bucket.Add(new MediaTrack(id, NullIfEmpty(title), NullIfEmpty(lang), external, NullIfEmpty(externalFilename)));
        }
        return new TrackSnapshot(
            (IReadOnlyList<MediaTrack>?)video ?? empty,
            (IReadOnlyList<MediaTrack>?)audio ?? empty,
            (IReadOnlyList<MediaTrack>?)subtitle ?? empty);
    }

    private static string? NullIfEmpty(string? s)
    {
        return string.IsNullOrEmpty(s) ? null : s;
    }

    // Internal so PlaybackTests can drive snapshot replacement without spinning up mpv's event pump. Sequence-equality dedup keeps PropertyChanged from firing for spurious track-list/count notifications that don't actually change the per-kind subset (e.g., a sub track add/remove also fires count, which would otherwise re-notify Video and Audio consumers for nothing).
    internal void UpdateVideoTracks(IReadOnlyList<MediaTrack> snapshot)
    {
        if (TracksEqual(VideoTracks, snapshot))
        {
            return;
        }
        VideoTracks = snapshot;
    }

    internal void UpdateAudioTracks(IReadOnlyList<MediaTrack> snapshot)
    {
        if (TracksEqual(AudioTracks, snapshot))
        {
            return;
        }
        AudioTracks = snapshot;
    }

    internal void UpdateSubtitleTracks(IReadOnlyList<MediaTrack> snapshot)
    {
        if (TracksEqual(SubtitleTracks, snapshot))
        {
            return;
        }
        SubtitleTracks = snapshot;
    }

    private static bool TracksEqual(IReadOnlyList<MediaTrack> a, IReadOnlyList<MediaTrack> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }
        return true;
    }

    // Internal test seam for verifying TracksReloaded subscription wiring without spinning up mpv. Production fires TracksReloaded only from inside ReloadTracks's main-thread callback, which can't run in tests; tests drive Update*Tracks directly and then ping the event through here.
    internal void RaiseTracksReloadedForTest()
    {
        TracksReloaded?.Invoke();
    }

    // Same shape as ReloadTracks but for chapter-list. Walked per-index because MpvClient.ReadPropertyValue doesn't unpack NodeArray. Title via GetPropertyString (nullable — some containers carry only timestamps); time via GetPropertyDouble (mpv reports it as a double directly).
    private void ReloadChapters()
    {
        dispatcher.Post(h =>
        {
            var snapshot = ReadChaptersFromMpv(h);
            postToMainThread(() => UpdateChapters(snapshot));
        });
    }

    private static IReadOnlyList<MediaChapter> ReadChaptersFromMpv(MpvHandle h)
    {
        var empty = Array.Empty<MediaChapter>();
        var countStr = h.GetPropertyString("chapter-list/count");
        if (countStr == null)
        {
            return empty;
        }
        if (!int.TryParse(countStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
        {
            Console.Error.WriteLine($"[vomplayer] chapters: unparseable chapter-list/count='{countStr}'");
            return empty;
        }
        if (count <= 0)
        {
            return empty;
        }
        var list = new List<MediaChapter>(count);
        for (int i = 0; i < count; i++)
        {
            var time = h.GetPropertyDouble($"chapter-list/{i}/time");
            if (!time.HasValue)
            {
                // mpv contracts every chapter to have a time; missing one is mid-walk race or a contract break. Log and skip.
                Console.Error.WriteLine($"[vomplayer] chapters: missing time for chapter-list/{i}, skipping");
                continue;
            }
            var title = h.GetPropertyString($"chapter-list/{i}/title");
            list.Add(new MediaChapter(i, NullIfEmpty(title), time.Value));
        }
        return list;
    }

    // Internal so PlaybackTests can drive snapshot replacement without spinning up mpv. Same sequence-equality dedup pattern as UpdateVideoTracks et al — empty→empty and identical-list re-fires are suppressed so PropertyChanged consumers don't redraw for nothing.
    internal void UpdateChapters(IReadOnlyList<MediaChapter> snapshot)
    {
        if (ChaptersEqual(Chapters, snapshot))
        {
            return;
        }
        Chapters = snapshot;
    }

    private static bool ChaptersEqual(IReadOnlyList<MediaChapter> a, IReadOnlyList<MediaChapter> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }
        return true;
    }

    // Internal so PlaybackTests can drive the property without spinning up mpv. The ObservableProperty setter already dedups same-value writes, so the gate here is purely for documentation symmetry with the Update*Tracks methods / UpdateHwdecCurrent.
    internal void UpdateCurrentVideoId(int? value)
    {
        CurrentVideoId = value;
    }

    internal void UpdateCurrentAudioId(int? value)
    {
        CurrentAudioId = value;
    }

    internal void UpdateCurrentSubtitleId(int? value)
    {
        CurrentSubtitleId = value;
    }

    // Runs on the main thread (OnDispatcherLogMessage forwards via postToMainThread). Pre-filtered upstream — every message landing here passed IsHwdecLogPrefix. Carries the dispatcher-side epoch so the LoadFile boundary clear lands at the right point in the sequence relative to in-flight events. Verbatim formatting matches mpv's terminal output style ("[prefix/level] text") so the dump is recognizable to anyone who's debugged mpv at the command line.
    private void OnMpvLogMessage(LogMessage message, int epoch)
    {
        AdvanceLogEpoch(epoch);
        if (hwdecTranscript.Count >= TranscriptMaxLines)
        {
            hwdecTranscript.Dequeue();
        }
        hwdecTranscript.Enqueue($"[{message.Prefix}/{message.Level}] {message.Text}");
    }

    // Internal test seam — drives the full main-thread path (epoch advance + filter + ring append) without an mpv pump. Tests pass the message and the epoch they want stamped; production passes the epoch captured on the dispatcher worker. The pre-dispatcher prefix filter is applied here too so tests exercise the same accept/reject decision.
    internal void IngestLogMessageForTest(LogMessage message, int epoch)
    {
        if (!IsHwdecLogPrefix(message.Prefix))
        {
            return;
        }
        OnMpvLogMessage(message, epoch);
    }

    // Internal seam to drive the LoadFile epoch advance directly without a real loadfile command. Mirrors what AdvanceLogEpoch does on the actual production path.
    internal void AdvanceLogEpochForTest(int epoch)
    {
        AdvanceLogEpoch(epoch);
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

    // Internal test seam mirroring UpdateHwdecCurrent etc. — null fallback to mpv's default (100) matches the constant-fallback pattern used throughout OnMpvPropertyChanged. mpv shouldn't ever fire null for `volume` (it's a process-level property, not file-bound), but if it does we land at the documented default rather than carrying a stale value forward.
    internal void UpdateVolume(double? value)
    {
        Volume = value ?? 100;
    }

    internal void UpdateMute(bool? value)
    {
        IsMuted = value ?? false;
    }

    // Internal test seam mirroring the other Update* methods. Empty strings normalize to null so consumers don't have to special-case "" — mpv occasionally fires empty for media-title on the synthesized initial observe before LoadFile, and again briefly during a load on some containers.
    internal void UpdateMediaTitle(string? value)
    {
        MediaTitle = string.IsNullOrEmpty(value) ? null : value;
    }

    // Internal test seam mirroring UpdateMute. Null falls back to false (mpv shouldn't report null for eof-reached on a loaded file, but the synthesized initial-fire is null pre-Initialize / on shutdown). The ObservableProperty setter dedups same-value writes already; the explicit method just normalizes null.
    internal void UpdateIsEofReached(bool? value)
    {
        IsEofReached = value ?? false;
    }

    // Recompute VideoAspect from the latest dwidth/dheight pair. Goes null when either dimension is missing or non-positive (no file loaded, no video stream, or pre-decode); the ObservableProperty setter dedups same-value writes so cycling between a known aspect and the same aspect after a Property re-fire doesn't emit a spurious change.
    private void UpdateVideoAspect()
    {
        if (lastDwidth.HasValue && lastDheight.HasValue && lastDwidth.Value > 0 && lastDheight.Value > 0)
        {
            VideoAspect = (double)lastDwidth.Value / (double)lastDheight.Value;
        }
        else
        {
            VideoAspect = null;
        }
    }

    // Resyncs the cached IsSourceFpsTrusted bool against the monitor's State and fires IsSourceFpsTrustedChanged on transitions. Same postToMainThread defer as SourceHdrChanged: VideoContext.ApplyVrrPolicy fires SetFrameMultiplier/ClearFrameMultiplier through the dispatcher, which can synchronously block the main thread; punting the event delivery breaks any same-tick-deadlock cycle. In tests postToMainThread is a => a() so the fire stays synchronous.
    private void EmitTrustStateIfChanged()
    {
        bool now = fpsTrustMonitor.State == FpsTrust.Trusted;
        if (now == isSourceFpsTrusted)
        {
            return;
        }
        isSourceFpsTrusted = now;
        postToMainThread(() => IsSourceFpsTrustedChanged?.Invoke(now));
    }

    // Test seam: drive a property-change through the SAME path as production's mpv observer (the private `OnMpvPropertyChanged` switch). Tests use this to lock in the wiring between observed properties and downstream state — the trust monitor, the VRR-policy inputs, the HDR observer, etc — so a refactor of the switch surfaces in tests rather than running silently. Note the deliberate symmetry with how `OnMpvPropertyChanged` is fed in production (via `dispatcher.PropertyChanged` → `postToMainThread`); tests use a synchronous postToMainThread so the call lands inline.
    internal void IngestPropertyChangeForTest(Mpv.PropertyChange change)
    {
        OnMpvPropertyChanged(change);
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
        // Drop chapters so the controls stop drawing markers as soon as playback ends. Mirrors the SDR reset above.
        UpdateChapters(Array.Empty<MediaChapter>());
        // Symmetry with the LoadFile reset — see comment there. Stale eof-reached carry-over could otherwise survive past a file-end into whatever loads next.
        UpdateIsEofReached(false);
        FileEnded?.Invoke(reason);
    }

    private void OnMpvShutdown()
    {
        Console.Error.WriteLine("[vomplayer] mpv signalled shutdown");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        // The `disposed` flag above gates OnMpvPropertyChanged, the one main-thread handler that itself calls dispatcher.Post (via ReloadTracks/ReloadChapters). Every OTHER main-thread handler the dispatcher feeds (FileLoaded, FileEnded, Shutdown, LogMessageReceived) only reaches dispatcher.Post indirectly via downstream subscribers — VideoContext.OnPlaybackFileLoaded → SetVideo → dispatcher.Post, etc. Nulling our event fields here BEFORE dispatcher.Dispose is what makes those paths safe: a forwarded handler that lands on the main thread post-Dispose finds the event field null, the invoke is a no-op, and the subscriber that would have called dispatcher.Post never runs. If a future event field gets added without being nulled here, the race resurfaces in that new path.
        FileLoaded = null;
        FileEnded = null;
        TracksReloaded = null;
        SourceHdrChanged = null;
        IsSourceFpsTrustedChanged = null;
        dispatcher.Dispose();
    }
}
