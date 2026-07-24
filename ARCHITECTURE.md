# Architecture

Living notes on the structure of the codebase. Keep this short and current; code-level "why" comments belong next to the code, not here.

## What it is

Cross-platform video player, C# + GTK4 (via GirCore) + libmpv (P/Invoke). Target: smplayer-parity feature set with a permissive-license front-end. Primary dev platform is Linux/Wayland/KWin; X11, Windows, macOS are fallback paths that degrade the HDR story but keep playback working. Windows and macOS haven't been exercised ever — GTK4 is cross-platform and the HDR path is gated on `OperatingSystem.IsLinux()`, so they *should* work, but no active QA.

## Licensing

App code is MIT. libmpv is LGPLv2.1+ (dynamic linking keeps us permissive). This is a hard constraint:
- No GPL / LGPL-static / LGPL-adjacent runtime dependencies in the app tree. LibVLCSharp was rejected for this reason.
- Feature work toward smplayer parity (playlists, subtitles, filters, shaders, stream selection) goes through libmpv properties/commands via the P/Invoke surface — we do not shell out to an `mpv` binary.

## Module layout

```
src/
  Program.cs             # entry, arg parsing, Gtk.Application wiring, primary Playback construction, StateDatabase open
  StartupHelpers.cs      # pure: GApplication flag computation + command-line path/URI resolution
  GLibLogDiag.cs         # g_log_set_writer_func — adds C# stack traces to GLib ERROR/CRITICAL
  Epoxy.cs               # eglGetProcAddress + glGetIntegerv for FBO binding
  LibC.cs                # setlocale(LC_NUMERIC,"C") — mpv refuses non-C LC_NUMERIC
  MainWindow.cs          # code-only GTK4 window: widgets, input controllers, fullscreen / autohide / screensaver, VM <-> view glue
  MainWindow.Menu.cs     # menubar + Gio.SimpleAction registration + per-kind track menus + accel refresh + File→Recent
  MainWindow.PlaylistIo.cs # File → Open/Save Playlist handlers (file dialogs + clipboard, PlaylistFile format glue)
  PipController.cs       # picture-in-picture controller: secondary widget set, drag/resize, layout, drift-controller tick timer, stream-selector toolbar. MainWindow implements IPipHost to expose the GTK widget tree the controller needs.
  PreferencesDialog.cs   # preferences UI: HotkeyAction → Trigger bindings plus application settings (theme, open-in-new-window, chapter preroll)

  Controls/
    IVideoHost.cs          # seam over the two render paths; consumers hold one IVideoHost instead of per-path fields
    VideoHostWayland.cs    # IVideoHost impl: VideoArea placeholder + VideoSurface subsurface
    VideoHostGlArea.cs     # IVideoHost impl: VideoView (GLArea)
    VideoView.cs           # Gtk.GLArea path: GLArea-owned FBO, mpv renders into it
    VideoArea.cs           # Wayland path: DrawingArea that paints nothing, emits GeometryChanged
    ChapterScrubber.cs     # seek-scale + chapter-mark overlay with click-to-seek
    SeekScaleController.cs # the seek Gtk.Scale + its press/settle state machine (see "MVVM" below)
    DownloadStatusOverlay.cs # top-left URL-download progress card, generation-token guarded
    PlaylistPanel.cs       # rebindable list view bound to a VideoContext's Playlist
    DiagnosticOverlay.cs   # 1 Hz read-out of HDR/VRR/hwdec/sync state, gated by Help → Diagnostic Overlay
    DiagnosticFormatter.cs # pure: readout fields → string lines (testable)

  Mpv/
    LibMpv.cs            # P/Invoke surface for libmpv (client + render_context)
    MpvClient.cs         # thin C# wrapper: events, observed properties, commands
    MpvDispatcher.cs     # worker thread that serializes client-API calls; hands out ref-struct MpvHandle inside Post callbacks
    MpvRenderContext.cs  # mpv_render_context_* wrapper, ADVANCED_CONTROL contract

  Playback/
    IPlayback.cs         # interface VideoContext depends on
    Playback.cs          # owns MpvDispatcher; maps mpv events → ObservableObject properties; hwdec transcript ring; HDR transition tracking; frame-multiplier filter; FpsTrustMonitor wiring
    FpsTrustMonitor.cs   # pure: expected-vs-estimated divergence accumulator with sticky-untrusted state (expected = declared × any active fps-filter multiplier, so our own VRR filter never reads as divergence)

  Services/
    IFilePicker.cs / FilePickerGtk.cs   # Gtk.FileDialog
    IUrlPrompt.cs / UrlPromptGtk.cs     # Gtk dialog for URL entry + download progress
    IUrlDownloader.cs                   # interface: availability probe + playlist probe (--flat-playlist) + classify (extractor vs direct) + download (with progress + cancel)
    YtDlpDownloader.cs                  # IUrlDownloader spawning yt-dlp
    UrlLoadCoordinator.cs               # per-VideoContext yt-dlp lifecycle: prompt/probe flow + race-guarded probe-then-route loads
    UrlDownloadCache.cs                 # XDG-cache-dir-rooted, mtime-based stale sweep
    FlatpakDetect.cs                    # sandbox sniff (drives the flatpak-spawn yt-dlp invocation)

  UserData/
    UserDataPaths.cs       # XDG-aware paths; VOMPL_CONFIG_DIR / VOMPL_STATE_DIR overrides for tests / portable installs
    UserConfig.cs          # TOML, Tomlyn-backed; load-or-defaults; today only [hotkeys]
    Hotkeys.cs             # HotkeyAction enum, Trigger discriminated record (Key | MouseClick), HotkeyMap, defaults
    StateDatabase.cs       # owns the SQLite connection + append-only Migrations[] registry walked vs PRAGMA user_version
    IRecentFiles.cs / RecentFiles.cs              # SQLite-backed; recents + per-file resume position
    ThemeMode.cs           # theme preference enum + parser (auto/light/dark)
    ISavedPlaylists.cs / SavedPlaylists.cs        # SQLite-backed; autosaved playlist history (single + multi-stream PiP)
    ITrackPreferences.cs / TrackPreferences.cs    # SQLite-backed; per-directory remembered video/audio/subtitle choice
    TrackPreference.cs / TrackMatcher.cs / MediaKind.cs   # pure record + matcher used by both save and apply paths

  Util/
    TimeFormatter.cs        # MM:SS / H:MM:SS
    IdleSafe.cs             # GLib.Functions.IdleAdd wrapper that keeps the delegate rooted across the queued tick
    UriListDropTarget.cs    # async text/uri-list drop target; bypasses GTK's FileList portal-mediation (fails in Flatpak sandbox)
    PipLayoutCalc.cs        # pure: PiP region rect + clamp + aspect-locked resize projection
    ChapterHitTest.cs       # pure: scrubber pointer-x → chapter index
    ChapterStep.cs          # pure: next/prev chapter cue resolution incl. preroll
    ChapterLabel.cs         # pure: chapter tooltip/label formatting
    CursorRevealPolicy.cs   # pure: fullscreen cursor-reveal displacement rule
    ScaleHelpers.cs         # Gtk.Scale gesture tweaks (long-press removal)
    PlaylistFile.cs         # pure: newline-delimited playlist parse/serialize
    UriShape.cs             # pure: the one URI-vs-local-path sniffer
    GtkDialogError.cs       # GException → "user dismissed the dialog?" via GError domain/code
    PlaylistMenuSelector.cs # pure: Recent menu's "10 entries with directory coverage" selection rule
    MediaExtensions.cs      # known video extension set (folder-drop expansion)

  ViewModels/
    ViewModelMain.cs       # coordinator: owns Primary VideoContext (always), optional Secondary (PiP); routes transport (selected ⇒ isolated; null ⇒ broadcast/sync); sync-mode stored targetOffset (absolute-pin seeks) + continuous drift controller (speed-nudge + hard-resync); lockstep advance gate
    VideoContext.cs        # one per video stream: per-instance HDR/VRR policy, track-preference apply, resume-position save/apply, URL/yt-dlp routing, auto-advance EOF state machine, owned Playlist
    Playlist.cs            # plain in-memory list + currentIndex + Changed event (no GTK dep — fully testable)
    PlaylistAutosave.cs    # subscribes to bound contexts' Playlist.Changed; upserts the current GUID's row in SavedPlaylists; mints a new GUID on a Primary-Replace when the playlist becomes single-stream

  Wayland/
    WaylandDetect.cs                # backend sniff via gdk_wayland_display_get_wl_display
    VomplWayland.cs                 # P/Invoke to libhdr_helper.so (subsurface + output callbacks)
    VideoSurface.cs                 # Wayland-path orchestrator: window lifecycle, render loop, IHdrSink + IVrrSink to the per-context policy
    IHdrSink.cs / IVrrSink.cs       # abstractions VideoContext consumes; null on the GLArea fallback path
    FrameTimingBridge.cs            # per-surface presentation-feedback accumulator, VRR ring, stats log
    VrrClassifier.cs                # pure: residual-against-T-grid classifier
    HdrClassifier.cs                # pure: preferred image description (tf_named/luminances) → HDR y/n + justification
    VrrPolicy.cs                    # pure: (sourceFps, vrrRange, currentRefreshHz, fpsTrusted) → frame-multiplier decision
    WaylandOutputRegistry.cs        # process-global wl_output mode/HDR/VRR table (keyed by registry name)
    EdidParser.cs / EdidLookup.cs   # pure: parse Range Limits descriptor for VRR window from the sysfs EDID blob

  Native/
    hdr_helper.c                                # Wayland ABI shim, see "ABI boundary" below
    color-management-v1-*                       # wayland-scanner output
    presentation-time-*                         # wayland-scanner output

test/                    # NUnit; references main project via InternalsVisibleTo
```

The native shim is compiled by an MSBuild `BuildHdrHelper` target in `Vomplayer.csproj` (Linux only) and copied to the output directory.

## Rendering paths

Two runtime-selected paths, chosen once by `WaylandDetect.IsWaylandBackend` in `MainWindow.CreateVideoHost` (PiP secondaries mint through the same factory via `IPipHost`). Consumers hold the `IVideoHost` seam — widget, playback attach, render events, geometry signal, teardown ordering — and reach the genuinely Wayland-only capabilities (subsurface stacking, HDR/VRR sinks) through its single nullable `WaylandSurface`.

**Wayland subsurface path (preferred on Linux/Wayland).**
`VideoArea` (paints nothing) reserves layout space. `VideoSurface` creates a `wl_subsurface` of the GTK main `wl_surface` via `libhdr_helper.so` and places it *below* the parent. GTK's main window is CSS-transparent (`.vompl-main-window { background: transparent; }`) so the subsurface shows through in the video region; opaque chrome widgets (`.vompl-chrome`) sit on top. This means:
- The subsurface is always explicitly tagged via `wp_color_management_v1`: PQ/BT.2020 when the source is HDR, GAMMA22/BT.709 when SDR. Per the protocol spec, an untagged surface is "compositor implementation defined"; we observed that on KWin with an HDR output present the resulting handling blows out gamma22-encoded SDR output catastrophically on the SDR panel, so we don't leave the surface in that state. The GTK UI surface stays sRGB and renders correctly. HDR-vs-SDR tagging is driven by the source's transfer function alone, *not* the output's HDR-capability — when an HDR source lands on an SDR output, we still tag PQ and have mpv emit pass-through PQ; KWin's libplacebo-backed compositor tone-maps PQ→SDR for the SDR scan-out. This delegates HDR→SDR conversion to libplacebo (smplayer / `vo=gpu-next` quality) instead of mpv-via-libmpv's older `gl_video` curves, which clip highlights hard at the source mastering-display peak. Signals:
  - `Playback` observes `video-params/gamma` and fires `SourceHdrChanged(bool)` per-video.
  - `VideoSurface.CurrentOutputHdrChanged` fires on output-side change — `wl_surface.enter`/`leave` mutating the active-output set, or `wp_color_management_output_v1.image_description_changed` propagating through `WaylandOutputRegistry`. Per-output HDR-capability is detected by probing the preferred image description: PQ/HLG `tf_named` (legacy compositors) OR luminance headroom, `max_lum > reference_lum` (modern KWin advertises gamma22 tf even for HDR-enabled outputs); classification lives in `HdrClassifier` on the C# side, which also reports which rule fired for the diagnostic overlay.
  - `VideoContext.ApplyHdrPolicy` combines the two signals and calls `IHdrSink.SetHdr` (stages `set_image_description` / `unset_image_description` that the next `eglSwapBuffers` flushes atomically with the first new-content buffer) and `Playback.EnableHdrOutput` / `DisableHdrOutput` for mpv's `target-*` targeting. Every VideoContext has its own policy — Primary and Secondary HDR decisions are independent.
- Controls overlaid in fullscreen (via `Gtk.Overlay` reparent) draw on top of the video with correct alpha.
- Pointer input falls through to the parent (empty input region on the subsurface) so motion-driven auto-hide works.
- `wp_presentation_feedback` per swap drives the `FrameTimingBridge` ring; VRR/fixed is classified against `wl_output.mode` refresh.

**Gtk.GLArea fallback path (X11 / other backends).**
`VideoView` is a `Gtk.GLArea`; mpv renders into GTK's owned FBO. HDR is not supported on this path — mpv tonemaps HDR source content down to SDR via its default `auto` targeting. Main-surface PQ tagging was tried and reverted: it produced a blown-out GTK UI (widgets render sRGB values into a surface KWin interprets as PQ) and couldn't be toggled per-file without tearing down the GTK surface. `IHdrSink`/`IVrrSink` are not attached on this path; `VideoContext` no-ops its policy methods when the sinks are null.

## Playback data flow and threading

Three distinct threads touch mpv, each with its own role:
- **mpv's event thread** (libmpv-internal) — fires the wakeup callback.
- **mpv-dispatcher worker** (`MpvDispatcher`, owned by `Playback`) — the ONLY thread that calls the mpv *client-API* surface (SetProperty, Command, ObserveProperty, DrainEvents, Initialize, etc). Enforced by the ref-struct `MpvHandle`: handles are handed out only inside `MpvDispatcher.Post` callbacks, and a ref struct can't be captured in a closure, stored in a field, awaited across, or otherwise leaked out of the post scope — so it's impossible to call a client-API method from the wrong thread.
- **GTK main thread** — owns GL context and window lifecycle; calls `mpv_render_context_*` (Update / Render / ReportSwap) via `MpvRenderContext`. Render-context construction also happens here (`mpv_render_context_create` reads the mpv handle but must run on the GL-owning thread); `MpvDispatcher.CreateRenderContext` is the controlled escape hatch that wraps the ctor. Render-context calls don't deadlock the way client-API calls do.

```
mpv event thread:                mpv-dispatcher worker:         GTK main thread:
  wakeup callback   --post--->     DrainEvents
                                     PropertyChanged  ---postToMainThread--->   Playback.ObservableProperties
                                     FileLoaded/etc   ---postToMainThread--->   VideoContext mirrors -> ViewModelMain proxies -> widgets
```

```
mpv render thread:               GTK main thread:
  UpdateRequested   -----IdleAdd------>   DoRender
                                           -> MakeCurrent
                                           -> mpv_render_context_update
                                           -> mpv_render_context_render
                                           -> Swap (eglSwapBuffers) / GLArea QueueRender
                                           -> mpv_render_context_report_swap
```

Why the dispatcher matters: `mpv_set_property_string` for VO properties (`target-prim`/`target-trc`/…) synchronously blocks until mpv_render_context_render acknowledges the change. That render call runs on the main thread via `IdleAdd`, so calling SetProperty from main → main blocks in SetProperty → IdleAdd can't fire → mpv core waits for render that can't happen → deadlock. Running SetProperty on the dispatcher worker breaks the cycle: main stays free to service the render IdleAdd while the worker blocks.

Cross-thread coupling per `Playback` is isolated in three places: the `Action<Action> postToMainThread` ctor seam (tests pass `a => a()`), the `MpvDispatcher` worker thread + queue, and the render-queue coalescer in `VideoSurface`/`VideoView`. PiP runs two `Playback` instances side by side (primary, plus a secondary constructed by `PipController.Enable`); each has its own copy of those three places, and the two share no state or synchronization. Cross-stream coordination lives in `ViewModelMain` and reaches mpv only via per-context `Playback` calls.

`Playback` keeps `MpvClient` behind the `MpvDispatcher` boundary — `MpvClient` is `internal` and exposed only via the ref-struct `MpvHandle` inside `Post` callbacks. `AttachRenderSurface(Action<MpvDispatcher> attach)` hands out the dispatcher for render-context construction; consumers call `CreateRenderContext` on it rather than touching an mpv handle directly.

## MVVM and the multi-video coordinator

- VM is `ViewModelMain`, a coordinator over one or two `VideoContext`s. Constructed with the primary `Playback` plus the service set (file picker, recents, track prefs, URL prompt, URL downloader). PiP adds a secondary `Playback` lazily via `PipController.Enable`.
- Each `VideoContext` (CommunityToolkit.Mvvm `ObservableObject`) wraps one `IPlayback`, mirrors its properties as observables, owns one `Playlist`, and runs that stream's HDR / VRR / track-preference / resume-position / auto-advance state machines. See the comments at the top of `VideoContext.cs` for the per-subsystem rationale; this doc deliberately doesn't restate them.
- View is `MainWindow` (with `.Menu.cs` and `.PlaylistIo.cs` partials; PiP lives in the non-partial `PipController`), code-only GTK4. It subscribes to VM `PropertyChanged` and switches on property name to push widget updates. User actions go to VM commands (`[RelayCommand]`) or imperative methods.
- Read-only proxy properties on `ViewModelMain` reflect `SingleTarget` (= SelectedContext if set, else Primary). PropertyChanged is forwarded only when the source context matches SingleTarget; SelectedSlot transitions re-fire every proxy so the view re-reads from the new target.
- Seek is the nontrivial single-video interaction: a state machine (idle / holding / settling) in `Controls/SeekScaleController.cs` around the `Gtk.Scale`. See comments there.

**PiP routing.** `ViewModelMain.SelectedSlot` governs every transport command:
- `null` ⇒ broadcast/sync. Transport (Seek*, StepFrame*, StepChapter) fans out to both contexts; PlayPause converges any drifted pair to a single target. Per-video commands (Volume, Mute, Tracks, Open*, Load*) target Primary.
- non-null ⇒ isolated. Every command goes to the selected context only.

**Sync-mode coordination.** When two contexts run and SelectedSlot is null, `ViewModelMain` defends a `Secondary.Position = Primary.Position + targetOffset` invariant where `targetOffsetSeconds` is the single source of truth. Sync-mode *absolute* seeks (`SeekTo`, `StepChapter`, `SeekToChapter`) re-pin Secondary to `primaryTarget + targetOffset`, computed directly from the scrubber/cue value rather than from Primary's live position — so repeated seeks can't accumulate drift. *Relative* moves (`SeekRelative`, frame-step) fan out unchanged and preserve the offset by construction. The governing rule: the offset is (re)captured only by actions that establish the sync relationship from the current positions — `EnablePip` → 0, `FileLoaded` (either slot) → pending/null, the selected→sync transition → captured divergence, `EnsureTargetOffset`'s baseline-on-first-use of a pending value, and a sync-mode `PlayPause` that changes only one stream's play state (converging a differed pair "joins" one stream to the other, a fresh sync point). Actions that move both streams together (sync Seek/StepChapter/StepFrame, a same-state play-both) only read and defend it, so ordinary seeking/playback can't shift the user's intended sync. A continuous drift controller (`ApplyDriftCorrection`, driven by a repeating GLib timer in `PipController`) defends the offset while both streams play: a three-tier control law — hard re-seek beyond 1 s, a latched ±5% speed catch-up beyond 50 ms (held until it overshoots), and a deadband + proportional speed settle below that. Per-context auto-advance is suppressed; `CheckLockstepAdvance` advances both together. The rationale lives in code comments on `ViewModelMain.SeekTo` / `EnsureTargetOffset` / `ApplyDriftCorrection`, and `CheckLockstepAdvance`.

**PiP lifecycle.** `PipController.Enable` builds the secondary `Playback`, `VideoSurface`/`VideoView`, and `VideoContext`, then hands the context to `ViewModelMain.EnablePip`. Disposal is split: PipController owns the secondary `Playback`'s teardown; `VideoContext.Dispose` only unsubscribes. Teardown ordering is documented in load-bearing comments at the call sites.

## Hotkeys

`HotkeyMap` (in `UserData/Hotkeys.cs`) is the runtime keymap, derived from `UserConfig.Hotkeys` at startup. `Trigger` is a discriminated record (`Key(keyval, mods)` | `MouseClick(button, clickCount)`); `HotkeyAction` is the bindable-action enum. Single dispatch site is `MainWindow.ExecuteAction`.

Keyboard input goes through a window-level capture-phase `Gtk.EventControllerKey` so focused children never see bound keys (no double-fire on focused buttons). Mouse input on the video widgets goes through `Gtk.GestureClick`. Both routes canonicalize the trigger via `Trigger.MakeKey` (lowercases keyvals via `gdk_keyval_to_lower`, masks modifiers to GTK's default-mod-mask) so CapsLock and `<Shift>F`-style author forms compare correctly against live events.

`PreferencesDialog` edits the runtime map (alongside the application settings); saving persists to `config.toml` via `MainWindow.ApplyPreferences` and refreshes the menu accel labels via `MainWindow.RefreshMenuAccels` (Gio.MenuItem isn't live-bound to its parent — accel labels update via remove-and-reinsert at the same slot).

## Persistence

- **`config.toml`** (TOML, Tomlyn-backed). `[hotkeys]` (a plain action → trigger-strings table; HotkeyMap owns schema and defaults) and `[application]` (open_in_new_window, theme, chapter_seek_preroll_seconds). Loaded eagerly at startup; saved on preferences edits.
- **`state.db`** (SQLite, `Microsoft.Data.Sqlite`, WAL). Owned by `StateDatabase`; per-feature persistence classes (`RecentFiles`, `TrackPreferences`, `SavedPlaylists`) take the `SqliteConnection` in their ctor. Append-only `Migrations[]` registry walked against `PRAGMA user_version`. Schema today:
  - v1: `recent_files(path_or_uri UNIQUE, last_opened, open_count)`
  - v2: `track_preferences(directory, kind, …)` PK `(directory, kind)`
  - v3: `recent_files.position_seconds REAL` for per-file resume
  - v4: `saved_playlists(guid PK, title, last_used_at, stream_count, payload_json)` — autosaved playlist history; payload is a JSON array of `{slot, current_index, items}`, schemaless room to grow

  Adding a v(N+1) is one append + one new migration test that pre-stages a vN DB via the internal `OpenConnectionAndMigrateTo(path, N)` escape hatch and verifies data survives the upgrade. Never edit a published migration's body — that would silently change schema for users whose DB already passed through it.

- **Saved-playlist autosave.** `PlaylistAutosave` subscribes to each bound context's `Playlist.Changed` and to the title-source `PropertyChanged` events. GUID-mint rule (decided purely from the Changed payload + bind state, no caller coordination): a Primary-Replace with no Secondary bound or Secondary's playlist empty mints a new GUID; everything else (incremental mutations, Secondary-Replaces, and Primary-Replaces while Secondary is also non-empty — i.e., one slot of a multi-stream entry being edited) re-uses the current GUID. Surfaces as File → Recent; `PlaylistMenuSelector` selects 10 entries with directory-coverage over the most-recent 5 distinct directories.

## VRR classification

`wp_presentation_feedback.presented` fires per composited frame; the native shim forwards `(tv_ns, refresh_ns)` to `FrameTimingBridge` via a GCHandle trampoline. The bridge keeps a 60-sample ring of inter-frame deltas. `VrrClassifier` is pure: it compares the residual of each delta against the panel's nominal period `T = 1e9 / mode_mHz`. `T` comes from `wl_output.mode` (authoritative panel mode — KWin's `refresh_ns` field is unreliable on VRR-capable outputs).

The panel's VRR window (min/max refresh) is parsed from EDID by `EdidParser` (Range Limits descriptor). The blob comes from sysfs: `wl_output` v4's `.name` event supplies the DRM connector name ("HDMI-A-1"), and `EdidLookup` reads `/sys/class/drm/card*-<connector>/edid`, resolving registry name → EDID blob → VrrRange so the policy layer can ask "does this mode lie inside the panel's VRR window?". (Note: sysfs is unreadable inside a Flatpak sandbox, so the VRR window stays unknown there.)

`VrrPolicy` is a pure function `(sourceFps, vrrRange, currentRefreshHz, fpsTrusted) → multiplier decision` — the current mode's refresh is a hard ceiling on the multiplied rate. For low-fps sources (24/25 fps) it produces an integer multiplier N ≥ 2 such that `N * sourceFps` lies inside the VRR window — `VideoContext.ApplyVrrPolicy` installs that as an mpv `vf=fps=…` filter.

`FpsTrustMonitor` (consumed by `Playback`) compares the rolling `estimated-vf-fps` against the rate it is currently *expected* to converge to — the declared `container-fps`, times the multiplier while our own fps filter is active (without that distinction the filter would read as divergence and unwind itself). Sustained divergence within a file load flips the trust state; sticky-untrusted means `ApplyVrrPolicy` will clear the multiplier on VFR / mistagged-CFR sources rather than running them at the wrong rate.

Identifiers across the ABI are `wl_output` registry names (uint32, stable for the session), not pointers — this keeps consumer lifetime decoupled from proxy lifetime.

## ABI boundary policy

`native/hdr_helper.c` contains only what the Wayland C ABI forces: inline protocol stubs that aren't directly P/Invoke-able, listener function-pointer structs, the per-handle state those listeners need, the one-shot globals-binding / image-description handshakes (protocol completion, not policy), and a registry-name → `wl_output*` lookup. Classification, thresholds, accumulation, VRR math, and logging all live in C# behind trampoline callbacks (`vompl_set_output_callbacks`, `vompl_video_surface_set_callbacks`). See CLAUDE.md § "Native code is for ABI interop only".

## Tests

NUnit in `test/`, biased toward code that's testable without a GTK/mpv runtime: pure functions, stateful units with injected clocks/sinks, marshalling/layout contracts, persistence (each test uses a temp dir and exercises real SQLite migrations), and the VM coordinator via the `IPlayback` + `Action<Action> postToMainThread` synchronous seam. UI (`MainWindow` / `VideoView` / `VideoSurface` at runtime) is not unit-tested; changes there require a manual smoke in a GTK session. `ls test/` for the inventory.

## Configuration / external libraries

- `libmpv.so` — runtime required (`mpv_*`)
- `libhdr_helper.so` — built from `src/Native/hdr_helper.c` + generated protocol glue
- `yt-dlp` — runtime optional, required for the Open URL flow. `IUrlDownloader.IsAvailable()` gates the menu path; missing yt-dlp surfaces a clear "install yt-dlp" message rather than letting the user type a URL and then failing.
- Tomlyn (NuGet) — TOML deserialization for `UserConfig`. 2.x uses `System.Text.Json.JsonNamingPolicy.SnakeCaseLower` for property naming so `[ui_section] some_key` maps to PascalCase POCO members
- Microsoft.Data.Sqlite (NuGet) — SQLite for `state.db`. Bundles `SQLitePCLRaw.bundle_e_sqlite3`, so no system SQLite needed
- CommunityToolkit.Mvvm (NuGet) — `ObservableObject`, `[ObservableProperty]`, `[RelayCommand]` for VM/context plumbing
- `libgtk-4.so.1`, `libgobject-2.0.so.0`, `libEGL.so.1`, `libGL.so.1`, `libc.so.6` — P/Invoke targets with explicit SONAMEs (bare `.so` names are dev-package symlinks that don't exist on runtime-only hosts)
- GirCore 0.7.0 — note its `Gtk.EventControllerLegacy` `event` signal is not marshallable; `MainWindow.cs` connects that one signal via raw `g_signal_connect_data`.

## Known blockers / tracked issues

- **HDR on KWin requires bypassing GDK's color management.** GTK 4.20's GDK *does* bind `wp_color_manager_v1` but aborts with `"Not using color management: Can't create srgb image description"` because KWin doesn't advertise `TRANSFER_FUNCTION_SRGB` (value 9 — it exposes gamma22/bt1886/PQ/etc. instead) and GDK's sanity check requires TF_SRGB specifically. `native/hdr_helper.c` binds the protocol directly via `gdk_wayland_surface_get_wl_surface`; GDK has already bailed at that point, so there's no conflict. When either side relaxes (KWin advertises TF_SRGB, or GDK drops the requirement), the shim can be retired. To check: `grep -R TRANSFER_FUNCTION_SRGB /usr/include/gtk-4.0/` and KDE release notes.
- **`Gtk.GraphicsOffload` migration is blocked on the same HDR gap.** Would be architecturally nicer (GTK would manage the subsurface internally and dissolve the `.vompl-main-window` transparent-bg + `.vompl-chrome` opt-in-opaque CSS dance), but `Gdk.ColorState.SetColorState(Rec2100Pq)` on a `Gdk.GLTextureBuilder` is a no-op against KWin for the reason above. Rejected alternatives: runtime-patching GDK (fragile); reaching into GTK internals for the offload subsurface's `wl_surface` (not exposed, races GTK's per-frame offload decisions); plain `Gtk.Picture` (loses the transparency-hack benefit, inherits the X11-path UI-blowout bug); bifurcating SDR-on-GraphicsOffload / HDR-on-shim (keeps the CSS dance for HDR users, doesn't achieve the goal). Don't re-pitch until the upstream HDR blocker above clears.
- **Fractional scale.** The subsurface uses GTK's integer `GetScaleFactor()` with `wl_surface.set_buffer_scale`; KWin downscales, wasting GPU on 1.25/1.5/1.75 HiDPI displays. Follow-up: bind `wp_fractional_scale_v1` + `wp_viewporter` in the shim.
- **In-widget overlays over video work via `Gtk.Overlay`.** The video subsurface stacks *below* the transparent parent surface (see "Rendering paths"), so `videoOverlay.AddOverlay` children painted on the parent composite over the video with correct alpha — the compositor blends them onto the subsurface. `DiagnosticOverlay` (top-right), the fullscreen `controlsBox` / stream-toolbar OSD, and `DownloadStatusOverlay` (top-left) all rely on this. The one thing that isn't achievable this way is sampling the video's own pixels from GTK (they live on a separate compositor surface GTK never sees), so effects that need frame content — a tint derived from the picture, say — would still need another path; text / progress / marker overlays don't. (This bullet previously claimed overlays were impossible, describing an earlier, rejected stacking order where the subsurface sat above the parent.)
- **No `ReportSwap` on the GLArea path.** mpv's display-sync accounting wants a post-swap hook and `Gtk.GLArea`'s render cycle doesn't expose one. Wayland path reports correctly via `eglSwapBuffers`.
- **8-bit FBO + PQ encoding may band.** The subsurface's EGL config is RGBA8 (`hdr_helper.c:choose_egl_config`). For HDR sources, mpv now emits PQ pass-through into that 8-bit FBO before the compositor tonemaps; PQ in 8 bits can show visible banding in dark/midtone regions. SDR sources are unaffected (8 bits across [0,1] sRGB-like range is fine). Fix: bind `EGL_RED_SIZE=10` etc. in `choose_egl_config` and verify driver/compositor honor the 10-bit window-surface format. Pre-fix the population hitting this was small (HDR-display-only); post-fix every HDR source on every display goes the PQ path so the affected population grew.
- **mpv source-mastering metadata not propagated.** The PQ image description's mastering-display primaries are hard-coded placeholders (and the values look wrong by ~2 orders of magnitude — `0.034, 0.016, …` instead of BT.2020's `0.708, 0.292, …`); we never set `mastering_luminance`, `max_cll`, or `max_fall`. KWin appears to tolerate the bogus primaries silently and use defaults for the missing fields, but a stricter compositor could refuse the description build, and even on KWin the tonemap quality could improve with real per-source metadata. The parameters now live in C# (`VideoSurface.SetHdr` builds an `ImageDescriptionParams` the shim transports verbatim), so the fix is C#-only: read `video-params/sig-peak`, `mastering-display-meta-*`, `content-light-*` from mpv's properties on file load and stage a per-source description (the shim's two-slot description cache needs growing alongside).
