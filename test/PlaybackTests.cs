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

    // Regression: libmpv aborts the host process with `free(): invalid pointer` if a `seek` command runs before any file has been loaded. User-visible symptom was the seek slider crashing the player when clicked with no media open. Sleeps bracket the Seek call to give the dispatcher's worker thread time to drain the posted action — without them the test could reach Dispose before the seek even reaches mpv, masking the abort. Reaching the end of this test without the test host being killed is the assertion; reverting the gate in Playback.Seek causes this test to abort the entire test run.
    [Test]
    public void SeekBeforeLoadFileIsSafe()
    {
        using var pb = NewHeadlessInitialized();
        System.Threading.Thread.Sleep(300);
        pb.Seek(0.0);
        System.Threading.Thread.Sleep(300);
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

    // Subtitle-track transitions exercise the same internal-method seam used elsewhere (UpdateSourceHdr / UpdateHwdecCurrent): no real mpv pump, just direct state writes that verify dedup semantics. The dedup matters because mpv's `track-list/count` observation fires for any track-type change (audio, video, sub), so a pure "audio track added" event would otherwise re-allocate and re-notify on the subtitle subset.
    [Test]
    public void SubtitleTracksDefaultsToEmpty()
    {
        using var pb = new Playback.Playback(a => a());
        Assert.That(pb.SubtitleTracks, Is.Empty);
    }

    [Test]
    public void SubtitleTracksUpdatesOnNewSnapshot()
    {
        using var pb = new Playback.Playback(a => a());
        var snapshot = new[] { new SubtitleTrack(1, "English", "eng", false) };
        pb.UpdateSubtitleTracks(snapshot);
        Assert.That(pb.SubtitleTracks, Is.EqualTo(snapshot));
    }

    [Test]
    public void SubtitleTracksDedupsEqualSnapshots()
    {
        using var pb = new Playback.Playback(a => a());
        var fires = new System.Collections.Generic.List<int>();
        pb.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Playback.Playback.SubtitleTracks))
            {
                fires.Add(pb.SubtitleTracks.Count);
            }
        };

        var first = new[] { new SubtitleTrack(1, "English", "eng", false) };
        var sameContent = new[] { new SubtitleTrack(1, "English", "eng", false) };
        var different = new[] { new SubtitleTrack(2, "French", "fre", false) };

        pb.UpdateSubtitleTracks(first);
        pb.UpdateSubtitleTracks(sameContent); // record-equal contents → no PropertyChanged
        pb.UpdateSubtitleTracks(different);

        Assert.That(fires, Is.EqualTo(new[] { 1, 1 }));
    }

    [Test]
    public void SubtitleTracksDedupsBackToEmpty()
    {
        using var pb = new Playback.Playback(a => a());
        var fires = new System.Collections.Generic.List<int>();
        pb.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Playback.Playback.SubtitleTracks))
            {
                fires.Add(pb.SubtitleTracks.Count);
            }
        };

        // Initial state is already empty; pushing another empty snapshot must not fire (defends against the spurious-track-list-count-fire case).
        pb.UpdateSubtitleTracks(Array.Empty<SubtitleTrack>());
        Assert.That(fires, Is.Empty);
    }

    [Test]
    public void CurrentSubtitleIdTracksValue()
    {
        using var pb = new Playback.Playback(a => a());
        var fires = new System.Collections.Generic.List<int?>();
        pb.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Playback.Playback.CurrentSubtitleId))
            {
                fires.Add(pb.CurrentSubtitleId);
            }
        };

        pb.UpdateCurrentSubtitleId(2);
        pb.UpdateCurrentSubtitleId(2); // dedup (ObservableProperty same-value gate)
        pb.UpdateCurrentSubtitleId(null);

        Assert.That(fires, Is.EqualTo(new int?[] { 2, null }));
    }

}
