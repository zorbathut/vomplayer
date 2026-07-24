using System;
using System.Collections.Generic;

namespace Vomplayer.Wayland;

// Process-global registry of wl_output state, keyed by the compositor-assigned registry name (unique per session). Populated by trampoline callbacks from native/hdr_helper.c — see vompl_set_output_callbacks in the C shim.
//
// Tracks per-output:
//   - current-mode refresh rate (mHz), consumed by the VRR classifier.
//   - the preferred image description's raw observations (tf_named, primaries_named, luminances); HDR-capability is classified from these on read via HdrClassifier (PQ/HLG tf OR luminance headroom).
//   - DRM connector name (e.g. "HDMI-A-1") from wl_output v4 .name.
//   - Resolved VRR window (min/max Hz) for the panel, looked up via the IEdidSource at the moment the connector name lands.
// Keyed by name rather than by wl_output* because names are stable opaque wire-level identifiers — pointers would turn every global_remove into a potential UAF across the managed/unmanaged boundary.
//
// Single-threaded: all callbacks fire on whichever thread drives the Wayland event queue (GTK's GMainContext dispatcher, or the main thread running wl_display_roundtrip synchronously during init). No locking.
public static class WaylandOutputRegistry
{
    private static readonly Dictionary<uint, int> modesByName = new();
    private static readonly Dictionary<uint, OutputImageDescription> imageDescByName = new();
    private static readonly Dictionary<uint, string> namesByRegistry = new();
    private static readonly Dictionary<uint, VrrRange?> vrrRangeByRegistry = new();

    // Edid source seam: production wraps the static EdidLookup; tests inject a fake to avoid coupling unit tests to /sys/class/drm. Defaults to the live lookup so production code paths require zero ceremony.
    private static IEdidSource edidSource = new EdidLookup();

    // Fires (on the same thread as OnOutputHdr, i.e. the Wayland dispatch thread) whenever an output's HDR bit is set or changes. Consumers filter by registry name — this is a single process-wide signal rather than per-surface, so subscribers for a particular surface's current output must self-filter.
    public static event Action<uint>? IsHdrChanged;

    // Fires whenever an output's resolved VrrRange transitions: name first arrives (null → known), output is removed (known → null), or hot-plug delivers a different range. Same threading + single-process-wide-signal contract as IsHdrChanged.
    public static event Action<uint>? VrrRangeChanged;

    // Called with MODE_CURRENT-flagged mode events only; C filters the others. refreshMhz is wl_output.mode.refresh (millihertz).
    public static void OnOutputMode(uint registryName, int refreshMhz)
    {
        modesByName[registryName] = refreshMhz;
    }

    // Called when the shim finishes introspecting an output's preferred image description (initial handshake or wp_color_management_output_v1.image_description_changed). Stores the raw observation; IsHdrChanged dedup keys on the classified HDR bit, not the raw values — a raw-only change (e.g. peak-brightness override tweaked while staying HDR) doesn't fire, and the diagnostic overlay re-reads the record on its own refresh tick anyway.
    public static void OnOutputImageDescription(uint registryName, OutputImageDescription desc)
    {
        bool newIsHdr = HdrClassifier.IsHdr(desc);
        bool changed = !imageDescByName.TryGetValue(registryName, out var prev) || HdrClassifier.IsHdr(prev) != newIsHdr;
        imageDescByName[registryName] = desc;
        if (changed)
        {
            IsHdrChanged?.Invoke(registryName);
        }
    }

    // Called when wl_output v4 .name lands. Empty `name` (compositor < v4 or never emits) yields a null VrrRange entry; the dictionary still gets a key so we don't re-resolve on every getter call. Resolution happens here (not lazily) so VrrRangeChanged fires deterministically at the protocol moment when the name becomes known.
    public static void OnOutputName(uint registryName, string name)
    {
        namesByRegistry[registryName] = name ?? string.Empty;
        VrrRange? newRange = ResolveVrrRange(name);
        bool prevPresent = vrrRangeByRegistry.TryGetValue(registryName, out var prev);
        bool changed = !prevPresent || !Nullable.Equals(prev, newRange);
        vrrRangeByRegistry[registryName] = newRange;
        if (changed)
        {
            VrrRangeChanged?.Invoke(registryName);
        }
    }

    public static void OnOutputRemoved(uint registryName)
    {
        modesByName.Remove(registryName);
        imageDescByName.Remove(registryName);
        namesByRegistry.Remove(registryName);
        if (vrrRangeByRegistry.Remove(registryName))
        {
            VrrRangeChanged?.Invoke(registryName);
        }
    }

    public static bool TryGetMode(uint registryName, out int refreshMhz)
    {
        return modesByName.TryGetValue(registryName, out refreshMhz);
    }

    public static bool TryGetImageDescription(uint registryName, out OutputImageDescription desc)
    {
        return imageDescByName.TryGetValue(registryName, out desc);
    }

    public static bool TryGetName(uint registryName, out string name)
    {
        if (namesByRegistry.TryGetValue(registryName, out var stored))
        {
            name = stored;
            return true;
        }
        name = string.Empty;
        return false;
    }

    // Returns true with a resolved range, true with a null range (name was empty or EDID had no Range Limits), or false if the name hasn't arrived yet. The first two cases are distinct from the third in semantics: known-no-VRR-data vs not-yet-known.
    public static bool TryGetVrrRange(uint registryName, out VrrRange? range)
    {
        return vrrRangeByRegistry.TryGetValue(registryName, out range);
    }

    private static VrrRange? ResolveVrrRange(string? connectorName)
    {
        if (string.IsNullOrEmpty(connectorName))
        {
            return null;
        }
        byte[]? edid = edidSource.TryReadEdid(connectorName);
        if (edid == null)
        {
            return null;
        }
        return EdidParser.TryGetVrrRange(edid);
    }

    // Test-only: clear all state between tests. Not used from production code.
    internal static void Reset()
    {
        modesByName.Clear();
        imageDescByName.Clear();
        namesByRegistry.Clear();
        vrrRangeByRegistry.Clear();
        IsHdrChanged = null;
        VrrRangeChanged = null;
        edidSource = new EdidLookup();
    }

    // Test-only: inject a fake EDID source so name-lookup paths don't depend on /sys/class/drm. Reset() restores the live lookup.
    internal static void SetEdidSourceForTest(IEdidSource source)
    {
        edidSource = source ?? new EdidLookup();
    }
}
