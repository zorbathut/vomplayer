using System;
using System.Collections.Generic;

namespace Vomplayer.Wayland;

// Process-global registry of wl_output state, keyed by the compositor-assigned registry name (unique per session). Populated by trampoline callbacks from native/hdr_helper.c — see vompl_set_output_callbacks in the C shim.
//
// Tracks two per-output facts:
//   - current-mode refresh rate (mHz), consumed by the VRR classifier.
//   - HDR-capability bit, derived from the output's preferred image description's transfer function (PQ/HLG ⇒ HDR).
// Keyed by name rather than by wl_output* because names are stable opaque wire-level identifiers — pointers would turn every global_remove into a potential UAF across the managed/unmanaged boundary.
//
// Single-threaded: all callbacks fire on whichever thread drives the Wayland event queue (GTK's GMainContext dispatcher, or the main thread running wl_display_roundtrip synchronously during init). No locking.
public static class WaylandOutputRegistry
{
    private static readonly Dictionary<uint, int> modesByName = new();
    private static readonly Dictionary<uint, bool> isHdrByName = new();

    // Fires (on the same thread as OnOutputHdr, i.e. the Wayland dispatch thread) whenever an output's HDR bit is set or changes. Consumers filter by registry name — this is a single process-wide signal rather than per-surface, so subscribers for a particular surface's current output must self-filter.
    public static event Action<uint>? IsHdrChanged;

    // Called with MODE_CURRENT-flagged mode events only; C filters the others. refreshMhz is wl_output.mode.refresh (millihertz).
    public static void OnOutputMode(uint registryName, int refreshMhz)
    {
        modesByName[registryName] = refreshMhz;
    }

    // Called when the shim finishes introspecting an output's preferred image description (initial handshake or wp_color_management_output_v1.image_description_changed). isHdr is true iff the transfer function is PQ or HLG.
    public static void OnOutputHdr(uint registryName, bool isHdr)
    {
        bool changed = !isHdrByName.TryGetValue(registryName, out var prev) || prev != isHdr;
        isHdrByName[registryName] = isHdr;
        if (changed)
        {
            IsHdrChanged?.Invoke(registryName);
        }
    }

    public static void OnOutputRemoved(uint registryName)
    {
        modesByName.Remove(registryName);
        isHdrByName.Remove(registryName);
    }

    public static bool TryGetMode(uint registryName, out int refreshMhz)
    {
        return modesByName.TryGetValue(registryName, out refreshMhz);
    }

    public static bool TryGetIsHdr(uint registryName, out bool isHdr)
    {
        return isHdrByName.TryGetValue(registryName, out isHdr);
    }

    // Test-only: clear all state between tests. Not used from production code.
    internal static void Reset()
    {
        modesByName.Clear();
        isHdrByName.Clear();
        IsHdrChanged = null;
    }
}
