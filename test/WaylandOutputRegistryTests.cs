using System.Collections.Generic;
using Vomplayer.Wayland;

namespace Vomplayer.Tests;

[TestFixture]
public class WaylandOutputRegistryTests
{
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

    [Test]
    public void UnknownNameHasNoHdrBit()
    {
        Assert.That(WaylandOutputRegistry.TryGetIsHdr(42, out _), Is.False);
    }

    [Test]
    public void HdrBitStored()
    {
        WaylandOutputRegistry.OnOutputHdr(5, true);
        Assert.That(WaylandOutputRegistry.TryGetIsHdr(5, out var isHdr), Is.True);
        Assert.That(isHdr, Is.True);
    }

    [Test]
    public void SdrBitStored()
    {
        WaylandOutputRegistry.OnOutputHdr(6, false);
        Assert.That(WaylandOutputRegistry.TryGetIsHdr(6, out var isHdr), Is.True);
        Assert.That(isHdr, Is.False);
    }

    [Test]
    public void HdrBitUpdateOverwrites()
    {
        WaylandOutputRegistry.OnOutputHdr(8, false);
        WaylandOutputRegistry.OnOutputHdr(8, true);
        Assert.That(WaylandOutputRegistry.TryGetIsHdr(8, out var isHdr), Is.True);
        Assert.That(isHdr, Is.True);
    }

    [Test]
    public void HdrBitClearedOnRemoval()
    {
        WaylandOutputRegistry.OnOutputHdr(11, true);
        WaylandOutputRegistry.OnOutputMode(11, 60000);
        WaylandOutputRegistry.OnOutputRemoved(11);
        Assert.That(WaylandOutputRegistry.TryGetIsHdr(11, out _), Is.False);
        Assert.That(WaylandOutputRegistry.TryGetMode(11, out _), Is.False);
    }

    [Test]
    public void HdrAndModeAreIndependent()
    {
        WaylandOutputRegistry.OnOutputMode(20, 60000);
        Assert.That(WaylandOutputRegistry.TryGetIsHdr(20, out _), Is.False);
        WaylandOutputRegistry.OnOutputHdr(21, true);
        Assert.That(WaylandOutputRegistry.TryGetMode(21, out _), Is.False);
    }

    [Test]
    public void ReAddAfterRemovalStoresNewValueAndFiresEvent()
    {
        // Hot-plug cycle: remove the output, then re-add with a different HDR state. The re-add must both store the new value and fire IsHdrChanged so subscribers see the refresh.
        WaylandOutputRegistry.OnOutputHdr(30, true);
        WaylandOutputRegistry.OnOutputRemoved(30);
        var fires = new List<uint>();
        WaylandOutputRegistry.IsHdrChanged += n => fires.Add(n);
        WaylandOutputRegistry.OnOutputHdr(30, false);
        Assert.That(WaylandOutputRegistry.TryGetIsHdr(30, out var isHdr), Is.True);
        Assert.That(isHdr, Is.False);
        Assert.That(fires, Is.EqualTo(new[] { 30u }));
    }

    [Test]
    public void IsHdrChangedFiresOnFirstSet()
    {
        var fires = new List<uint>();
        WaylandOutputRegistry.IsHdrChanged += n => fires.Add(n);
        WaylandOutputRegistry.OnOutputHdr(40, true);
        Assert.That(fires, Is.EqualTo(new[] { 40u }));
    }

    [Test]
    public void IsHdrChangedFiresOnTransition()
    {
        var fires = new List<uint>();
        WaylandOutputRegistry.IsHdrChanged += n => fires.Add(n);
        WaylandOutputRegistry.OnOutputHdr(41, false);
        WaylandOutputRegistry.OnOutputHdr(41, true);
        Assert.That(fires, Is.EqualTo(new[] { 41u, 41u }));
    }

    [Test]
    public void IsHdrChangedDoesNotFireOnNoopUpdate()
    {
        WaylandOutputRegistry.OnOutputHdr(42, true);
        var fires = new List<uint>();
        WaylandOutputRegistry.IsHdrChanged += n => fires.Add(n);
        WaylandOutputRegistry.OnOutputHdr(42, true);
        Assert.That(fires, Is.Empty);
    }
}
