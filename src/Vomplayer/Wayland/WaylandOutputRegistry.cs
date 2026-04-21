using System.Collections.Generic;

namespace Vomplayer.Wayland;

// Process-global registry of wl_output state, keyed by the compositor-assigned registry name (unique per session). Populated by trampoline callbacks from native/hdr_helper.c — see vom_set_output_callbacks in the C shim.
//
// Only the current-mode refresh rate (mHz) is tracked, because that's the only per-output datum the VRR classifier consumes. Keyed by name rather than by wl_output* because names are stable opaque wire-level identifiers — pointers would turn every global_remove into a potential UAF across the managed/unmanaged boundary.
//
// Single-threaded: all callbacks fire on whichever thread drives the Wayland event queue (GTK's GMainContext dispatcher, or the main thread running wl_display_roundtrip synchronously during init). No locking.
public static class WaylandOutputRegistry
{
    private static readonly Dictionary<uint, int> modesByName = new();

    // Called with MODE_CURRENT-flagged mode events only; C filters the others. refreshMhz is wl_output.mode.refresh (millihertz).
    public static void OnOutputMode(uint registryName, int refreshMhz)
    {
        modesByName[registryName] = refreshMhz;
    }

    public static void OnOutputRemoved(uint registryName)
    {
        modesByName.Remove(registryName);
    }

    public static bool TryGetMode(uint registryName, out int refreshMhz)
    {
        return modesByName.TryGetValue(registryName, out refreshMhz);
    }

    // Test-only: clear all state between tests. Not used from production code.
    internal static void Reset()
    {
        modesByName.Clear();
    }
}
