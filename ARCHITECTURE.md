# Architecture

Living notes on the structure of the codebase. Keep this short and current; code-level "why" comments belong next to the code, not here.

## What it is

Cross-platform video player, C# + GTK4 (via GirCore) + libmpv (P/Invoke). Target: smplayer-parity feature set with a permissive-license front-end. Primary dev platform is Linux/Wayland/KWin; X11, Windows, macOS are fallback paths that degrade the HDR story but keep playback working.

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
- HDR PQ/BT.2020 tagging lives only on the subsurface; the GTK UI surface stays sRGB and renders correctly. Tagging is toggled per video: `Playback` observes `video-params/gamma` and fires `SourceHdrChanged`; `MainWindow` forwards that to `VideoSurface.SetHdr` (stages a `set_image_description` / `unset_image_description` that the next `eglSwapBuffers` flushes atomically with the first new-content buffer) and to `Playback.EnableHdrOutput` / `DisableHdrOutput` for mpv's `target-*` targeting. `--sdr` forces SDR for the whole session and skips the subscription.
- Controls overlaid in fullscreen (via `Gtk.Overlay` reparent) draw on top of the video with correct alpha.
- Pointer input falls through to the parent (empty input region on the subsurface) so motion-driven auto-hide works.
- `wp_presentation_feedback` per swap drives the `FrameTimingBridge` ring; VRR/fixed is classified against `wl_output.mode` refresh.

**Gtk.GLArea fallback path (X11 / other backends).**
`VideoView` is a `Gtk.GLArea`; mpv renders into GTK's owned FBO. HDR is not supported on this path — mpv tonemaps HDR source content down to SDR via its default `auto` targeting. Main-surface PQ tagging was tried and reverted: it produced a blown-out GTK UI (widgets render sRGB values into a surface KWin interprets as PQ) and couldn't be toggled per-file without tearing down the GTK surface.

## Playback data flow

```
mpv thread:                  main thread (GTK GMainContext):
  wakeup callback   -----IdleAdd------>   Playback.ProcessEvents
                                           -> MpvClient.DrainEvents
                                             -> Dispatch events
                                               -> PropertyChanged -> Playback.ObservableProperties
                                                                  -> ViewModelMain
                                                                    -> MainWindow widgets
```

```
mpv render thread:           main thread:
  UpdateRequested   -----IdleAdd------>   DoRender
                                           -> MakeCurrent
                                           -> mpv_render_context_update
                                           -> mpv_render_context_render
                                           -> Swap (eglSwapBuffers) / GLArea QueueRender
                                           -> mpv_render_context_report_swap
```

Cross-thread coupling is isolated in exactly two places: `Playback` (constructor takes an `Action<Action> postToMainThread` so tests can pass `a => a()`) and the render-queue coalescer in `VideoSurface`/`VideoView`.

`Playback` keeps `MpvClient` behind its boundary. The one exception is `AttachRenderSurface(Action<MpvClient> attach)` — the callback receives the client long enough to build a render context, and nothing outside that scope gets a reference.

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

- HDR on KWin requires bypassing GDK's color management (GDK opts out when the compositor doesn't advertise SRGB transfer via `wp_color_manager_v1`). Clean `Avalonia.GraphicsOffload`-style migration is blocked on this; kept in memory.
