# Flatpak

This directory holds everything flatpak-builder needs to produce
`io.github.zorbathut.vomplayer` from the project tree.

## Files

| File | Purpose |
|------|---------|
| `io.github.zorbathut.vomplayer.yml` | Manifest. Pulls GNOME 49 + dotnet10 SDK extension, builds libplacebo, libmpv, then `dotnet publish`-es vomplayer self-contained. |
| `io.github.zorbathut.vomplayer.desktop` | Desktop entry with `MimeType=` covering the formats `Util/MediaExtensions.cs` enumerates. |
| `io.github.zorbathut.vomplayer.metainfo.xml` | AppStream metadata required by the Flathub validator. |
| `vomplayer.sh` | `/app/bin/vomplayer` trampoline that execs the .NET apphost from `/app/lib/vomplayer`. |
| `icons/io.github.zorbathut.vomplayer.svg` | Application icon. Placeholder — replace before any release. |

## One-time prerequisites

```sh
flatpak install --user flathub \
    org.gnome.Platform//49 \
    org.gnome.Sdk//49 \
    org.freedesktop.Sdk.Extension.dotnet10//25.08 \
    org.freedesktop.Platform.codecs-extra//25.08-extra
```

(The Mesa GL extension is auto-installed alongside `org.gnome.Platform`.
NVIDIA users get the matching `org.freedesktop.Platform.GL.nvidia-*`
extension auto-installed on first run if the host driver is proprietary.)

## Build + install

From the repo root:

```sh
flatpak-builder --user --install --force-clean build-flatpak \
    flatpak/io.github.zorbathut.vomplayer.yml
```

Run:

```sh
flatpak run io.github.zorbathut.vomplayer [path-or-url]
```

## Build a redistributable bundle

`flatpak build-bundle` packages the app into a single `.flatpak` file that
anyone with flatpak (and the matching runtime) can install with one command —
no flatpak-builder, no source tree, no network build. The flow is two steps:
build into a local OSTree repo, then export a bundle from it.

```sh
# 1. Build into a local repo (./repo) instead of installing.
flatpak-builder --force-clean --repo=repo build-flatpak \
    flatpak/io.github.zorbathut.vomplayer.yml

# 2. Export a single-file bundle.
flatpak build-bundle repo vomplayer.flatpak io.github.zorbathut.vomplayer
```

The resulting `vomplayer.flatpak` is self-contained for the *app*, but the
recipient still needs `org.gnome.Platform//49` and the
`org.freedesktop.Platform.codecs-extra//25.08-extra` extension installed on
their flatpak (the bundle does not embed runtimes — that would balloon it to
hundreds of MB for no benefit, since flatpak shares runtimes across apps).
Recipient install:

```sh
flatpak install --user vomplayer.flatpak
flatpak run io.github.zorbathut.vomplayer
```

To bundle a debug build with full symbols (useful for sharing crash reports),
add `--runtime` to also bundle the `.Debug` extension flatpak-builder produces
alongside the app:

```sh
flatpak build-bundle repo vomplayer-debug.flatpak \
    io.github.zorbathut.vomplayer.Debug --runtime
```

## Hardware acceleration

The manifest builds libmpv with VA-API (Wayland + X11) and VDPAU. At runtime:

* `--device=dri` exposes `/dev/dri/*` so VA-API / VDPAU / direct GL on the GPU work.
* `LIBVA_DRIVERS_PATH` and `VDPAU_DRIVER_PATH` point at the GL extension's
  driver dirs (`/usr/lib/x86_64-linux-gnu/GL/lib/{dri,vdpau}`); without these,
  libva's default `/usr/lib/dri` lookup misses Mesa's back-ends and falls back
  to software decode on radeonsi/iHD/i965/...
* The runtime's `org.freedesktop.Platform.GL.default` extension carries Mesa's
  drivers and matching VA-API/VDPAU back-ends.
* `org.freedesktop.Platform.codecs-extra` supplies the patent-encumbered codecs
  (openh264, etc.) the base runtime ships without; mounted at
  `/app/extensions/codecs-extra` and added to LD_LIBRARY_PATH automatically.
* On hosts with the proprietary NVIDIA driver, flatpak transparently installs
  `org.freedesktop.Platform.GL.nvidia-<version>` and mpv's `--hwdec=nvdec`
  picks it up via libnvcuvid.

mpv decides between these via its standard `--hwdec=auto-safe` machinery;
vomplayer does not currently force a specific back-end.

## File associations

The desktop entry advertises MIME types matching `src/Util/MediaExtensions.cs`
(video + audio). After install, file managers offer Vomplayer in "Open With"
for matching files; you can set it as default the same way you would for any
other flatpak app.

## Notes for Flathub submission

Two things in this manifest are unsuitable for a Flathub submission and need
adjustment before submitting:

1. **`--share=network` on the vomplayer module.** Flathub requires offline
   builds. Run [flatpak-builder-tools/dotnet-sdk/flatpak-dotnet-generator.py](https://github.com/flatpak/flatpak-builder-tools/tree/master/dotnet-sdk)
   to produce a `nuget-sources.json` and replace the `--share=network` build
   arg with that source list.
2. **`--filesystem=host:ro`.** Flathub prefers the more constrained portal
   approach (which vomplayer's GTK file picker already uses on the inside).
   Drop this and rely on portal-mediated access for ad-hoc paths; keep the
   `xdg-*` filesystem entries for the common cases.

The icon is a placeholder; Flathub also expects a real launcher icon and at
least one screenshot referenced from the metainfo.
