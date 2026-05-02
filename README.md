# vomplayer

A cross-platform video player that just handles the whole HDR/VRR/hardware-acceleration mess for you. It just works! You don't have to screw with it and reconfigure it! Probably!

## Status

Pre-release.

Linux+KDE+Wayland is exercised daily. Do the others work? Who knows, man. If you actually want to use this, and it doesn't work for you, let me know.

## Features

HDR, VRR, hardware acceleration, subtitles, audio/video tracks, easy cueing via chapter markers.

Keeps track settings stored per-directory, not per-file, so if you're watching a series of stuff, you don't have to go reset the options for every single video.

## Building

Requires .NET 10 SDK and, on Linux, a C toolchain plus Wayland and EGL headers (for the native HDR/timing shim).

```sh
dotnet build
dotnet run --project Vomplayer.csproj -- [path-or-url]
```

Tests:

```sh
dotnet test
```

Flatpak:

```sh
flatpak-builder --user --install --force-clean build-flatpak flatpak/net.vomplayer.Vomplayer.yml
flatpak run net.vomplayer.Vomplayer [path-or-url]
```

See [flatpak/README.md](flatpak/README.md) for prerequisites, hardware acceleration notes, and file associations.

## Architecture

See [ARCHITECTURE.md](ARCHITECTURE.md) for the codebase map, the two rendering paths, the playback threading model, and the ABI-boundary policy.

## License

MIT — see [LICENSE](LICENSE). libmpv is LGPLv2.1+ and used as a dynamic-link dependency.

## Capitalization

The following capitalizations are valid:

vomplayer  
Vomplayer  
VomplAyer  
Vompl Ayer  

"VomPlayer" is explicitly not allowed. That's not how it's spelled or pronounced. Deal with it.
