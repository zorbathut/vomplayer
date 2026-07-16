using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Vomplayer.Mpv;

namespace Vomplayer.Tests;

// Integration tests against a real libmpv. vo/ao=null keeps mpv headless so the tests
// don't need a display server or audio output.
[TestFixture]
public class MpvClientObserveTests
{
    private static MpvClient NewHeadless()
    {
        var mpv = new MpvClient();
        mpv.SetOption("vo", "null");
        mpv.SetOption("ao", "null");
        mpv.SetOption("terminal", "no");
        mpv.Initialize();
        return mpv;
    }

    // Drives a wakeup-signalled drain loop until the condition is met or the timeout expires.
    // Exercises the real wakeup-callback path rather than polling.
    private static void PumpUntil(MpvClient mpv, Func<bool> condition, TimeSpan timeout)
    {
        var signal = new ManualResetEventSlim(false);
        mpv.EventAvailable += () => signal.Set();
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            signal.Wait(TimeSpan.FromMilliseconds(50));
            signal.Reset();
            mpv.DrainEvents();
        }
    }

    [Test]
    public void ObservePropertyReturnsDistinctIds()
    {
        using var mpv = NewHeadless();
        var id1 = mpv.ObserveProperty("time-pos", MpvFormat.Double);
        var id2 = mpv.ObserveProperty("duration", MpvFormat.Double);
        var id3 = mpv.ObserveProperty("pause", MpvFormat.Flag);
        Assert.That(id1, Is.Not.EqualTo(id2));
        Assert.That(id2, Is.Not.EqualTo(id3));
        Assert.That(id1, Is.Not.EqualTo(id3));
    }

    [Test]
    public void PropertyChangeCarriesRegistrationId()
    {
        using var mpv = NewHeadless();
        var events = new List<PropertyChange>();
        mpv.PropertyChanged += events.Add;

        var id = mpv.ObserveProperty("pause", MpvFormat.Flag);
        PumpUntil(mpv, () => events.Count > 0, TimeSpan.FromSeconds(1));

        Assert.That(events, Is.Not.Empty);
        Assert.That(events[0].Id, Is.EqualTo(id));
        Assert.That(events[0].Name, Is.EqualTo("pause"));
    }

    [Test]
    public void TwoObserversDemuxById()
    {
        using var mpv = NewHeadless();
        var events = new List<PropertyChange>();
        mpv.PropertyChanged += events.Add;

        var idPause = mpv.ObserveProperty("pause", MpvFormat.Flag);
        var idDuration = mpv.ObserveProperty("duration", MpvFormat.Double);

        PumpUntil(mpv,
            () => events.Any(e => e.Id == idPause) && events.Any(e => e.Id == idDuration),
            TimeSpan.FromSeconds(1));

        Assert.That(events.Where(e => e.Id == idPause).All(e => e.Name == "pause"), Is.True);
        Assert.That(events.Where(e => e.Id == idDuration).All(e => e.Name == "duration"), Is.True);
    }

    [Test]
    public void SamePropertyObservedTwiceDeliversToBothIds()
    {
        using var mpv = NewHeadless();
        var events = new List<PropertyChange>();
        mpv.PropertyChanged += events.Add;

        var id1 = mpv.ObserveProperty("pause", MpvFormat.Flag);
        var id2 = mpv.ObserveProperty("pause", MpvFormat.Flag);
        Assert.That(id1, Is.Not.EqualTo(id2));

        PumpUntil(mpv,
            () => events.Any(e => e.Id == id1) && events.Any(e => e.Id == id2),
            TimeSpan.FromSeconds(1));

        Assert.That(events.Any(e => e.Id == id1), Is.True);
        Assert.That(events.Any(e => e.Id == id2), Is.True);
    }

}
