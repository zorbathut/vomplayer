using Vomplayer.Wayland;

namespace Vomplayer.Tests;

[TestFixture]
public class EdidParserTests
{
    private static readonly byte[] HeaderSignature = { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };

    private static byte[] BuildEdid(int rangeLimitsSlot, byte byte4Flags, byte minHz, byte maxHz, bool validHeader = true)
    {
        var edid = new byte[128];
        if (validHeader)
        {
            for (int i = 0; i < HeaderSignature.Length; i++)
            {
                edid[i] = HeaderSignature[i];
            }
        }
        // Detailed Timing Descriptor placeholders in the slots before rangeLimitsSlot, so the parser must scan past them. Non-zero pixel-clock low-byte at offset 0 disambiguates "DTD" from "monitor descriptor".
        for (int s = 0; s < 4; s++)
        {
            int off = 54 + s * 18;
            if (s == rangeLimitsSlot)
            {
                edid[off + 0] = 0x00;
                edid[off + 1] = 0x00;
                edid[off + 2] = 0x00;
                edid[off + 3] = 0xFD;
                edid[off + 4] = byte4Flags;
                edid[off + 5] = minHz;
                edid[off + 6] = maxHz;
                edid[off + 7] = 30; // min_h_khz, irrelevant for our parse
                edid[off + 8] = 80; // max_h_khz, irrelevant
            }
            else
            {
                edid[off + 0] = 0x40; // non-zero ⇒ DTD
                edid[off + 1] = 0x12;
            }
        }
        return edid;
    }

    [Test]
    public void NullInputReturnsNull()
    {
        Assert.That(EdidParser.TryGetVrrRange(null!), Is.Null);
    }

    [Test]
    public void TooShortReturnsNull()
    {
        Assert.That(EdidParser.TryGetVrrRange(new byte[64]), Is.Null);
    }

    [Test]
    public void BadHeaderReturnsNull()
    {
        var edid = BuildEdid(0, 0, 48, 60, validHeader: false);
        Assert.That(EdidParser.TryGetVrrRange(edid), Is.Null);
    }

    [Test]
    public void NoRangeLimitsDescriptorReturnsNull()
    {
        // All four slots are DTDs; no monitor descriptor with tag 0xFD.
        var edid = new byte[128];
        for (int i = 0; i < HeaderSignature.Length; i++)
        {
            edid[i] = HeaderSignature[i];
        }
        for (int s = 0; s < 4; s++)
        {
            int off = 54 + s * 18;
            edid[off + 0] = 0x40;
        }
        Assert.That(EdidParser.TryGetVrrRange(edid), Is.Null);
    }

    [Test]
    public void Valid48To60PanelInFirstSlot()
    {
        var edid = BuildEdid(0, 0x00, 48, 60);
        var r = EdidParser.TryGetVrrRange(edid);
        Assert.That(r, Is.Not.Null);
        Assert.That(r!.Value.MinHz, Is.EqualTo(48));
        Assert.That(r.Value.MaxHz, Is.EqualTo(60));
    }

    [Test]
    public void Valid40To144PanelInLastSlot()
    {
        var edid = BuildEdid(3, 0x00, 40, 144);
        var r = EdidParser.TryGetVrrRange(edid);
        Assert.That(r, Is.Not.Null);
        Assert.That(r!.Value.MinHz, Is.EqualTo(40));
        Assert.That(r.Value.MaxHz, Is.EqualTo(144));
    }

    [Test]
    public void Byte4FlagBit1_AddsTo255MaxOnly()
    {
        // flags = 0b10: max +255, min untouched (VESA E-EDID 1.4 Table 3.28; kernel drm_edid.h DRM_EDID_RANGE_OFFSET_MAX_VFREQ = bit 1). This is the encoding every real >255 Hz panel emits — modeled here on a 48-360 Hz gaming panel: stored (48, 105) → [48, 360].
        var edid = BuildEdid(2, 0x02, 48, 105);
        var r = EdidParser.TryGetVrrRange(edid);
        Assert.That(r, Is.Not.Null);
        Assert.That(r!.Value.MinHz, Is.EqualTo(48));
        Assert.That(r.Value.MaxHz, Is.EqualTo(360));
    }

    [Test]
    public void Byte4FlagBits11_AddsTo255BothMinAndMax()
    {
        // flags = 0b11: both min and max +255 (bit 0 = min offset, bit 1 = max offset, applied independently). Synthetic 280-360 Hz panel: stored (25, 105) → [280, 360].
        var edid = BuildEdid(0, 0x03, 25, 105);
        var r = EdidParser.TryGetVrrRange(edid);
        Assert.That(r, Is.Not.Null);
        Assert.That(r!.Value.MinHz, Is.EqualTo(280));
        Assert.That(r.Value.MaxHz, Is.EqualTo(360));
    }

    [Test]
    public void Byte4FlagBit0Alone_MinOffsetWithoutMaxIsDegenerate()
    {
        // flags = 0b01: min +255 only (kernel DRM_EDID_RANGE_OFFSET_MIN_VFREQ = bit 0). With max stored ≤ 255 and un-offset, min ends up above max in every representable case, so this encoding always falls out via the min ≥ max rejection: stored (25, 105) → 280/105 → null.
        var edid = BuildEdid(0, 0x01, 25, 105);
        Assert.That(EdidParser.TryGetVrrRange(edid), Is.Null);
    }

    [Test]
    public void RawZeroMinRejectedAsMalformed()
    {
        var edid = BuildEdid(0, 0x00, 0, 60);
        Assert.That(EdidParser.TryGetVrrRange(edid), Is.Null);
    }

    [Test]
    public void RawFFWithoutOffsetFlagRejected()
    {
        var edid = BuildEdid(0, 0x00, 48, 0xFF);
        Assert.That(EdidParser.TryGetVrrRange(edid), Is.Null);
    }

    [Test]
    public void MinGreaterThanOrEqualToMaxRejected()
    {
        Assert.That(EdidParser.TryGetVrrRange(BuildEdid(0, 0x00, 60, 60)), Is.Null);
        Assert.That(EdidParser.TryGetVrrRange(BuildEdid(0, 0x00, 70, 60)), Is.Null);
    }

    [Test]
    public void MaxAbove480Rejected()
    {
        // Stored 25/230 with max +255 (flags 0b10): 25/485 → rejected by the 480 Hz plausibility cap.
        var edid = BuildEdid(0, 0x02, 25, 230);
        Assert.That(EdidParser.TryGetVrrRange(edid), Is.Null);
    }
}
