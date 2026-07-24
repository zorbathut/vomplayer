using System.Collections.Generic;
using Vomplayer.Wayland;

namespace Vomplayer.Tests;

[TestFixture]
public class WaylandOutputRegistryTests
{
    private sealed class FakeEdidSource : IEdidSource
    {
        public Dictionary<string, byte[]?> ByConnector { get; } = new();
        public byte[]? TryReadEdid(string connectorName)
        {
            return ByConnector.TryGetValue(connectorName, out var bytes) ? bytes : null;
        }
    }

    private static byte[] BuildEdidWithRange(int slot, byte minHz, byte maxHz)
    {
        var edid = new byte[128];
        var sig = new byte[] { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };
        for (int i = 0; i < sig.Length; i++)
        {
            edid[i] = sig[i];
        }
        for (int s = 0; s < 4; s++)
        {
            int off = 54 + s * 18;
            if (s == slot)
            {
                edid[off + 3] = 0xFD;
                edid[off + 5] = minHz;
                edid[off + 6] = maxHz;
                edid[off + 7] = 30;
                edid[off + 8] = 80;
            }
            else
            {
                edid[off + 0] = 0x40;
            }
        }
        return edid;
    }

    [SetUp]
    public void Reset()
    {
        WaylandOutputRegistry.Reset();
    }

    [Test]
    public void UnknownNameHasNoMode()
    {
        Assert.That(WaylandOutputRegistry.TryGetMode(42, out _), Is.False);
    }

    [Test]
    public void ModeStored()
    {
        WaylandOutputRegistry.OnOutputMode(7, 60000);
        Assert.That(WaylandOutputRegistry.TryGetMode(7, out var mhz), Is.True);
        Assert.That(mhz, Is.EqualTo(60000));
    }

    [Test]
    public void ModeUpdateOverwrites()
    {
        WaylandOutputRegistry.OnOutputMode(3, 60000);
        WaylandOutputRegistry.OnOutputMode(3, 120000);
        Assert.That(WaylandOutputRegistry.TryGetMode(3, out var mhz), Is.True);
        Assert.That(mhz, Is.EqualTo(120000));
    }

    [Test]
    public void RemovedOutputIsDropped()
    {
        WaylandOutputRegistry.OnOutputMode(9, 60000);
        WaylandOutputRegistry.OnOutputRemoved(9);
        Assert.That(WaylandOutputRegistry.TryGetMode(9, out _), Is.False);
    }

    [Test]
    public void RemoveOfUnknownIsNoop()
    {
        WaylandOutputRegistry.OnOutputRemoved(999);
        WaylandOutputRegistry.OnOutputMode(1, 60000);
        Assert.That(WaylandOutputRegistry.TryGetMode(1, out var mhz), Is.True);
        Assert.That(mhz, Is.EqualTo(60000));
    }

    // Description shapes mirroring what real compositors emit (see HdrClassifier): modern KWin expresses HDR-enabled as luminance headroom on a gamma22 preferred tf; legacy compositors advertise PQ outright; SDR outputs have max==ref.
    private static readonly OutputImageDescription HdrHeadroomDesc = new(HasTfNamed: true, TfNamed: 2, HasPrimariesNamed: true, PrimariesNamed: 6, HasLuminances: true, MinLum: 0, MaxLum: 390, RefLum: 201);
    private static readonly OutputImageDescription HdrPqDesc = new(HasTfNamed: true, TfNamed: 11, HasPrimariesNamed: false, PrimariesNamed: 0, HasLuminances: false, MinLum: 0, MaxLum: 0, RefLum: 0);
    private static readonly OutputImageDescription SdrDesc = new(HasTfNamed: true, TfNamed: 2, HasPrimariesNamed: true, PrimariesNamed: 1, HasLuminances: true, MinLum: 100, MaxLum: 200, RefLum: 200);

    [Test]
    public void UnknownNameHasNoImageDescription()
    {
        Assert.That(WaylandOutputRegistry.TryGetImageDescription(42, out _), Is.False);
    }

    [Test]
    public void ImageDescriptionRoundTrips()
    {
        WaylandOutputRegistry.OnOutputImageDescription(7, HdrHeadroomDesc);
        Assert.That(WaylandOutputRegistry.TryGetImageDescription(7, out var desc), Is.True);
        Assert.That(desc, Is.EqualTo(HdrHeadroomDesc));
    }

    [Test]
    public void DescriptionUpdateOverwrites()
    {
        WaylandOutputRegistry.OnOutputImageDescription(8, SdrDesc);
        WaylandOutputRegistry.OnOutputImageDescription(8, HdrHeadroomDesc);
        Assert.That(WaylandOutputRegistry.TryGetImageDescription(8, out var desc), Is.True);
        Assert.That(desc, Is.EqualTo(HdrHeadroomDesc));
    }

    [Test]
    public void DescriptionClearedOnRemoval()
    {
        WaylandOutputRegistry.OnOutputImageDescription(11, HdrHeadroomDesc);
        WaylandOutputRegistry.OnOutputMode(11, 60000);
        WaylandOutputRegistry.OnOutputRemoved(11);
        Assert.That(WaylandOutputRegistry.TryGetImageDescription(11, out _), Is.False);
        Assert.That(WaylandOutputRegistry.TryGetMode(11, out _), Is.False);
    }

    [Test]
    public void DescriptionAndModeAreIndependent()
    {
        WaylandOutputRegistry.OnOutputMode(20, 60000);
        Assert.That(WaylandOutputRegistry.TryGetImageDescription(20, out _), Is.False);
        WaylandOutputRegistry.OnOutputImageDescription(21, HdrHeadroomDesc);
        Assert.That(WaylandOutputRegistry.TryGetMode(21, out _), Is.False);
    }

    [Test]
    public void ReAddAfterRemovalStoresNewValueAndFiresEvent()
    {
        // Hot-plug cycle: remove the output, then re-add with a different HDR state. The re-add must both store the new value and fire IsHdrChanged so subscribers see the refresh.
        WaylandOutputRegistry.OnOutputImageDescription(30, HdrHeadroomDesc);
        WaylandOutputRegistry.OnOutputRemoved(30);
        var fires = new List<uint>();
        WaylandOutputRegistry.IsHdrChanged += n => fires.Add(n);
        WaylandOutputRegistry.OnOutputImageDescription(30, SdrDesc);
        Assert.That(WaylandOutputRegistry.TryGetImageDescription(30, out var desc), Is.True);
        Assert.That(HdrClassifier.IsHdr(desc), Is.False);
        Assert.That(fires, Is.EqualTo(new[] { 30u }));
    }

    [Test]
    public void IsHdrChangedFiresOnFirstSet()
    {
        var fires = new List<uint>();
        WaylandOutputRegistry.IsHdrChanged += n => fires.Add(n);
        WaylandOutputRegistry.OnOutputImageDescription(40, HdrHeadroomDesc);
        Assert.That(fires, Is.EqualTo(new[] { 40u }));
    }

    [Test]
    public void IsHdrChangedFiresOnClassificationTransition()
    {
        var fires = new List<uint>();
        WaylandOutputRegistry.IsHdrChanged += n => fires.Add(n);
        WaylandOutputRegistry.OnOutputImageDescription(41, SdrDesc);
        WaylandOutputRegistry.OnOutputImageDescription(41, HdrHeadroomDesc);
        Assert.That(fires, Is.EqualTo(new[] { 41u, 41u }));
    }

    [Test]
    public void IsHdrChangedDoesNotFireOnNoopUpdate()
    {
        WaylandOutputRegistry.OnOutputImageDescription(42, HdrHeadroomDesc);
        var fires = new List<uint>();
        WaylandOutputRegistry.IsHdrChanged += n => fires.Add(n);
        WaylandOutputRegistry.OnOutputImageDescription(42, HdrHeadroomDesc);
        Assert.That(fires, Is.Empty);
    }

    [Test]
    public void IsHdrChangedDoesNotFireOnRawValueOnlyChange()
    {
        // Dedup keys on the classified bit: a raw-value tweak that stays HDR (e.g. the user adjusts the peak-brightness override) must not fire — the diagnostic overlay re-reads the record on its refresh tick.
        WaylandOutputRegistry.OnOutputImageDescription(43, HdrHeadroomDesc);
        var fires = new List<uint>();
        WaylandOutputRegistry.IsHdrChanged += n => fires.Add(n);
        WaylandOutputRegistry.OnOutputImageDescription(43, HdrHeadroomDesc with { MaxLum = 400 });
        Assert.That(fires, Is.Empty);
        Assert.That(WaylandOutputRegistry.TryGetImageDescription(43, out var desc), Is.True);
        Assert.That(desc.MaxLum, Is.EqualTo(400));
    }

    [Test]
    public void IsHdrChangedDoesNotFireOnJustificationOnlyChange()
    {
        // The exact transition a future KWin re-advertising PQ would produce: HDR-via-headroom replaced by HDR-via-tf. Classification stays HDR, so no event — but the stored record must still update.
        WaylandOutputRegistry.OnOutputImageDescription(44, HdrHeadroomDesc);
        var fires = new List<uint>();
        WaylandOutputRegistry.IsHdrChanged += n => fires.Add(n);
        WaylandOutputRegistry.OnOutputImageDescription(44, HdrPqDesc);
        Assert.That(fires, Is.Empty);
        Assert.That(WaylandOutputRegistry.TryGetImageDescription(44, out var desc), Is.True);
        Assert.That(desc, Is.EqualTo(HdrPqDesc));
    }

    [Test]
    public void NameStoredAndRetrievable()
    {
        WaylandOutputRegistry.OnOutputName(50, "HDMI-A-1");
        Assert.That(WaylandOutputRegistry.TryGetName(50, out var name), Is.True);
        Assert.That(name, Is.EqualTo("HDMI-A-1"));
    }

    [Test]
    public void EmptyNameStoredAndYieldsNullVrrRange()
    {
        WaylandOutputRegistry.OnOutputName(51, "");
        Assert.That(WaylandOutputRegistry.TryGetName(51, out var name), Is.True);
        Assert.That(name, Is.EqualTo(""));
        Assert.That(WaylandOutputRegistry.TryGetVrrRange(51, out var range), Is.True);
        Assert.That(range, Is.Null);
    }

    [Test]
    public void VrrRangeResolvedFromEdidOnNameSet()
    {
        var fake = new FakeEdidSource();
        fake.ByConnector["HDMI-A-1"] = BuildEdidWithRange(0, 48, 60);
        WaylandOutputRegistry.SetEdidSourceForTest(fake);
        WaylandOutputRegistry.OnOutputName(60, "HDMI-A-1");
        Assert.That(WaylandOutputRegistry.TryGetVrrRange(60, out var range), Is.True);
        Assert.That(range!.Value.MinHz, Is.EqualTo(48));
        Assert.That(range.Value.MaxHz, Is.EqualTo(60));
    }

    [Test]
    public void VrrRangeNullWhenEdidUnavailable()
    {
        var fake = new FakeEdidSource();
        WaylandOutputRegistry.SetEdidSourceForTest(fake);
        WaylandOutputRegistry.OnOutputName(70, "HDMI-A-1");
        Assert.That(WaylandOutputRegistry.TryGetVrrRange(70, out var range), Is.True);
        Assert.That(range, Is.Null);
    }

    [Test]
    public void VrrRangeChangedFiresOnFirstResolve()
    {
        var fake = new FakeEdidSource();
        fake.ByConnector["DP-1"] = BuildEdidWithRange(2, 56, 75);
        WaylandOutputRegistry.SetEdidSourceForTest(fake);
        var fires = new List<uint>();
        WaylandOutputRegistry.VrrRangeChanged += n => fires.Add(n);
        WaylandOutputRegistry.OnOutputName(80, "DP-1");
        Assert.That(fires, Is.EqualTo(new[] { 80u }));
    }

    [Test]
    public void VrrRangeChangedDoesNotFireOnNoopRename()
    {
        var fake = new FakeEdidSource();
        fake.ByConnector["DP-1"] = BuildEdidWithRange(0, 48, 60);
        WaylandOutputRegistry.SetEdidSourceForTest(fake);
        WaylandOutputRegistry.OnOutputName(81, "DP-1");
        var fires = new List<uint>();
        WaylandOutputRegistry.VrrRangeChanged += n => fires.Add(n);
        WaylandOutputRegistry.OnOutputName(81, "DP-1");
        Assert.That(fires, Is.Empty);
    }

    [Test]
    public void RemovedOutputClearsNameAndRange()
    {
        var fake = new FakeEdidSource();
        fake.ByConnector["HDMI-A-1"] = BuildEdidWithRange(0, 48, 60);
        WaylandOutputRegistry.SetEdidSourceForTest(fake);
        WaylandOutputRegistry.OnOutputName(90, "HDMI-A-1");
        WaylandOutputRegistry.OnOutputRemoved(90);
        Assert.That(WaylandOutputRegistry.TryGetName(90, out _), Is.False);
        Assert.That(WaylandOutputRegistry.TryGetVrrRange(90, out _), Is.False);
    }

    [Test]
    public void RemovedOutputFiresVrrRangeChanged()
    {
        var fake = new FakeEdidSource();
        fake.ByConnector["HDMI-A-1"] = BuildEdidWithRange(0, 48, 60);
        WaylandOutputRegistry.SetEdidSourceForTest(fake);
        WaylandOutputRegistry.OnOutputName(91, "HDMI-A-1");
        var fires = new List<uint>();
        WaylandOutputRegistry.VrrRangeChanged += n => fires.Add(n);
        WaylandOutputRegistry.OnOutputRemoved(91);
        Assert.That(fires, Is.EqualTo(new[] { 91u }));
    }
}
