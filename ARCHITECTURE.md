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
  Program.cs             # entry, arg parsing, Gtk.Application wiring, Playback construction
  MainWindow.cs          # code-only GTK4 window: widgets, input controllers, VM <-> view glue
  MainWindow.Menu.cs     # menu bar + Gio.SimpleAction registration + accelerators
  Epoxy.cs               # eglGetProcAddress + glGetIntegerv for FBO binding
  LibC.cs                # setlocale(LC_NUMERIC,"C") — mpv refuses non-C LC_NUMERIC
  Controls/
    VideoView.cs         # Gtk.GLArea path: GLArea-owned FBO, mpv renders into it
    VideoArea.cs         # Wayland path: DrawingArea that paints nothing, emits GeometryChanged
  Mpv/
    LibMpv.cs            # P/Invoke surface for libmpv (client + render_context)
    MpvClient.cs         # thin C# wrapper: events, observed properties, commands
    MpvDispatcher.cs     # worker thread that serializes client-API calls; hands out ref-struct MpvHandle inside Post callbacks
    MpvRenderContext.cs  # mpv_render_context_* wrapper, ADVANCED_CONTROL contract
  Playback/
    IPlayback.cs         # interface the VM depends on
    Playback.cs          # owns MpvClient, maps mpv events -> ObservableObject properties
  Services/
    IFilePicker.cs
    FilePickerGtk.cs     # Gtk.FileDialog
  Util/TimeFormatter.cs
  ViewModels/
    ViewModelMain.cs     # CommunityToolkit.Mvvm, RelayCommands, seek scale glue
  Wayland/
    WaylandDetect.cs     # backend sniff via gdk_wayland_display_get_wl_display
    VomWayland.cs        # P/Invoke to libhdr_helper.so (subsurface + callbacks)
    VideoSurface.cs      # Wayland-path orchestrator: window lifecycle, render loop, HDR/VRR exposure
    FrameTimingBridge.cs # per-surface presentation-feedback accumulator, VRR ring, stats log
    VrrClassifier.cs     # pure function: residual-against-T-grid classifier
    HdrClassifier.cs     # pure function: wp_color_management_output_v1 tf_named -> HDR y/n
    WaylandOutputRegistry.cs # process-global wl_output mode table (keyed by registry name)
  Native/
    hdr_helper.c                                # Wayland ABI shim, see "ABI boundary" below
    color-management-v1-*                       # wayland-scanner output
    presentation-time-*                         # wayland-scanner output

test/                    # NUnit; references main project via InternalsVisibleTo
```

The native shim is compiled by an MSBuild `BuildHdrHelper` target in `Vomplayer.csproj` (Linux only) and copied to the output directory.

## Rendering paths

Two runtime-selected paths, chosen by `WaylandDetect.IsWaylandBackend` in `MainWindow` construction.

**Wayland subsurface path (preferred on Linux/Wayland).**
`VideoArea` (paints nothing) reserves layout space. `VideoSurface` creates a `wl_subsurface` of the GTK main `wl_surface` via `libhdr_helper.so` and places it *below* the parent. GTK's main window is CSS-transparent (`.vom-main-window { background: transparent; }`) so the subsurface shows through in the video region; opaque chrome widgets (`.vom-chrome`) sit on top. This means:
- HDR PQ/BT.2020 tagging lives only on the subsurface; the GTK UI surface stays sRGB and renders correctly. Tagging is driven by the combined policy `source video is PQ/HLG AND current output is HDR` — both must be true, else we stay SDR. Signals:
  - `Playback` observes `video-params/gamma` and fires `SourceHdrChanged(bool)` per-video.
  - `VideoSurface.CurrentOutputHdrChanged` fires on output-side change — `wl_surface.enter`/`leave` mutating the active-output set, or `wp_color_management_output_v1.image_description_changed` propagating through `WaylandOutputRegistry`. Per-output HDR-capability is detected by probing the preferred image description's `tf_named` (PQ/HLG ⇒ HDR); classification lives in `HdrClassifier` on the C# side.
  - `MainWindow.ApplyHdrPolicy` combines the two signals and calls `VideoSurface.SetHdr` (stages `set_image_description` / `unset_image_description` that the next `eglSwapBuffers` flushes atomically with the first new-content buffer) and `Playback.EnableHdrOutput` / `DisableHdrOutput` for mpv's `target-*` targeting.
  - `--sdr` forces SDR for the whole session and skips both subscriptions.
- Controls overlaid in fullscreen (via `Gtk.Overlay` reparent) draw on top of the video with correct alpha.
- Pointer input falls through to the parent (empty input region on the subsurface) so motion-driven auto-hide works.
- `wp_presentation_feedback` per swap drives the `FrameTimingBridge` ring; VRR/fixed is classified against `wl_output.mode` refresh.

**Gtk.GLArea fallback path (X11 / other backends).**
`VideoView` is a `Gtk.GLArea`; mpv renders into GTK's owned FBO. HDR is not supported on this path — mpv tonemaps HDR source content down to SDR via its default `auto` targeting. Main-surface PQ tagging was tried and reverted: it produced a blown-out GTK UI (widgets render sRGB values into a surface KWin interprets as PQ) and couldn't be toggled per-file without tearing down the GTK surface.

## Playback data flow and threading

Three distinct threads touch mpv, each with its own role:
- **mpv's event thread** (libmpv-internal) — fires the wakeup callback.
- **mpv-dispatcher worker** (`MpvDispatcher`, owned by `Playback`) — the ONLY thread that calls the mpv *client-API* surface (SetProperty, Command, ObserveProperty, DrainEvents, Initialize, etc). Enforced by the ref-struct `MpvHandle`: handles are handed out only inside `MpvDispatcher.Post` callbacks, and a ref struct can't be captured in a closure, stored in a field, awaited across, or otherwise leaked out of the post scope — so it's impossible to call a client-API method from the wrong thread.
- **GTK main thread** — owns GL context and window lifecycle; calls `mpv_render_context_*` (Update / Render / ReportSwap) via `MpvRenderContext`. Render-context construction also happens here (`mpv_render_context_create` reads the mpv handle but must run on the GL-owning thread); `MpvDispatcher.CreateRenderContext` is the controlled escape hatch that wraps the ctor. Render-context calls don't deadlock the way client-API calls do.

```
mpv event thread:                mpv-dispatcher worker:         GTK main thread:
  wakeup callback   --post--->     DrainEvents
                                     PropertyChanged  ---postToMainThread--->   Playback.ObservableProperties
                                     FileLoaded/etc   ---postToMainThread--->   ViewModelMain -> widgets
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

Cross-thread coupling is isolated in exactly three places: `Playback` (constructor takes an `Action<Action> postToMainThread` so tests can pass `a => a()`), `MpvDispatcher` (worker thread + queue), and the render-queue coalescer in `VideoSurface`/`VideoView`.

`Playback` keeps `MpvClient` behind the `MpvDispatcher` boundary — `MpvClient` is `internal` and exposed only via the ref-struct `MpvHandle` inside `Post` callbacks. `AttachRenderSurface(Action<MpvDispatcher> attach)` hands out the dispatcher for render-context construction; consumers call `CreateRenderContext` on it rather than touching an mpv handle directly.

## MVVM

- VM is `ViewModelMain` (CommunityToolkit.Mvvm `ObservableObject` + `[RelayCommand]`).
- View is `MainWindow`, code-only GTK4. It subscribes to VM `PropertyChanged` and switches on property name to push widget updates. User actions go to VM commands.
- Seek is the nontrivial interaction: a state machine (idle / holding / settling) in `MainWindow.cs` around the `Gtk.Scale`. See comments there.

## VRR classification

`wp_presentation_feedback.presented` fires per composited frame; the native shim forwards `(tv_ns, refresh_ns)` to `FrameTimingBridge` via a GCHandle trampoline. The bridge keeps a 60-sample ring of inter-frame deltas. `VrrClassifier` is pure: it compares the residual of each delta against the panel's nominal period `T = 1e9 / mode_mHz`. `T` comes from `wl_output.mode` (authoritative panel mode — KWin's `refresh_ns` field is unreliable on VRR-capable outputs).

Identifiers across the ABI are `wl_output` registry names (uint32, stable for the session), not pointers — this keeps consumer lifetime decoupled from proxy lifetime.

## ABI boundary policy

`native/hdr_helper.c` contains only what the Wayland C ABI forces: inline protocol stubs that aren't directly P/Invoke-able, listener function-pointer structs, the per-handle state those listeners need, the one-shot globals-binding / image-description handshakes (protocol completion, not policy), and a registry-name → `wl_output*` lookup. Classification, thresholds, accumulation, VRR math, and logging all live in C# behind trampoline callbacks (`vom_set_output_callbacks`, `vom_video_surface_set_callbacks`). See CLAUDE.md § "Native code is for ABI interop only".

## Tests

NUnit in `test/`. Coverage is biased toward the code that's testable without a GTK/mpv runtime:
- `VrrClassifierTests` — pure function, straightforward table tests.
- `FrameTimingBridgeTests` — ring/stats accumulator with injected log sink.
- `WaylandOutputRegistryTests` — static registry.
- `MpvClientObserveTests`, `MpvPropertyValueTests`, `MpvRenderStructLayoutTests`, `MpvRenderContextContractTests` — marshalling/layout contracts that can be verified without a running mpv.
- `PlaybackTests`, `ViewModelMainTests` — via `IPlayback` / `Action<Action>` seam.
- `TimeFormatterTests` — trivial.

UI (MainWindow / VideoView / VideoSurface at runtime) is not unit-tested; changes there require a manual smoke in a GTK session.

## Configuration / external libraries

- `libmpv.so` — runtime required (`mpv_*`)
- `libhdr_helper.so` — built from `src/Native/hdr_helper.c` + generated protocol glue
- `libgtk-4.so.1`, `libgobject-2.0.so.0`, `libEGL.so.1`, `libGL.so.1`, `libc.so.6` — P/Invoke targets with explicit SONAMEs (bare `.so` names are dev-package symlinks that don't exist on runtime-only hosts)
- GirCore 0.7.0 — note its `Gtk.EventControllerLegacy` `event` signal is not marshallable; `MainWindow.cs` connects that one signal via raw `g_signal_connect_data`.

## Known blockers / tracked issues

- **HDR on KWin requires bypassing GDK's color management.** GTK 4.20's GDK *does* bind `wp_color_manager_v1` but aborts with `"Not using color management: Can't create srgb image description"` because KWin doesn't advertise `TRANSFER_FUNCTION_SRGB` (value 9 — it exposes gamma22/bt1886/PQ/etc. instead) and GDK's sanity check requires TF_SRGB specifically. `native/hdr_helper.c` binds the protocol directly via `gdk_wayland_surface_get_wl_surface`; GDK has already bailed at that point, so there's no conflict. When either side relaxes (KWin advertises TF_SRGB, or GDK drops the requirement), the shim can be retired. To check: `grep -R TRANSFER_FUNCTION_SRGB /usr/include/gtk-4.0/` and KDE release notes.
- **`Gtk.GraphicsOffload` migration is blocked on the same HDR gap.** Would be architecturally nicer (GTK would manage the subsurface internally and dissolve the `.vom-main-window` transparent-bg + `.vom-chrome` opt-in-opaque CSS dance), but `Gdk.ColorState.SetColorState(Rec2100Pq)` on a `Gdk.GLTextureBuilder` is a no-op against KWin for the reason above. Rejected alternatives: runtime-patching GDK (fragile); reaching into GTK internals for the offload subsurface's `wl_surface` (not exposed, races GTK's per-frame offload decisions); plain `Gtk.Picture` (loses the transparency-hack benefit, inherits the X11-path UI-blowout bug); bifurcating SDR-on-GraphicsOffload / HDR-on-shim (keeps the CSS dance for HDR users, doesn't achieve the goal). Don't re-pitch until the upstream HDR blocker above clears.
- **Fractional scale.** The subsurface uses GTK's integer `GetScaleFactor()` with `wl_surface.set_buffer_scale`; KWin downscales, wasting GPU on 1.25/1.5/1.75 HiDPI displays. Follow-up: bind `wp_fractional_scale_v1` + `wp_viewporter` in the shim.
- **In-widget overlays over video are not possible** on the Wayland path: the subsurface is opaque and stacks above the main surface in composite order. Any future OSD / chapter markers / time-preview tooltips must be `Gtk.Popover` anchored to `VideoArea`, not painted into a widget — popovers use their own `xdg_popup` surfaces and stack above the window correctly.
- **No `ReportSwap` on the GLArea path.** mpv's display-sync accounting wants a post-swap hook and `Gtk.GLArea`'s render cycle doesn't expose one. Wayland path reports correctly via `eglSwapBuffers`.
