using System.IO;

namespace Vomplayer.Services;

internal static class FlatpakDetect
{
    // True when running inside a flatpak sandbox. /.flatpak-info is created by the sandbox itself, so it's the canonical check — more reliable than the FLATPAK_ID env var, which can be inherited or spoofed.
    public static bool IsSandboxed()
    {
        return File.Exists("/.flatpak-info");
    }
}
