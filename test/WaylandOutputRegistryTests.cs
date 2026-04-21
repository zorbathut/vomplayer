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
}
