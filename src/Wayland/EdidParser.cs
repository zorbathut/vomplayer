using System;

namespace Vomplayer.Wayland;

// Pure parser for the VRR-relevant slice of an EDID base block: the Display Range Limits Descriptor (tag 0xFD). Returns the panel's vertical refresh range in Hz, or null when the descriptor is missing or malformed.
//
// Reference: VESA EDID 1.4, section 3.10.4 ("Display Range Limits Descriptor"). The Range Limits structure has been in EDID since 1.3 (1996); EDID 1.4 added the byte-4 offset flags so panels with rates above 255 Hz can be represented.
//
// We deliberately only touch the fields we need (header signature, the four descriptor blocks, and the Range Limits' min/max vertical Hz with EDID-1.4 offset flags). Skipping the full block checksum is intentional: bad EDIDs land on null via the bounds gate at the bottom, and a checksum mismatch on an otherwise-readable Range Limits block tends to be benign noise (e.g. KMS modes in the descriptor area being slightly mismatched). The cost of strict checksum enforcement would be losing legitimate VRR ranges.
public static class EdidParser
{
    private const int BaseBlockSize = 128;
    private const int FirstDescriptorOffset = 54;
    private const int DescriptorSize = 18;
    private const int DescriptorCount = 4;
    private const byte RangeLimitsTag = 0xFD;

    // Sanity bounds for the parsed range. Lower bound 1 Hz catches all-zero / unpopulated descriptor slots; upper 480 Hz is well above any panel that exists today (current high-end is 540 Hz on a niche 24") and above-480 EDIDs we encounter are almost certainly bad data.
    private const int MinPlausibleHz = 1;
    private const int MaxPlausibleHz = 480;

    private static readonly byte[] EdidHeaderSignature = { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };

    public static VrrRange? TryGetVrrRange(byte[] edid)
    {
        if (edid == null || edid.Length < BaseBlockSize)
        {
            return null;
        }
        // Header signature anchor: if this isn't matched, we may be reading garbage (an empty edid sysfs file padded to some length, an unconnected port's stub, etc). Cheap to verify and load-bearing for safety on the eight bytes we depend on later.
        for (int i = 0; i < EdidHeaderSignature.Length; i++)
        {
            if (edid[i] != EdidHeaderSignature[i])
            {
                return null;
            }
        }
        for (int slot = 0; slot < DescriptorCount; slot++)
        {
            int off = FirstDescriptorOffset + slot * DescriptorSize;
            // Descriptor blocks have bytes 0-2 = 0 and byte 3 = tag. A non-zero in 0-2 means this slot is a Detailed Timing Descriptor (real mode), not a monitor descriptor.
            if (edid[off] != 0x00 || edid[off + 1] != 0x00 || edid[off + 2] != 0x00)
            {
                continue;
            }
            if (edid[off + 3] != RangeLimitsTag)
            {
                continue;
            }
            return ParseRangeLimits(edid, off);
        }
        return null;
    }

    // EDID 1.4 byte-4 vertical-rate offset flag encoding (bits 1:0):
    //   00 = no offsets, byte 5 = min Hz, byte 6 = max Hz.
    //   01 = max only +255 (max stored value + 255).
    //   10 = both min and max +255.
    //   11 = same as 10 (reserved by the spec but several implementations emit it).
    // Pre-1.4 EDIDs always had byte 4 = 0; if bytes 5 or 6 read 0xFF in that case, the panel was inexpressible — we treat it as malformed and return null rather than silently capping at 255 Hz.
    private static VrrRange? ParseRangeLimits(byte[] edid, int off)
    {
        byte flags = edid[off + 4];
        int verticalFlags = flags & 0b11;
        int minOffset = verticalFlags >= 0b10 ? 255 : 0;
        int maxOffset = verticalFlags >= 0b01 ? 255 : 0;
        int rawMin = edid[off + 5];
        int rawMax = edid[off + 6];
        if (verticalFlags == 0 && (rawMin == 0xFF || rawMax == 0xFF))
        {
            return null;
        }
        int minHz = rawMin + minOffset;
        int maxHz = rawMax + maxOffset;
        if (minHz < MinPlausibleHz || maxHz > MaxPlausibleHz || minHz >= maxHz)
        {
            return null;
        }
        return new VrrRange(minHz, maxHz);
    }
}
