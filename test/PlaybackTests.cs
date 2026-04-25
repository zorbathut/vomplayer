using System;
using Vomplayer.Playback;

namespace Vomplayer.Tests;

[TestFixture]
public class PlaybackTests
{
    private static Playback.Playback NewHeadlessInitialized()
    {
        // Synchronous post — dispatcher forwards events via this hook, so event handlers run on the test thread.
        var pb = new Playback.Playback(a => a());
        pb.Initialize();
        return pb;
    }

    [Test]
    public void NullPostThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new Playback.Playback(null!));
    }

    [Test]
    public void DisposeWithoutInitializeIsSafe()
    {
        var pb = new Playback.Playback(a => a());
        Assert.DoesNotThrow(() => pb.Dispose());
    }

    [Test]
    public void IsPausedDefaultsTrue()
    {
        using var pb = NewHeadlessInitialized();
        Assert.That(pb.IsPaused, Is.True);
    }

    [Test]
    public void IsCoreIdleDefaultsTrue()
    {
        using var pb = NewHeadlessInitialized();
        Assert.That(pb.IsCoreIdle, Is.True);
    }

    [Test]
    public void LoadFileNullPathThrows()
    {
        using var pb = NewHeadlessInitialized();
        Assert.Throws<ArgumentNullException>(() => pb.LoadFile(null!));
    }

    [TestCase(null, false)]
    [TestCase("", false)]
    [TestCase("bt.1886", false)]
    [TestCase("srgb", false)]
    [TestCase("gamma2.2", false)]
    [TestCase("st428", false)]
    [TestCase("v-log", false)]
    [TestCase("s-log1", false)]
    [TestCase("pq", true)]
    [TestCase("hlg", true)]
    public void IsHdrGammaClassifiesTransferFunctions(string? gamma, bool expected)
    {
        Assert.That(Playback.Playback.IsHdrGamma(gamma), Is.EqualTo(expected));
    }

    // HDR-transition tests drive UpdateSourceHdr directly on an uninitialized Playback. Skipping Initialize() avoids mpv's event-pump thread, which would concurrently dispatch observed properties onto the same object. Exercising UpdateSourceHdr (internal) rather than the private OnMpvPropertyChanged dispatcher keeps the test seam narrow — dispatch routing itself is a one-line case in OnMpvPropertyChanged.
    [Test]
    public void SourceHdrChangedFiresOnSdrToHdrTransition()
    {
        using var pb = new Playback.Playback(a => a());
        var fires = new System.Collections.Generic.List<bool>();
        pb.SourceHdrChanged += v => fires.Add(v);

        pb.UpdateSourceHdr("pq");

        Assert.That(fires, Is.EqualTo(new[] { true }));
    }

    [Test]
    public void SourceHdrChangedDoesNotFireOnSdrRepeats()
    {
        using var pb = new Playback.Playback(a => a());
        var fires = new System.Collections.Generic.List<bool>();
        pb.SourceHdrChanged += v => fires.Add(v);

        pb.UpdateSourceHdr(null);
        pb.UpdateSourceHdr("bt.1886");
        pb.UpdateSourceHdr("srgb");

        Assert.That(fires, Is.Empty);
    }

    [Test]
    public void SourceHdrChangedDoesNotFireOnHdrRepeats()
    {
        using var pb = new Playback.Playback(a => a());
        var fires = new System.Collections.Generic.List<bool>();

        pb.UpdateSourceHdr("pq");
        pb.SourceHdrChanged += v => fires.Add(v);

        // Both pq and hlg are HDR — switching between them is not a state transition.
        pb.UpdateSourceHdr("pq");
        pb.UpdateSourceHdr("hlg");
        pb.UpdateSourceHdr("pq");

        Assert.That(fires, Is.Empty);
    }

    [Test]
    public void SourceHdrChangedFiresOnHdrToSdrTransition()
    {
        using var pb = new Playback.Playback(a => a());
        pb.UpdateSourceHdr("pq");

        var fires = new System.Collections.Generic.List<bool>();
        pb.SourceHdrChanged += v => fires.Add(v);

        pb.UpdateSourceHdr("bt.1886");

        Assert.That(fires, Is.EqualTo(new[] { false }));
    }

    [Test]
    public void HwdecCurrentEmptyAndNullCoalesceToNull()
    {
        using var pb = new Playback.Playback(a => a());
        pb.UpdateHwdecCurrent("");
        Assert.That(pb.HwdecCurrent, Is.Null);
        pb.UpdateHwdecCurrent(null);
        Assert.That(pb.HwdecCurrent, Is.Null);
    }

    [Test]
    public void HwdecCurrentTracksBackendName()
    {
        using var pb = new Playback.Playback(a => a());
        pb.UpdateHwdecCurrent("vaapi");
        Assert.That(pb.HwdecCurrent, Is.EqualTo("vaapi"));
        pb.UpdateHwdecCurrent("no");
        Assert.That(pb.HwdecCurrent, Is.EqualTo("no"));
    }

    [Test]
    public void HwdecCurrentNotifiesOnlyOnTransition()
    {
        using var pb = new Playback.Playback(a => a());
        var fires = new System.Collections.Generic.List<string?>();
        pb.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Playback.Playback.HwdecCurrent))
            {
                fires.Add(pb.HwdecCurrent);
            }
        };

        pb.UpdateHwdecCurrent(null);      // null -> null, no fire
        pb.UpdateHwdecCurrent("vaapi");   // null -> vaapi
        pb.UpdateHwdecCurrent("vaapi");   // dedup
        pb.UpdateHwdecCurrent("no");      // vaapi -> no
        pb.UpdateHwdecCurrent("");        // no -> null (empty coalesces)

        Assert.That(fires, Is.EqualTo(new string?[] { "vaapi", "no", null }));
    }

    [Test]
    public void SourceHdrChangedFiresFullTransitionCycle()
    {
        using var pb = new Playback.Playback(a => a());
        var fires = new System.Collections.Generic.List<bool>();
        pb.SourceHdrChanged += v => fires.Add(v);

        pb.UpdateSourceHdr("bt.1886"); // SDR -> SDR (no fire)
        pb.UpdateSourceHdr("pq");      // SDR -> HDR (true)
        pb.UpdateSourceHdr("hlg");     // HDR -> HDR (no fire)
        pb.UpdateSourceHdr("srgb");    // HDR -> SDR (false)
        pb.UpdateSourceHdr("pq");      // SDR -> HDR (true)

        Assert.That(fires, Is.EqualTo(new[] { true, false, true }));
    }

}
