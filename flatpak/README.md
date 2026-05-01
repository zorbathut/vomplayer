# Flatpak

This directory holds everything flatpak-builder needs to produce
`net.vomplayer.Vomplayer` from the project tree.

## Files

| File | Purpose |
|------|---------|
| `net.vomplayer.Vomplayer.yml` | Manifest. Pulls GNOME 49 + dotnet10 SDK extension, builds libplacebo, libmpv, then `dotnet publish`-es vomplayer self-contained. |
| `net.vomplayer.Vomplayer.desktop` | Desktop entry with `MimeType=` covering the formats `Util/MediaExtensions.cs` enumerates. |
| `net.vomplayer.Vomplayer.metainfo.xml` | AppStream metadata required by the Flathub validator. |
| `vomplayer.sh` | `/app/bin/vomplayer` trampoline that execs the .NET apphost from `/app/lib/vomplayer`. |
| `icons/net.vomplayer.Vomplayer.svg` | Application icon. Placeholder — replace before any release. |

## One-time prerequisites

```sh
flatpak install --user flathub \
    org.gnome.Platform//49 \
    org.gnome.Sdk//49 \
    org.freedesktop.Sdk.Extension.dotnet10//25.08 \
    org.freedesktop.Platform.ffmpeg-full//25.08
```

(The Mesa GL extension is auto-installed alongside `org.gnome.Platform`.
NVIDIA users get the matching `org.freedesktop.Platform.GL.nvidia-*`
extension auto-installed on first run if the host driver is proprietary.)

## Build + install

From the repo root:

```sh
flatpak-builder --user --install --force-clean build-flatpak \
    flatpak/net.vomplayer.Vomplayer.yml
```

Run:

```sh
flatpak run net.vomplayer.Vomplayer [path-or-url]
```

## Hardware acceleration

The manifest builds libmpv with VA-API (Wayland + X11 + DRM), VDPAU, and DRM
output paths enabled. At runtime:

* `--device=dri` exposes `/dev/dri/*` so VA-API / VDPAU / direct GL on the GPU work.
* The runtime's `org.freedesktop.Platform.GL.default` extension carries Mesa's
  drivers and matching VA-API/VDPAU back-ends.
* `org.freedesktop.Platform.ffmpeg-full` is mounted at `/app/lib/ffmpeg` and
  shadows the runtime's stripped ffmpeg so all common codecs decode without the
  user adding anything.
* On hosts with the proprietary NVIDIA driver, flatpak transparently installs
  `org.freedesktop.Platform.GL.nvidia-<version>` and mpv's `--hwdec=nvdec`
  picks it up via libnvcuvid.

mpv decides between these via its standard `--hwdec=auto` machinery; vomplayer
does not currently force a specific back-end.

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
