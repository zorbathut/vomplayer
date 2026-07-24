using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vomplayer.Playback;
using Vomplayer.Services;
using Vomplayer.UserData;
using Vomplayer.ViewModels;
using Vomplayer.Wayland;

namespace Vomplayer.Tests;

[TestFixture]
public class VideoContextTests
{
    private sealed class FakeVrrSink : IVrrSink
    {
        public VrrRange? Range { get; set; }
        public double? RefreshHz { get; set; }
        public VrrRange? CurrentOutputVrrRange { get { return Range; } }
        public double? CurrentOutputRefreshHz { get { return RefreshHz; } }
        public event Action? CurrentOutputVrrRangeChanged;
        public void RaiseChanged() { CurrentOutputVrrRangeChanged?.Invoke(); }
    }

    // Minimal IHdrSink for HDR-policy tests. Records every SetHdr call (in order) so tests can assert pre-stage / enable / disable sequences. CurrentOutputHdrChanged firing simulates compositor-side output transitions; tests use it to drive ApplyHdrPolicy on output-side change.
    private sealed class FakeHdrSink : IHdrSink
    {
        public List<bool> SetHdrCalls { get; } = new();
        // SetHdr return value — 0 = success, -1 = compositor refused. Tests configure this to simulate the failed-sink case.
        public int NextRc { get; set; }
        public OutputImageDescription? CurrentOutputImageDescriptionValue { get; set; }
        public OutputImageDescription? CurrentOutputImageDescription { get { return CurrentOutputImageDescriptionValue; } }
        public event Action? CurrentOutputHdrChanged;

        public int SetHdr(bool enable)
        {
            SetHdrCalls.Add(enable);
            return NextRc;
        }

        public void RaiseOutputHdrChanged() { CurrentOutputHdrChanged?.Invoke(); }
    }

    private static VideoContext NewContext(out FakePlayback playback)
    {
        playback = new FakePlayback();
        return new VideoContext(playback, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader { Available = false }, new FakeUrlPrompt());
    }

    [Test]
    public void HasNextItemReflectsPlaylistPosition()
    {
        using var ctx = NewContext(out _);
        Assert.That(ctx.HasNextItem, Is.False, "empty playlist");

        ctx.LoadPaths(new[] { "/a.mp4" }, replace: true);
        Assert.That(ctx.HasNextItem, Is.False, "single-item playlist at index 0");

        ctx.LoadPaths(new[] { "/a.mp4", "/b.mp4", "/c.mp4" }, replace: true);
        Assert.That(ctx.HasNextItem, Is.True, "three-item at index 0");

        ctx.PlayPlaylistItem(2);
        Assert.That(ctx.HasNextItem, Is.False, "three-item at last index");
    }

    [Test]
    public void AdvanceAndLoadIfPossibleReturnsFalseAtEnd()
    {
        using var ctx = NewContext(out var pb);
        ctx.LoadPaths(new[] { "/a.mp4" }, replace: true);
        // Single item — at index 0, no next.
        Assert.That(ctx.AdvanceAndLoadIfPossible(), Is.False);
        // No additional LoadFile beyond the initial replace.
        Assert.That(pb.LoadedFiles, Is.EqualTo(new[] { "/a.mp4" }));
    }

    [Test]
    public void MediaTitleMirrorsPlayback()
    {
        using var ctx = NewContext(out var pb);
        var fires = new List<string?>();
        ctx.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(VideoContext.MediaTitle))
            {
                fires.Add(ctx.MediaTitle);
            }
        };
        Assert.That(ctx.MediaTitle, Is.Null, "no source loaded ⇒ MediaTitle is null");

        pb.MediaTitle = "Big Buck Bunny";
        Assert.That(ctx.MediaTitle, Is.EqualTo("Big Buck Bunny"));

        pb.MediaTitle = "Sintel";
        Assert.That(ctx.MediaTitle, Is.EqualTo("Sintel"));

        pb.MediaTitle = null;
        Assert.That(ctx.MediaTitle, Is.Null, "unload clears the title");

        Assert.That(fires, Is.EqualTo(new string?[] { "Big Buck Bunny", "Sintel", null }));
    }

    [Test]
    public void VideoAspectMirrorsPlayback()
    {
        using var ctx = NewContext(out var pb);
        var fires = new List<double?>();
        ctx.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(VideoContext.VideoAspect))
            {
                fires.Add(ctx.VideoAspect);
            }
        };
        Assert.That(ctx.VideoAspect, Is.Null, "no source loaded ⇒ aspect is null");

        pb.VideoAspect = 16.0 / 9.0;
        Assert.That(ctx.VideoAspect, Is.EqualTo(16.0 / 9.0).Within(1e-9));

        pb.VideoAspect = 4.0 / 3.0;
        Assert.That(ctx.VideoAspect, Is.EqualTo(4.0 / 3.0).Within(1e-9));

        pb.VideoAspect = null;
        Assert.That(ctx.VideoAspect, Is.Null, "unload clears the aspect");

        Assert.That(fires, Is.EqualTo(new double?[] { 16.0 / 9.0, 4.0 / 3.0, null }));
    }

    [Test]
    public void AdvanceAndLoadIfPossibleAdvancesAndLoadsNext()
    {
        using var ctx = NewContext(out var pb);
        ctx.LoadPaths(new[] { "/a.mp4", "/b.mp4" }, replace: true);
        Assert.That(ctx.AdvanceAndLoadIfPossible(), Is.True);
        Assert.That(ctx.Playlist.CurrentIndex, Is.EqualTo(1));
        Assert.That(pb.LoadedFiles, Is.EqualTo(new[] { "/a.mp4", "/b.mp4" }));
    }

    [Test]
    public void AutoAdvanceDisabledSuppressesEofRisingEdgeAdvance()
    {
        using var ctx = NewContext(out var pb);
        ctx.AutoAdvanceEnabled = false;
        ctx.LoadPaths(new[] { "/a.mp4", "/b.mp4" }, replace: true);
        // FileLoaded primes currentFileLoaded; duration arrives; then EOF rises. With AutoAdvanceEnabled=false the per-context handler must NOT call AdvanceAndLoadIfPossible.
        pb.RaiseFileLoaded();
        pb.DurationSeconds = 60;
        pb.IsEofReached = true;
        Assert.That(ctx.Playlist.CurrentIndex, Is.EqualTo(0), "current must not advance when auto-advance is off");
        Assert.That(pb.LoadedFiles, Is.EqualTo(new[] { "/a.mp4" }), "no second LoadFile");
        // Coordinator-driven path still works manually.
        Assert.That(ctx.IsAtPlayableEof, Is.True, "IsAtPlayableEof signals to the coordinator that this context is ready");
    }

    [Test]
    public void AutoAdvanceEnabledFiresOnEofRisingEdgeAsBefore()
    {
        using var ctx = NewContext(out var pb);
        // Default: AutoAdvanceEnabled = true.
        ctx.LoadPaths(new[] { "/a.mp4", "/b.mp4" }, replace: true);
        pb.RaiseFileLoaded();
        pb.DurationSeconds = 60;
        pb.IsEofReached = true;
        Assert.That(ctx.Playlist.CurrentIndex, Is.EqualTo(1), "single-context EOF advances by default");
        Assert.That(pb.LoadedFiles, Is.EqualTo(new[] { "/a.mp4", "/b.mp4" }));
    }

    [Test]
    public void AutoAdvanceFiresOnEveryConsecutiveEof()
    {
        // Regression: re-entrant PropertyChanged from Playback.LoadFile's synchronous IsEofReached=false during AdvanceAndLoadIfPossible was leaving wasEofReached stamped true after the outer handler returned (stale local stomp). The next file's EOF then looked like a non-rising edge and the playlist halted. Three files, two consecutive natural EOFs — both must advance.
        using var ctx = NewContext(out var pb);
        ctx.LoadPaths(new[] { "/a.mp4", "/b.mp4", "/c.mp4" }, replace: true);

        pb.RaiseFileLoaded();
        pb.DurationSeconds = 60;
        pb.IsEofReached = true;
        Assert.That(ctx.Playlist.CurrentIndex, Is.EqualTo(1), "first EOF advanced /a → /b");
        Assert.That(pb.LoadedFiles, Is.EqualTo(new[] { "/a.mp4", "/b.mp4" }));

        // Faithful fake LoadFile cleared IsEofReached synchronously; now simulate the new file's lifecycle: FileLoaded, then a fresh false→true EOF transition.
        pb.RaiseFileLoaded();
        pb.IsEofReached = true;
        Assert.That(ctx.Playlist.CurrentIndex, Is.EqualTo(2), "second EOF advanced /b → /c");
        Assert.That(pb.LoadedFiles, Is.EqualTo(new[] { "/a.mp4", "/b.mp4", "/c.mp4" }));
    }

    [Test]
    public void IsAtPlayableEofTracksThreeGates()
    {
        using var ctx = NewContext(out var pb);
        var transitions = new List<bool>();
        ctx.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(VideoContext.IsAtPlayableEof))
            {
                transitions.Add(ctx.IsAtPlayableEof);
            }
        };

        // No file loaded: IsAtPlayableEof must be false even with EOF=true (mirrors the currentFileLoaded gate).
        pb.IsEofReached = true;
        Assert.That(ctx.IsAtPlayableEof, Is.False);

        // Reset for the load-and-advance flow.
        pb.IsEofReached = false;
        ctx.LoadPaths(new[] { "/a.mp4" }, replace: true);
        pb.RaiseFileLoaded();
        // Duration still 0 — IsAtPlayableEof gates on > 0.
        pb.IsEofReached = true;
        Assert.That(ctx.IsAtPlayableEof, Is.False, "duration=0 must keep IsAtPlayableEof false");

        // Duration arrives — now all three gates met (currentFileLoaded, IsEofReached, Duration > 0). Should rise.
        pb.DurationSeconds = 30;
        Assert.That(ctx.IsAtPlayableEof, Is.True, "all three gates met → true");

        // Falling edge: EOF clears.
        pb.IsEofReached = false;
        Assert.That(ctx.IsAtPlayableEof, Is.False);

        // PropertyChanged fired exactly twice (false→true, true→false).
        Assert.That(transitions, Is.EqualTo(new[] { true, false }));
    }

    [Test]
    public void TwoContextsAreIndependent()
    {
        using var a = NewContext(out var pbA);
        using var b = NewContext(out var pbB);
        a.AutoAdvanceEnabled = false;
        b.AutoAdvanceEnabled = false;

        a.LoadPaths(new[] { "/a1.mp4", "/a2.mp4" }, replace: true);
        b.LoadPaths(new[] { "/b1.mp4", "/b2.mp4", "/b3.mp4" }, replace: true);

        Assert.That(a.Playlist.Items, Is.EqualTo(new[] { "/a1.mp4", "/a2.mp4" }));
        Assert.That(b.Playlist.Items, Is.EqualTo(new[] { "/b1.mp4", "/b2.mp4", "/b3.mp4" }));
        Assert.That(a.Playlist.CurrentIndex, Is.EqualTo(0));
        Assert.That(b.Playlist.CurrentIndex, Is.EqualTo(0));

        // Coordinator-style advance: both IndependentLockstep dictates b advances independently when called.
        b.AdvanceAndLoadIfPossible();
        Assert.That(a.Playlist.CurrentIndex, Is.EqualTo(0), "a unaffected");
        Assert.That(b.Playlist.CurrentIndex, Is.EqualTo(1), "b advanced");

        // Property updates on pbA do not propagate to context b (each has its own playback subscription).
        pbA.DurationSeconds = 100;
        pbA.PositionSeconds = 50;
        Assert.That(a.Duration, Is.EqualTo(TimeSpan.FromSeconds(100)));
        Assert.That(b.Duration, Is.EqualTo(TimeSpan.Zero), "b's duration is independent");
    }

    [Test]
    public void SourceHdrTrueWithSuccessfulSinkEnablesHdr()
    {
        using var ctx = NewContext(out var pb);
        var sink = new FakeHdrSink { NextRc = 0 };
        ctx.AttachHdrSink(sink);
        sink.SetHdrCalls.Clear();
        pb.RaiseSourceHdrChanged(true);
        Assert.That(sink.SetHdrCalls, Is.EqualTo(new[] { true }), "SetHdr(true) must run");
        Assert.That(pb.EnableHdrOutputCalls, Is.EqualTo(1), "mpv advanced to PQ targets");
        Assert.That(ctx.ActiveHdrState, Is.EqualTo(VideoContext.HdrActiveState.Hdr));
    }

    [Test]
    public void SourceHdrTrueWithFailingSinkStaysSdrAndDoesNotEnableMpvHdr()
    {
        using var ctx = NewContext(out var pb);
        var sink = new FakeHdrSink { NextRc = -1 };
        ctx.AttachHdrSink(sink);
        sink.SetHdrCalls.Clear();
        pb.RaiseSourceHdrChanged(true);
        Assert.That(sink.SetHdrCalls, Is.EqualTo(new[] { true }), "SetHdr(true) attempted");
        Assert.That(pb.EnableHdrOutputCalls, Is.EqualTo(0), "mpv stays SDR when shim refused — no PQ-encoded pixels into untagged surface");
        Assert.That(ctx.ActiveHdrState, Is.EqualTo(VideoContext.HdrActiveState.Failed));
    }

    [Test]
    public void SourceHdrFalseAttachesSdrAndDisablesMpvHdr()
    {
        using var ctx = NewContext(out var pb);
        var sink = new FakeHdrSink { NextRc = 0 };
        ctx.AttachHdrSink(sink);
        // Drive HDR first to give DisableHdrOutput somewhere to step down from.
        pb.RaiseSourceHdrChanged(true);
        sink.SetHdrCalls.Clear();
        pb.RaiseSourceHdrChanged(false);
        Assert.That(sink.SetHdrCalls, Is.EqualTo(new[] { false }));
        Assert.That(pb.DisableHdrOutputCalls, Is.GreaterThanOrEqualTo(1));
        Assert.That(ctx.ActiveHdrState, Is.EqualTo(VideoContext.HdrActiveState.Sdr));
    }

    [Test]
    public void DetachHdrSinkUnsubscribesOutputChange()
    {
        using var ctx = NewContext(out var pb);
        var sink = new FakeHdrSink { NextRc = 0 };
        ctx.AttachHdrSink(sink);
        // Source is HDR; sink attaches → ApplyHdrPolicy ran during AttachHdrSink and lastSourceHdr is false (no SourceHdrChanged fired yet).
        pb.RaiseSourceHdrChanged(true);
        sink.SetHdrCalls.Clear();

        ctx.DetachHdrSink();
        // Output transition AFTER detach: ApplyHdrPolicy must NOT run (sink was the source of subscription).
        sink.RaiseOutputHdrChanged();
        Assert.That(sink.SetHdrCalls, Is.Empty);
    }

    [Test]
    public void IsAtPlayableEofResetsOnLoadCurrentItem()
    {
        // Scenario: at EOF, then user clicks a different playlist item — IsAtPlayableEof must drop to false synchronously when the new load starts (currentFileLoaded resets to false), so the coordinator doesn't see a stale "at EOF" signal across the load boundary.
        using var ctx = NewContext(out var pb);
        ctx.LoadPaths(new[] { "/a.mp4", "/b.mp4" }, replace: true);
        pb.RaiseFileLoaded();
        pb.DurationSeconds = 60;
        pb.IsEofReached = true;
        // Auto-advance fires by default; observe the post-advance state.
        Assert.That(ctx.Playlist.CurrentIndex, Is.EqualTo(1));
        Assert.That(ctx.IsAtPlayableEof, Is.False, "post-advance LoadCurrentItem reset currentFileLoaded → IsAtPlayableEof drops to false");
    }

    [Test]
    public void AttachVrrSinkAppliesPolicyImmediately()
    {
        using var ctx = NewContext(out var pb);
        pb.VideoFps = 25.0;
        var sink = new FakeVrrSink { Range = new VrrRange(48, 60) };
        ctx.AttachVrrSink(sink);
        // 25 fps × 2 = 50 Hz, in window.
        Assert.That(pb.SetFrameMultiplierCalls, Is.EqualTo(new[] { 50.0 }));
        Assert.That(ctx.LastVrrDecision.Multiplier, Is.EqualTo(2));
    }

    [Test]
    public void VideoFpsChangeReappliesPolicy()
    {
        using var ctx = NewContext(out var pb);
        var sink = new FakeVrrSink { Range = new VrrRange(48, 60) };
        ctx.AttachVrrSink(sink);
        pb.SetFrameMultiplierCalls.Clear();
        pb.ClearFrameMultiplierCalls.Equals(0);
        // VideoFps lands → policy should re-run, applying the multiplier.
        pb.VideoFps = 25.0;
        Assert.That(pb.SetFrameMultiplierCalls, Is.EqualTo(new[] { 50.0 }));
    }

    [Test]
    public void OutputVrrRangeChangeReappliesPolicy()
    {
        using var ctx = NewContext(out var pb);
        pb.VideoFps = 25.0;
        var sink = new FakeVrrSink { Range = new VrrRange(48, 60) };
        ctx.AttachVrrSink(sink);
        pb.SetFrameMultiplierCalls.Clear();
        // Move to a 100-144 panel: N=4 → 100Hz becomes the smallest fitting multiplier. The previous-window policy chose N=2 (50Hz). Asserting 100.0 here proves ApplyVrrPolicy actually re-ran with the new range and re-chose the multiplier.
        sink.Range = new VrrRange(100, 144);
        sink.RaiseChanged();
        Assert.That(pb.SetFrameMultiplierCalls, Is.EqualTo(new[] { 100.0 }));
    }

    [Test]
    public void OutOfWindowSourceClearsMultiplier()
    {
        using var ctx = NewContext(out var pb);
        pb.VideoFps = 50.0; // already in 48-60 window
        var sink = new FakeVrrSink { Range = new VrrRange(48, 60) };
        ctx.AttachVrrSink(sink);
        Assert.That(pb.SetFrameMultiplierCalls, Is.Empty);
        Assert.That(pb.ClearFrameMultiplierCalls, Is.GreaterThan(0));
    }

    [Test]
    public void TrustFlipToUntrustedClearsMultiplier()
    {
        using var ctx = NewContext(out var pb);
        pb.VideoFps = 25.0;
        var sink = new FakeVrrSink { Range = new VrrRange(48, 60) };
        ctx.AttachVrrSink(sink);
        Assert.That(pb.SetFrameMultiplierCalls, Is.EqualTo(new[] { 50.0 }));
        // Trust flips → policy must re-run, clearing.
        pb.RaiseIsSourceFpsTrustedChanged(false);
        Assert.That(pb.ClearFrameMultiplierCalls, Is.GreaterThan(0));
        Assert.That(ctx.LastVrrDecision.Multiplier, Is.EqualTo(1));
    }

    [Test]
    public void DetachVrrSinkUnsubscribes()
    {
        using var ctx = NewContext(out var pb);
        pb.VideoFps = 25.0;
        var sink = new FakeVrrSink { Range = new VrrRange(48, 60) };
        ctx.AttachVrrSink(sink);
        ctx.DetachVrrSink();
        pb.SetFrameMultiplierCalls.Clear();
        // Range change AFTER detach: must not trigger ApplyVrrPolicy.
        sink.Range = new VrrRange(40, 144);
        sink.RaiseChanged();
        Assert.That(pb.SetFrameMultiplierCalls, Is.Empty);
    }

    [Test]
    public void NoSinkAttachedSkipsPolicy()
    {
        using var ctx = NewContext(out var pb);
        // Without a sink, source-FPS landing or trust transitions must not result in any multiplier mpv writes — the GLArea-fallback path should never get a vf=fps filter.
        pb.VideoFps = 25.0;
        pb.RaiseIsSourceFpsTrustedChanged(false);
        Assert.That(pb.SetFrameMultiplierCalls, Is.Empty);
        Assert.That(pb.ClearFrameMultiplierCalls, Is.EqualTo(0));
    }

    [Test]
    public void FpsTransition24To50ClearsMultiplier()
    {
        // Regression: load 24fps file (×2 → 48fps multiplier applied), then load 50fps file (already in 48-60 window, no multiplier needed). The transition must call ClearFrameMultiplier on the new file so the previous file's filter is removed.
        using var ctx = NewContext(out var pb);
        var sink = new FakeVrrSink { Range = new VrrRange(48, 60) };
        ctx.AttachVrrSink(sink);
        // File A: 24fps source → multiplier=2.
        pb.VideoFps = 24.0;
        Assert.That(pb.SetFrameMultiplierCalls.Count, Is.EqualTo(1));
        pb.SetFrameMultiplierCalls.Clear();
        pb.ClearFrameMultiplierCalls.Equals(0); // reference baseline
        int clearCallsBeforeFileB = pb.ClearFrameMultiplierCalls;
        // File B: 50fps source — already in window, multiplier=1.
        pb.VideoFps = 50.0;
        Assert.That(pb.SetFrameMultiplierCalls, Is.Empty, "no Set call when source is already in/above the VRR range");
        Assert.That(pb.ClearFrameMultiplierCalls, Is.GreaterThan(clearCallsBeforeFileB), "Clear must be called when transitioning from a multiplied source to one already in window");
        Assert.That(ctx.LastVrrDecision.Multiplier, Is.EqualTo(1));
    }

    [Test]
    public void NextTrackStepsForwardInMiddleOfPlaylist()
    {
        using var ctx = NewContext(out var pb);
        // Fake paths: the middle-of-playlist branch never touches the filesystem.
        ctx.LoadPaths(new[] { "/a.mp4", "/b.mp4", "/c.mp4" }, replace: true);
        Assert.That(pb.LoadedFiles, Is.EqualTo(new[] { "/a.mp4" }));

        ctx.NextTrack();
        Assert.That(ctx.Playlist.CurrentIndex, Is.EqualTo(1));
        Assert.That(pb.LoadedFiles, Is.EqualTo(new[] { "/a.mp4", "/b.mp4" }));
    }

    [Test]
    public void PreviousTrackStepsBackInMiddleOfPlaylist()
    {
        using var ctx = NewContext(out var pb);
        ctx.LoadPaths(new[] { "/a.mp4", "/b.mp4", "/c.mp4" }, replace: true);
        ctx.PlayPlaylistItem(2);
        Assert.That(pb.LoadedFiles, Is.EqualTo(new[] { "/a.mp4", "/c.mp4" }));

        ctx.PreviousTrack();
        Assert.That(ctx.Playlist.CurrentIndex, Is.EqualTo(1));
        Assert.That(pb.LoadedFiles, Is.EqualTo(new[] { "/a.mp4", "/c.mp4", "/b.mp4" }));
    }

    [Test]
    public void NextTrackAtEndAppendsDirectoryNeighbor()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vompl-nav-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.mp4"), "");
            File.WriteAllText(Path.Combine(dir, "b.mp4"), "");
            File.WriteAllText(Path.Combine(dir, "c.mp4"), "");
            string b = Path.Combine(dir, "b.mp4");
            string c = Path.Combine(dir, "c.mp4");

            using var ctx = NewContext(out var pb);
            ctx.LoadPaths(new[] { b }, replace: true);

            ctx.NextTrack();
            Assert.That(ctx.Playlist.Items, Is.EqualTo(new[] { b, c }));
            Assert.That(ctx.Playlist.CurrentIndex, Is.EqualTo(1));
            Assert.That(pb.LoadedFiles[^1], Is.EqualTo(c));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void PreviousTrackAtStartPrependsDirectoryNeighbor()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vompl-nav-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.mp4"), "");
            File.WriteAllText(Path.Combine(dir, "b.mp4"), "");
            File.WriteAllText(Path.Combine(dir, "c.mp4"), "");
            string a = Path.Combine(dir, "a.mp4");
            string b = Path.Combine(dir, "b.mp4");

            using var ctx = NewContext(out var pb);
            ctx.LoadPaths(new[] { b }, replace: true);

            ctx.PreviousTrack();
            Assert.That(ctx.Playlist.Items, Is.EqualTo(new[] { a, b }));
            Assert.That(ctx.Playlist.CurrentIndex, Is.EqualTo(0));
            Assert.That(pb.LoadedFiles[^1], Is.EqualTo(a));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void NextAndPreviousNoOpAtDirectoryEdges()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vompl-nav-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.mp4"), "");
            File.WriteAllText(Path.Combine(dir, "b.mp4"), "");
            string a = Path.Combine(dir, "a.mp4");
            string b = Path.Combine(dir, "b.mp4");

            using var ctx = NewContext(out var pb);
            // Whole directory in the playlist, current at the last item.
            ctx.LoadPaths(new[] { a, b }, replace: true);
            ctx.PlayPlaylistItem(1);
            int loadsBefore = pb.LoadedFiles.Count;

            // At last item AND last dir file: no wrap-around.
            ctx.NextTrack();
            Assert.That(ctx.Playlist.Items, Is.EqualTo(new[] { a, b }), "playlist unchanged");
            Assert.That(pb.LoadedFiles.Count, Is.EqualTo(loadsBefore), "no load");

            // Back to first item, then Previous at the first dir file: no wrap-around.
            ctx.PlayPlaylistItem(0);
            int loadsBeforePrev = pb.LoadedFiles.Count;
            ctx.PreviousTrack();
            Assert.That(ctx.Playlist.Items, Is.EqualTo(new[] { a, b }), "playlist unchanged");
            Assert.That(pb.LoadedFiles.Count, Is.EqualTo(loadsBeforePrev), "no load");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void NextTrackWalksRepeatedlyPastTheEnd()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vompl-nav-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "b.mp4"), "");
            File.WriteAllText(Path.Combine(dir, "c.mp4"), "");
            File.WriteAllText(Path.Combine(dir, "d.mp4"), "");
            string b = Path.Combine(dir, "b.mp4");
            string c = Path.Combine(dir, "c.mp4");
            string d = Path.Combine(dir, "d.mp4");

            using var ctx = NewContext(out var pb);
            ctx.LoadPaths(new[] { b }, replace: true);

            // Two presses past the end: each walk anchors on the new current item, so it walks b → c → d rather than re-finding c's neighbor.
            ctx.NextTrack();
            ctx.NextTrack();
            Assert.That(ctx.Playlist.Items, Is.EqualTo(new[] { b, c, d }));
            Assert.That(ctx.Playlist.CurrentIndex, Is.EqualTo(2));
            Assert.That(pb.LoadedFiles[^1], Is.EqualTo(d));

            // d is the last dir file — third press stops.
            int loadsBefore = pb.LoadedFiles.Count;
            ctx.NextTrack();
            Assert.That(ctx.Playlist.Items, Is.EqualTo(new[] { b, c, d }));
            Assert.That(pb.LoadedFiles.Count, Is.EqualTo(loadsBefore));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void NextTrackDoesNotDuplicateAnAlreadyQueuedNeighbor()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vompl-nav-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.mp4"), "");
            File.WriteAllText(Path.Combine(dir, "b.mp4"), "");
            string a = Path.Combine(dir, "a.mp4");
            string b = Path.Combine(dir, "b.mp4");

            using var ctx = NewContext(out var pb);
            // Disordered playlist (b before a) with current on the last item a. The directory neighbor after a is b — already queued at index 0 — so the walk must NOT re-append b.
            ctx.LoadPaths(new[] { b, a }, replace: true);
            ctx.PlayPlaylistItem(1);
            int loadsBefore = pb.LoadedFiles.Count;

            ctx.NextTrack();
            Assert.That(ctx.Playlist.Items, Is.EqualTo(new[] { b, a }), "no duplicate row appended");
            Assert.That(pb.LoadedFiles.Count, Is.EqualTo(loadsBefore), "no load");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void NextTrackOnNonLocalCurrentItemNoOps()
    {
        using var ctx = NewContext(out var pb);
        // Single-item URL playlist: no scannable directory → no neighbor → no-op (no wrap, no crash).
        ctx.LoadPaths(new[] { "https://example.com/stream.m3u8" }, replace: true);
        int loadsBefore = pb.LoadedFiles.Count;
        ctx.NextTrack();
        ctx.PreviousTrack();
        Assert.That(pb.LoadedFiles.Count, Is.EqualTo(loadsBefore));
        Assert.That(ctx.Playlist.Items, Is.EqualTo(new[] { "https://example.com/stream.m3u8" }));
    }

    // --- Per-directory track preferences: save-on-explicit-choice + apply-on-load ---

    private static string LocalPathInTemp(string filename)
    {
        // Build a path that survives TryGetDirectoryKey (i.e., a real local-filesystem path with a real parent dir).
        var dir = Path.Combine(Path.GetTempPath(), "vompl-ctx-prefs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, filename);
    }

    [Test]
    public async Task OpenAsyncFromPickerEnablesPreferencePersistence()
    {
        // Regression: the picker's OpenAsync used to bypass OpenFile and load the path directly, leaving currentDirectoryKey unset. As a result, every subsequent SelectXxx hit the SaveTrackPreference early-return (null directory key) and nothing was saved. Drag-and-drop and command-line invocation went through OpenFile and worked fine, but the menu's File → Open… item silently failed to persist anything.
        var pb = new FakePlayback();
        var prefs = new FakeTrackPreferences();
        var path = LocalPathInTemp("movie.mkv");
        var picker = new FakeFilePicker { NextResult = path };
        var ctx = new VideoContext(pb, picker, new FakeRecentFiles(), prefs, new FakeUrlDownloader(), new FakeUrlPrompt());
        try
        {
            await ctx.OpenAsync();
            // Set tracks now that the VM has subscribed to the playback mirror.
            pb.AudioTracks = new[] { new MediaTrack(7, "English", "eng", false, null) };

            ctx.SelectAudio(7);

            Assert.That(prefs.RecordedPrefs, Has.Count.EqualTo(1));
            Assert.That(prefs.RecordedPrefs[0].Dir, Is.EqualTo(Path.GetDirectoryName(path)));
            Assert.That(prefs.RecordedPrefs[0].Pref.Title, Is.EqualTo("English"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void SelectAudioRecordsPreferenceForLocalFile()
    {
        var pb = new FakePlayback();
        var prefs = new FakeTrackPreferences();
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs, new FakeUrlDownloader(), new FakeUrlPrompt());
        // Set tracks AFTER VM construction so the PropertyChanged → mirror flow runs and ctx.AudioTracks reflects the test's setup.
        pb.AudioTracks = new[]
        {
            new MediaTrack(1, "First", "eng", false, null),
            new MediaTrack(7, "Commentary", "eng", true, "commentary.ac3"),
        };
        var path = LocalPathInTemp("movie.mkv");
        var dir = Path.GetDirectoryName(path);
        try
        {
            ctx.OpenFile(path);
            ctx.SelectAudio(7);

            Assert.That(prefs.RecordedPrefs, Has.Count.EqualTo(1));
            var (recordedDir, kind, pref) = prefs.RecordedPrefs[0];
            Assert.That(recordedDir, Is.EqualTo(dir));
            Assert.That(kind, Is.EqualTo(MediaKind.Audio));
            Assert.That(pref.IsNone, Is.False);
            Assert.That(pref.Title, Is.EqualTo("Commentary"));
            Assert.That(pref.Lang, Is.EqualTo("eng"));
            Assert.That(pref.External, Is.True);
            Assert.That(pref.ExternalFilename, Is.EqualTo("commentary.ac3"));
            Assert.That(pref.IndexInKind, Is.EqualTo(1));

            // The forwarding to playback still happens.
            Assert.That(pb.AudioSelections, Is.EqualTo(new int?[] { 7 }));
        }
        finally
        {
            Directory.Delete(dir!, recursive: true);
        }
    }

    [Test]
    public void SelectSubtitleNullRecordsIsNonePreference()
    {
        var pb = new FakePlayback();
        var prefs = new FakeTrackPreferences();
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs, new FakeUrlDownloader(), new FakeUrlPrompt());
        pb.SubtitleTracks = new[] { new MediaTrack(1, "English", "eng", false, null) };
        var path = LocalPathInTemp("show.mkv");
        try
        {
            ctx.OpenFile(path);
            ctx.SelectSubtitle(null);

            Assert.That(prefs.RecordedPrefs, Has.Count.EqualTo(1));
            Assert.That(prefs.RecordedPrefs[0].Pref.IsNone, Is.True);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void SelectIsNoopForRecordingWhenSourceIsUri()
    {
        // URI sources have no useful directory key; preferences must not be saved (and the playback call must still happen so the user's pick takes effect for the current session).
        var pb = new FakePlayback();
        var prefs = new FakeTrackPreferences();
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs, new FakeUrlDownloader(), new FakeUrlPrompt());
        pb.AudioTracks = new[] { new MediaTrack(1, "Foo", "eng", false, null) };
        ctx.OpenFile("https://example.com/stream.m3u8");

        ctx.SelectAudio(1);

        Assert.That(prefs.RecordedPrefs, Is.Empty);
        Assert.That(pb.AudioSelections, Is.EqualTo(new int?[] { 1 }));
    }

    [Test]
    public void SelectIsNoopForRecordingWhenChosenIdNotInTracklist()
    {
        var pb = new FakePlayback();
        var prefs = new FakeTrackPreferences();
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs, new FakeUrlDownloader(), new FakeUrlPrompt());
        pb.AudioTracks = new[] { new MediaTrack(1, "Foo", "eng", false, null) };
        var path = LocalPathInTemp("movie.mkv");
        try
        {
            ctx.OpenFile(path);
            // Race: user clicks track 99 but the list has changed since the menu was rendered. Skip the save.
            ctx.SelectAudio(99);

            Assert.That(prefs.RecordedPrefs, Is.Empty);
            // playback still receives the call — let mpv decide what to do with an unknown id.
            Assert.That(pb.AudioSelections, Is.EqualTo(new int?[] { 99 }));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void ApplyHappensOnFileLoaded()
    {
        // mpv fires `track-list/count` (and we marshal it as TracksReloaded) BEFORE FileLoaded — the lists are populated by the time FileLoaded lands. So apply fires from FileLoaded itself, not from a subsequent TracksReloaded. Verified empirically against real mpv via VOMPL_LOG_PREFS traces.
        var pb = new FakePlayback();
        var prefs = new FakeTrackPreferences();
        var path = LocalPathInTemp("movie.mkv");
        var dir = Path.GetDirectoryName(path)!;
        prefs.Stored[(dir, MediaKind.Audio)] = new TrackPreference(false, null, "fre", false, null, null);
        prefs.Stored[(dir, MediaKind.Subtitle)] = new TrackPreference(true, null, null, false, null, null);

        try
        {
            var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs, new FakeUrlDownloader(), new FakeUrlPrompt());
            // Tracks set after VM construction so the PropertyChanged → mirror path runs and ApplyTrackPreferences sees them via ctx.AudioTracks etc. In real mpv, the TracksReloaded fire that produces the populated lists happens before FileLoaded — so by the time FileLoaded fires, mirrors are populated.
            pb.AudioTracks = new[] { new MediaTrack(11, "English", "eng", false, null), new MediaTrack(12, "French", "fre", false, null) };
            pb.SubtitleTracks = new[] { new MediaTrack(21, "English", "eng", false, null) };
            ctx.OpenFile(path);

            // Before FileLoaded fires, no apply.
            Assert.That(pb.AudioSelections, Is.Empty);

            pb.RaiseFileLoaded();
            // Apply runs immediately: French audio (lang match) → 12; subtitles → null (IsNone).
            Assert.That(pb.AudioSelections, Is.EqualTo(new int?[] { 12 }));
            Assert.That(pb.SubtitleSelections, Is.EqualTo(new int?[] { null }));

            // A subsequent TracksReloaded must NOT re-apply (e.g., the user adds a sub via Add Subtitle File later in the session). The per-kind applied gate enforces this.
            pb.RaiseTracksReloaded();
            Assert.That(pb.AudioSelections, Has.Count.EqualTo(1));
            Assert.That(pb.SubtitleSelections, Has.Count.EqualTo(1));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void ApplyDoesNotReSavePreference()
    {
        // The apply path goes through playback.SetXxx directly, bypassing ctx.SelectXxx — otherwise an immediate read-back-and-save loop would constantly rewrite the same row on every file load. Verify by counting Record calls before and after the apply.
        var pb = new FakePlayback();
        var prefs = new FakeTrackPreferences();
        var path = LocalPathInTemp("movie.mkv");
        var dir = Path.GetDirectoryName(path)!;
        prefs.Stored[(dir, MediaKind.Audio)] = new TrackPreference(false, null, "eng", false, null, null);
        try
        {
            var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs, new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.AudioTracks = new[] { new MediaTrack(1, null, "eng", false, null) };
            ctx.OpenFile(path);
            pb.RaiseFileLoaded();
            pb.RaiseTracksReloaded();

            // playback got its SetAudio call for the matched track…
            Assert.That(pb.AudioSelections, Is.EqualTo(new int?[] { 1 }));
            // …but Record was NOT called from the apply path.
            Assert.That(prefs.RecordedPrefs, Is.Empty);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void ApplySkipsWhenNoPreferenceStored()
    {
        var pb = new FakePlayback();
        var prefs = new FakeTrackPreferences();
        var path = LocalPathInTemp("movie.mkv");
        try
        {
            var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs, new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.AudioTracks = new[] { new MediaTrack(1, null, "eng", false, null) };
            ctx.OpenFile(path);
            pb.RaiseFileLoaded();
            pb.RaiseTracksReloaded();
            Assert.That(pb.AudioSelections, Is.Empty);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void ApplyRetriesPerKindUntilEachSucceeds()
    {
        // Realistic scenario: subtitle preference is for an external sub mpv hasn't auto-loaded yet. First TracksReloaded only has the embedded set (no match for sub). A second TracksReloaded later includes the lazy external sub. The retry loop applies it.
        var pb = new FakePlayback();
        var prefs = new FakeTrackPreferences();
        var path = LocalPathInTemp("movie.mkv");
        var dir = Path.GetDirectoryName(path)!;
        prefs.Stored[(dir, MediaKind.Subtitle)] = new TrackPreference(false, "Forced", null, false, null, null);
        prefs.Stored[(dir, MediaKind.Audio)] = new TrackPreference(false, null, "eng", false, null, null);
        try
        {
            var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs, new FakeUrlDownloader(), new FakeUrlPrompt());
            // Initial track-list: matches audio (eng) but NOT subtitle (no track titled "Forced").
            pb.AudioTracks = new[] { new MediaTrack(11, null, "eng", false, null) };
            pb.SubtitleTracks = new[] { new MediaTrack(21, "English", "eng", false, null) };
            ctx.OpenFile(path);

            pb.RaiseFileLoaded();
            pb.RaiseTracksReloaded();
            // Audio applied (matched by lang). Subtitle did NOT apply (no "Forced" title in the list).
            Assert.That(pb.AudioSelections, Is.EqualTo(new int?[] { 11 }));
            Assert.That(pb.SubtitleSelections, Is.Empty);

            // Second TracksReloaded — mpv has now lazy-loaded the forced-subs sidecar.
            pb.SubtitleTracks = new[]
            {
                new MediaTrack(21, "English", "eng", false, null),
                new MediaTrack(22, "Forced", "eng", true, "forced.srt"),
            };
            pb.RaiseTracksReloaded();
            // Audio is NOT re-applied (already done on the first pass).
            Assert.That(pb.AudioSelections, Is.EqualTo(new int?[] { 11 }));
            // Subtitle IS now applied (the matching track appeared).
            Assert.That(pb.SubtitleSelections, Is.EqualTo(new int?[] { 22 }));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void ApplyDoesNotReFireOnPostApplyTracksReloaded()
    {
        // Once a kind has been applied, a later TracksReloaded (e.g., user adds an external sub via menu) must NOT re-apply the same preference and clobber the user's just-added sub.
        var pb = new FakePlayback();
        var prefs = new FakeTrackPreferences();
        var path = LocalPathInTemp("movie.mkv");
        var dir = Path.GetDirectoryName(path)!;
        prefs.Stored[(dir, MediaKind.Subtitle)] = new TrackPreference(false, null, "eng", false, null, null);
        try
        {
            var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs, new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.SubtitleTracks = new[] { new MediaTrack(21, null, "eng", false, null) };
            ctx.OpenFile(path);
            pb.RaiseFileLoaded();
            pb.RaiseTracksReloaded();

            Assert.That(pb.SubtitleSelections, Is.EqualTo(new int?[] { 21 }));

            // Simulate user adding an external sub: track-list grows, TracksReloaded fires again.
            pb.SubtitleTracks = new[]
            {
                new MediaTrack(21, null, "eng", false, null),
                new MediaTrack(22, "External", "eng", true, "added.srt"),
            };
            pb.RaiseTracksReloaded();

            // Critical: NO re-apply. Subtitle stays at the user's now-current pick (which would be 22 in the real flow; the fake doesn't auto-select).
            Assert.That(pb.SubtitleSelections, Is.EqualTo(new int?[] { 21 }));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void ApplySkipsForUriSources()
    {
        var pb = new FakePlayback();
        var prefs = new FakeTrackPreferences();
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), prefs, new FakeUrlDownloader(), new FakeUrlPrompt());
        pb.AudioTracks = new[] { new MediaTrack(1, null, "eng", false, null) };
        ctx.OpenFile("https://example.com/stream.m3u8");
        pb.RaiseFileLoaded();
        pb.RaiseTracksReloaded();
        Assert.That(pb.AudioSelections, Is.Empty);
        Assert.That(prefs.GetCalls, Is.Empty);
    }

    // --- Per-file play position: save triggers + apply on reload ---

    [Test]
    public void OpenFileWithSavedPositionSeeksAfterFileLoaded()
    {
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var path = LocalPathInTemp("movie.mkv");
        recents.Positions[path] = 60;
        try
        {
            var ctx = new VideoContext(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.DurationSeconds = 600;
            ctx.OpenFile(path);
            // Apply happens on FileLoaded (which is when duration is reliably populated in the real flow), not on OpenFile itself.
            Assert.That(pb.LastSeekSeconds, Is.Null);
            pb.RaiseFileLoaded();
            Assert.That(pb.LastSeekSeconds, Is.EqualTo(60));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void OpenFileWithSavedPositionAppliesWhenDurationArrivesAfterFileLoaded()
    {
        // mpv's emission order between MPV_EVENT_FILE_LOADED and the synthesized property-change for `duration` is not contractually guaranteed: track lists land before FileLoaded (per the existing comment at OnPlaybackFileLoaded), but duration may land after on some versions / formats. The apply must work regardless of which arrives first.
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var path = LocalPathInTemp("movie.mkv");
        recents.Positions[path] = 60;
        try
        {
            var ctx = new VideoContext(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
            // Simulate "FileLoaded fires before duration is known" — DurationSeconds defaults to 0 on FakePlayback.
            ctx.OpenFile(path);
            pb.RaiseFileLoaded();
            Assert.That(pb.LastSeekSeconds, Is.Null, "duration not yet known, no seek possible");

            // Now duration arrives.
            pb.DurationSeconds = 600;
            Assert.That(pb.LastSeekSeconds, Is.EqualTo(60), "apply must retry once duration is known");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void OpenFileWithoutSavedPositionDoesNotSeek()
    {
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var path = LocalPathInTemp("movie.mkv");
        try
        {
            var ctx = new VideoContext(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.DurationSeconds = 600;
            ctx.OpenFile(path);
            pb.RaiseFileLoaded();
            Assert.That(pb.LastSeekSeconds, Is.Null);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void OpenFileWithSavedPositionPastDurationDoesNotSeek()
    {
        // Saved position is within 5 seconds of the end — ignore it and start fresh. Same near-end filter the save side uses; symmetric.
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var path = LocalPathInTemp("movie.mkv");
        recents.Positions[path] = 597;
        try
        {
            var ctx = new VideoContext(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.DurationSeconds = 600;
            ctx.OpenFile(path);
            pb.RaiseFileLoaded();
            Assert.That(pb.LastSeekSeconds, Is.Null);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void OpenFileWithSavedPositionZeroDoesNotSeek()
    {
        // Don't waste a seek round-trip when the saved position is the natural start position.
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var path = LocalPathInTemp("movie.mkv");
        recents.Positions[path] = 0;
        try
        {
            var ctx = new VideoContext(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.DurationSeconds = 600;
            ctx.OpenFile(path);
            pb.RaiseFileLoaded();
            Assert.That(pb.LastSeekSeconds, Is.Null);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void OpenFileSavesOutgoingFilesPosition()
    {
        // The "user moved on from file A to file B" case. Save A's position synchronously inside OpenFile(B), before currentFilePath is overwritten. Replaces the FileEnded subscription idea — see plan notes about the broken evt.Error reason field and the EOF/keep-open=yes incoherence.
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var pathA = LocalPathInTemp("a.mkv");
        var pathB = LocalPathInTemp("b.mkv");
        try
        {
            var ctx = new VideoContext(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.DurationSeconds = 600;
            ctx.OpenFile(pathA);
            pb.RaiseFileLoaded();
            pb.PositionSeconds = 60;
            // Default IsPaused=true means the periodic save is gated off; only the outgoing-file save should appear when we switch to B.
            ctx.OpenFile(pathB);
            Assert.That(recents.RecordedPositions, Has.Count.EqualTo(1));
            Assert.That(recents.RecordedPositions[0].Path, Is.EqualTo(pathA));
            Assert.That(recents.RecordedPositions[0].Position, Is.EqualTo(60));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(pathA)!, recursive: true);
            Directory.Delete(Path.GetDirectoryName(pathB)!, recursive: true);
        }
    }

    [Test]
    public void PauseTransitionSavesPosition()
    {
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var path = LocalPathInTemp("movie.mkv");
        try
        {
            var ctx = new VideoContext(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.DurationSeconds = 600;
            pb.IsPaused = false;
            ctx.OpenFile(path);
            pb.RaiseFileLoaded();
            pb.PositionSeconds = 30;
            // The position bump fires a periodic save (|30-0|>=10 with IsPaused=false). Drop it so the assertion isolates the pause-edge save.
            recents.RecordedPositions.Clear();

            pb.IsPaused = true;
            Assert.That(recents.RecordedPositions, Has.Count.EqualTo(1));
            Assert.That(recents.RecordedPositions[0].Path, Is.EqualTo(path));
            Assert.That(recents.RecordedPositions[0].Position, Is.EqualTo(30));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void UnpausingDoesNotSavePosition()
    {
        // Only the rising edge (play→pause) saves; the falling edge (pause→play) carries no new information that the periodic save won't catch later.
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var path = LocalPathInTemp("movie.mkv");
        try
        {
            var ctx = new VideoContext(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.DurationSeconds = 600;
            pb.IsPaused = true;
            ctx.OpenFile(path);
            pb.RaiseFileLoaded();

            pb.IsPaused = false;
            Assert.That(recents.RecordedPositions, Is.Empty);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void PeriodicSaveDuringPlaybackFiresEveryTenSeconds()
    {
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var path = LocalPathInTemp("movie.mkv");
        try
        {
            var ctx = new VideoContext(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.DurationSeconds = 600;
            pb.IsPaused = false;
            ctx.OpenFile(path);
            pb.RaiseFileLoaded();

            for (int i = 1; i <= 25; i++)
            {
                pb.PositionSeconds = i;
            }
            // Saves expected at 10 (|10-0|=10) and 20 (|20-10|=10). 25 is only +5 since the last save.
            Assert.That(recents.RecordedPositions.Select(r => r.Position), Is.EqualTo(new[] { 10.0, 20.0 }));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void PeriodicSaveWhilePausedDoesNotFire()
    {
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var path = LocalPathInTemp("movie.mkv");
        try
        {
            var ctx = new VideoContext(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.DurationSeconds = 600;
            pb.IsPaused = true;
            ctx.OpenFile(path);
            pb.RaiseFileLoaded();

            for (int i = 1; i <= 25; i++)
            {
                pb.PositionSeconds = i;
            }
            Assert.That(recents.RecordedPositions, Is.Empty);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void PositionNearEndIsNotSaved()
    {
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var path = LocalPathInTemp("movie.mkv");
        try
        {
            var ctx = new VideoContext(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.DurationSeconds = 600;
            ctx.OpenFile(path);
            pb.RaiseFileLoaded();
            pb.PositionSeconds = 599;
            recents.RecordedPositions.Clear();
            ctx.Dispose();
            Assert.That(recents.RecordedPositions, Is.Empty);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void PeriodicSaveNearEndDoesNotFire()
    {
        // Companion to PositionNearEndIsNotSaved: same near-end filter must elide the periodic-save trigger too, not just the dispose-time save. Important for the EOF-with-keep-open=yes case where mpv keeps firing time-pos at duration with IsPaused=false.
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var path = LocalPathInTemp("movie.mkv");
        try
        {
            var ctx = new VideoContext(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.DurationSeconds = 600;
            pb.IsPaused = false;
            ctx.OpenFile(path);
            pb.RaiseFileLoaded();

            // Walk position from 595 to 600 in 1s steps — all within the (duration - NearEndIgnoreSeconds = 595) skip threshold. Each tick fires PropertyChanged; periodic save would normally trigger (delta from lastSaved=0 is well over 10), but the near-end filter must skip every one of them.
            for (int i = 595; i <= 600; i++)
            {
                pb.PositionSeconds = i;
            }
            Assert.That(recents.RecordedPositions, Is.Empty);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void DisposeSavesFinalPosition()
    {
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var path = LocalPathInTemp("movie.mkv");
        try
        {
            var ctx = new VideoContext(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
            pb.DurationSeconds = 600;
            pb.IsPaused = false;
            ctx.OpenFile(path);
            pb.RaiseFileLoaded();
            pb.PositionSeconds = 30;
            recents.RecordedPositions.Clear();
            ctx.Dispose();
            Assert.That(recents.RecordedPositions, Has.Count.EqualTo(1));
            Assert.That(recents.RecordedPositions[0].Position, Is.EqualTo(30));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void UriSourceDoesNotPersistPosition()
    {
        // URIs (http/smb/…) skip both the apply lookup AND every save trigger. Position persistence is for local paths only.
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var ctx = new VideoContext(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        pb.DurationSeconds = 600;
        pb.IsPaused = false;
        ctx.OpenFile("https://example.com/stream.m3u8");
        pb.RaiseFileLoaded();
        pb.PositionSeconds = 50;
        pb.IsPaused = true;
        ctx.Dispose();
        Assert.That(recents.GetPositionCalls, Is.Empty);
        Assert.That(recents.RecordedPositions, Is.Empty);
    }

    [Test]
    public void OpenFileLoadsTheTarget()
    {
        var pb = new FakePlayback();
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        ctx.OpenFile("/path/to/dropped.mp4");
        Assert.That(pb.LoadFileCalls, Is.EqualTo(1));
        Assert.That(pb.LastLoadedFile, Is.EqualTo("/path/to/dropped.mp4"));
    }

    [Test]
    public void OpenFileRecordsTheTarget()
    {
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var ctx = new VideoContext(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        ctx.OpenFile("/path/to/dropped.mp4");
        Assert.That(recents.RecordedPaths, Is.EqualTo(new[] { "/path/to/dropped.mp4" }));
    }

    [Test]
    public async Task OpenFileAcceptsRemoteUris()
    {
        // A direct-stream URL — yt-dlp's classification step returns MpvDirect (matches the generic extractor), so mpv loads the URL itself rather than going through a download. Recents records the original URI either way.
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var dl = new FakeUrlDownloader { ClassifyResult = Vomplayer.Services.UrlLoadKind.MpvDirect };
        var ctx = new VideoContext(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), dl, new FakeUrlPrompt());
        ctx.OpenFile("https://example.com/stream.m3u8");
        await Task.Yield();
        await Task.Delay(10);
        Assert.That(pb.LastLoadedFile, Is.EqualTo("https://example.com/stream.m3u8"));
        Assert.That(recents.RecordedPaths, Is.EqualTo(new[] { "https://example.com/stream.m3u8" }));
        Assert.That(dl.DownloadCalls, Is.Empty, "mpv-direct branch must not invoke DownloadAsync");
    }

    // --- Playlist: LoadPaths, PlayPlaylistItem, OpenFile back-compat ---

    [Test]
    public void OpenFilePublicReplacesPlaylistWithSingleItem()
    {
        var pb = new FakePlayback();
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        ctx.OpenFile("/path/to/a.mp4");
        Assert.That(ctx.Playlist.Items, Is.EqualTo(new[] { "/path/to/a.mp4" }));
        Assert.That(ctx.Playlist.CurrentIndex, Is.EqualTo(0));
        Assert.That(pb.LastLoadedFile, Is.EqualTo("/path/to/a.mp4"));

        ctx.OpenFile("/path/to/b.mp4");
        Assert.That(ctx.Playlist.Items, Is.EqualTo(new[] { "/path/to/b.mp4" }));
        Assert.That(pb.LastLoadedFile, Is.EqualTo("/path/to/b.mp4"));
    }

    [Test]
    public void LoadPathsReplaceLoadsFirstItem()
    {
        var pb = new FakePlayback();
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        ctx.LoadPaths(new[] { "/a", "/b", "/c" }, replace: true);
        Assert.That(ctx.Playlist.Items, Is.EqualTo(new[] { "/a", "/b", "/c" }));
        Assert.That(ctx.Playlist.CurrentIndex, Is.EqualTo(0));
        Assert.That(pb.LoadFileCalls, Is.EqualTo(1));
        Assert.That(pb.LastLoadedFile, Is.EqualTo("/a"));
    }

    [Test]
    public void LoadPathsReplaceWithEmptyDoesNothing()
    {
        var pb = new FakePlayback();
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        ctx.LoadPaths(new[] { "/a" }, replace: true);
        Assert.That(pb.LoadFileCalls, Is.EqualTo(1));

        ctx.LoadPaths(Array.Empty<string>(), replace: true);
        // Empty input is a no-op — does not orphan the currently-playing item.
        Assert.That(ctx.Playlist.Items, Is.EqualTo(new[] { "/a" }));
        Assert.That(pb.LoadFileCalls, Is.EqualTo(1));
    }

    [Test]
    public void LoadPathsAppendToEmptyKicksOffPlayback()
    {
        var pb = new FakePlayback();
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        ctx.LoadPaths(new[] { "/a", "/b" }, replace: false);
        Assert.That(ctx.Playlist.Items, Is.EqualTo(new[] { "/a", "/b" }));
        Assert.That(ctx.Playlist.CurrentIndex, Is.EqualTo(0));
        Assert.That(pb.LastLoadedFile, Is.EqualTo("/a"));
    }

    [Test]
    public void LoadPathsAppendToNonEmptyDoesNotChangeCurrent()
    {
        var pb = new FakePlayback();
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        ctx.LoadPaths(new[] { "/a", "/b" }, replace: true);
        pb.LoadFileCalls = 0;
        pb.LastLoadedFile = null;

        ctx.LoadPaths(new[] { "/c", "/d" }, replace: false);
        Assert.That(ctx.Playlist.Items, Is.EqualTo(new[] { "/a", "/b", "/c", "/d" }));
        Assert.That(ctx.Playlist.CurrentIndex, Is.EqualTo(0));
        // No new load fired — current item keeps playing.
        Assert.That(pb.LoadFileCalls, Is.EqualTo(0));
    }

    [Test]
    public void PlayPlaylistItemSwitchesCurrent()
    {
        var pb = new FakePlayback();
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        ctx.LoadPaths(new[] { "/a", "/b", "/c" }, replace: true);
        pb.LoadFileCalls = 0;
        pb.LastLoadedFile = null;

        ctx.PlayPlaylistItem(2);
        Assert.That(ctx.Playlist.CurrentIndex, Is.EqualTo(2));
        Assert.That(pb.LoadFileCalls, Is.EqualTo(1));
        Assert.That(pb.LastLoadedFile, Is.EqualTo("/c"));
    }

    // --- Playlist: auto-advance ---

    [Test]
    public void EofReachedRisingEdgeDoesNotAdvanceWhilePendingLoad()
    {
        // The currentFileLoaded gate. A stale eof-reached=true from the prior file's tail can land in the dispatcher queue after LoadFile dispatches but before the new file's FileLoaded fires. Without the gate, the spurious rising edge would skip the just-loaded file.
        var pb = new FakePlayback();
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        ctx.LoadPaths(new[] { "/a", "/b" }, replace: true);
        // FileLoaded NOT raised — currentFileLoaded is still false.
        pb.DurationSeconds = 60;

        pb.IsEofReached = true;

        Assert.That(ctx.Playlist.CurrentIndex, Is.EqualTo(0));
        Assert.That(pb.LastLoadedFile, Is.EqualTo("/a"));
    }

    [Test]
    public void EofReachedRisingEdgeDoesNotAdvanceForLiveStream()
    {
        // The DurationSeconds > 0 gate. Live streams may oscillate eof-reached without a meaningful "next item" semantic.
        var pb = new FakePlayback();
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        ctx.LoadPaths(new[] { "https://example.com/live", "/b" }, replace: true);
        pb.RaiseFileLoaded();
        // DurationSeconds remains 0 — live source.

        pb.IsEofReached = true;

        Assert.That(ctx.Playlist.CurrentIndex, Is.EqualTo(0));
    }

    [Test]
    public void EofReachedAtLastItemDoesNotAdvance()
    {
        var pb = new FakePlayback();
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        ctx.LoadPaths(new[] { "/a" }, replace: true);
        pb.RaiseFileLoaded();
        pb.DurationSeconds = 60;
        pb.LastLoadedFile = null;
        int loadsBeforeEof = pb.LoadFileCalls;

        pb.IsEofReached = true;

        Assert.That(pb.LoadFileCalls, Is.EqualTo(loadsBeforeEof));
        Assert.That(ctx.Playlist.CurrentIndex, Is.EqualTo(0));
    }

    [Test]
    public void EofReachedNoOpsOutsidePlaylist()
    {
        // No LoadPaths call yet — playlist is empty. EOF on whatever happens to be playing (shouldn't happen in practice; defense in depth).
        var pb = new FakePlayback();
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        pb.DurationSeconds = 60;

        pb.IsEofReached = true;

        Assert.That(ctx.Playlist.Items, Is.Empty);
        Assert.That(pb.LoadFileCalls, Is.EqualTo(0));
    }

    [Test]
    public void LastItemEofThenUserClicksRowDoesNotSpuriouslyAdvance()
    {
        // Sequence: last item ends (auto-advance returns null, no load), user double-clicks an earlier row. The intervening LoadCurrentItem must reset wasEofReached so the row's eventual EOF triggers a real advance, but the row click itself must NOT auto-advance past whatever it landed on.
        var pb = new FakePlayback();
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
        ctx.LoadPaths(new[] { "/a", "/b", "/c" }, replace: true);
        pb.RaiseFileLoaded();
        pb.DurationSeconds = 60;

        // Walk through to the last item and EOF.
        pb.IsEofReached = true;     // → /b
        pb.IsEofReached = false;
        pb.RaiseFileLoaded();
        pb.IsEofReached = true;     // → /c
        pb.IsEofReached = false;
        pb.RaiseFileLoaded();
        pb.IsEofReached = true;     // last item; no advance
        Assert.That(ctx.Playlist.CurrentIndex, Is.EqualTo(2));

        // User clicks row 0 from the panel.
        pb.LoadFileCalls = 0;
        ctx.PlayPlaylistItem(0);
        Assert.That(ctx.Playlist.CurrentIndex, Is.EqualTo(0));
        Assert.That(pb.LoadFileCalls, Is.EqualTo(1));
        // No spurious auto-advance from the lingering eof state.
        Assert.That(pb.LastLoadedFile, Is.EqualTo("/a"));
    }

    [Test]
    public async Task AutoAdvanceIntoUriItemWorks()
    {
        // The next item is a URI; auto-advance shouldn't bail just because the entry isn't a local-fs path. The IsLocalFilesystemPath gate inside LoadCurrentItem only affects the recents-position lookup, not the load itself. With ClassifyResult=MpvDirect (matches a real HLS m3u8 that yt-dlp's generic extractor handles), the loaded path is the URL itself.
        var pb = new FakePlayback();
        var dl = new FakeUrlDownloader { ClassifyResult = Vomplayer.Services.UrlLoadKind.MpvDirect };
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, new FakeUrlPrompt());
        ctx.LoadPaths(new[] { "/local.mkv", "https://example.com/stream.m3u8" }, replace: true);
        pb.RaiseFileLoaded();
        pb.DurationSeconds = 60;

        pb.IsEofReached = true;
        await Task.Yield();
        await Task.Delay(10);

        Assert.That(ctx.Playlist.CurrentIndex, Is.EqualTo(1));
        Assert.That(pb.LastLoadedFile, Is.EqualTo("https://example.com/stream.m3u8"));
    }

    [Test]
    public void AutoAdvanceElidesNearEndOutgoingSave()
    {
        // Pin the no-save-on-natural-EOF behavior. At natural EOF with keep-open=yes, position parks at duration; SaveCurrentPositionIfEligible's near-end gate (position >= duration - 5) elides the save. If a future change inverts the gate, this test catches the regression.
        var pb = new FakePlayback();
        var recents = new FakeRecentFiles();
        var path = LocalPathInTemp("a.mkv");
        var pathB = LocalPathInTemp("b.mkv");
        try
        {
            var ctx = new VideoContext(pb, new FakeFilePicker(), recents, new FakeTrackPreferences(), new FakeUrlDownloader(), new FakeUrlPrompt());
            ctx.LoadPaths(new[] { path, pathB }, replace: true);
            pb.RaiseFileLoaded();
            pb.DurationSeconds = 600;
            pb.PositionSeconds = 599;   // within the near-end window
            recents.RecordedPositions.Clear();

            pb.IsEofReached = true;     // auto-advance triggers SaveCurrentPositionIfEligible for the outgoing file

            Assert.That(recents.RecordedPositions, Is.Empty);
            Assert.That(ctx.Playlist.CurrentIndex, Is.EqualTo(1));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
            Directory.Delete(Path.GetDirectoryName(pathB)!, recursive: true);
        }
    }

    // ── Open URL flow ─────────────────────────────────────────────────────────

    [Test]
    public async Task OpenUrlShowsErrorWhenYtDlpUnavailable()
    {
        var pb = new FakePlayback();
        var dl = new FakeUrlDownloader { Available = false };
        var prompt = new FakeUrlPrompt();
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, prompt);

        await ctx.OpenUrlAsync();

        Assert.That(prompt.Errors, Has.Count.EqualTo(1));
        Assert.That(prompt.Errors[0].Title, Does.Contain("yt-dlp"));
        Assert.That(prompt.PromptCalls, Is.EqualTo(0), "should not prompt for URL when yt-dlp unavailable");
    }

    [Test]
    public async Task OpenUrlPromptCancelDoesNothing()
    {
        var pb = new FakePlayback();
        var dl = new FakeUrlDownloader();
        var prompt = new FakeUrlPrompt { NextUrl = null };
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, prompt);

        await ctx.OpenUrlAsync();

        Assert.That(prompt.PromptCalls, Is.EqualTo(1));
        Assert.That(dl.ProbeCalls, Is.Empty);
        Assert.That(ctx.Playlist.Items, Is.Empty);
    }

    [Test]
    public async Task OpenUrlSingleVideoLoadsAsSingleItem()
    {
        var pb = new FakePlayback();
        var dl = new FakeUrlDownloader
        {
            NextProbeResult = new[] { "https://youtu.be/abc" },
            DownloadResolver = u => "/cache/abc/video.mp4",
        };
        var prompt = new FakeUrlPrompt { NextUrl = "https://youtu.be/abc" };
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, prompt);

        await ctx.OpenUrlAsync();

        Assert.That(ctx.Playlist.Items, Is.EquivalentTo(new[] { "https://youtu.be/abc" }));
        // The download should have been kicked off by LoadCurrentItem.
        Assert.That(dl.DownloadCalls, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task OpenUrlPlaylistPopulatesWithoutAutoplay()
    {
        var pb = new FakePlayback();
        var dl = new FakeUrlDownloader
        {
            NextProbeResult = new[] { "https://youtu.be/a", "https://youtu.be/b", "https://youtu.be/c" },
            DownloadResolver = u => $"/cache/{u}.mp4",
        };
        var prompt = new FakeUrlPrompt { NextUrl = "https://youtube.com/playlist?list=XYZ" };
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, prompt);

        await ctx.OpenUrlAsync();

        Assert.That(ctx.Playlist.Items, Has.Count.EqualTo(3));
        Assert.That(ctx.Playlist.CurrentIndex, Is.EqualTo(0));
        // Multi-entry OpenUrl populates the playlist but does NOT auto-download — the user picks a row to start. (Single-video URLs still autoplay; see OpenUrlSingleVideoLoadsAsSingleItem.)
        Assert.That(dl.DownloadCalls, Is.Empty);
        Assert.That(pb.LoadedFiles, Is.Empty);
    }

    [Test]
    public async Task OpenUrlPlaylistDownloadsOnlyAfterRowClick()
    {
        var pb = new FakePlayback();
        var dl = new FakeUrlDownloader
        {
            NextProbeResult = new[] { "https://youtu.be/a", "https://youtu.be/b", "https://youtu.be/c" },
            DownloadResolver = u => $"/cache/{u}.mp4",
        };
        var prompt = new FakeUrlPrompt { NextUrl = "https://youtube.com/playlist?list=XYZ" };
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, prompt);

        await ctx.OpenUrlAsync();
        // User clicks the second row.
        ctx.PlayPlaylistItem(1);
        await Task.Yield();
        await Task.Delay(10);

        Assert.That(dl.DownloadCalls, Is.EquivalentTo(new[] { "https://youtu.be/b" }));
        Assert.That(pb.LoadedFiles, Is.EquivalentTo(new[] { "/cache/https://youtu.be/b.mp4" }));
    }

    [Test]
    public async Task OpenUrlProbeFailureSurfacesError()
    {
        var pb = new FakePlayback();
        var dl = new FailingProbeDownloader("upstream parse failed");
        var prompt = new FakeUrlPrompt { NextUrl = "https://something" };
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, prompt);

        await ctx.OpenUrlAsync();

        Assert.That(prompt.Errors, Has.Count.EqualTo(1));
        Assert.That(prompt.Errors[0].Message, Does.Contain("upstream parse failed"));
        Assert.That(ctx.Playlist.Items, Is.Empty);
    }

    [Test]
    public async Task LoadCurrentItemUrlRoutesThroughDownloader()
    {
        // Prove that cache-hit DownloadAsync wires through to playback.LoadFile with the LOCAL path, not the URL.
        var pb = new FakePlayback();
        var dl = new FakeUrlDownloader
        {
            NextProbeResult = new[] { "https://youtu.be/X" },
            DownloadResolver = u => "/local/cache/X.mp4",
        };
        var prompt = new FakeUrlPrompt { NextUrl = "https://youtu.be/X" };
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, prompt);

        await ctx.OpenUrlAsync();
        // Allow the fire-and-forget LoadUrlAsync to complete its synchronous Task.FromResult path.
        await Task.Yield();
        await Task.Delay(10);

        Assert.That(pb.LoadedFiles, Is.EquivalentTo(new[] { "/local/cache/X.mp4" }));
    }

    [Test]
    public async Task LoadCurrentItemLocalPathIsNotDownloaded()
    {
        // Local-filesystem paths bypass the downloader and go straight to mpv. Routing is by URI shape — a "looks like a scheme://..." string heads to yt-dlp, anything else heads to mpv.
        var pb = new FakePlayback();
        var dl = new FakeUrlDownloader();
        var prompt = new FakeUrlPrompt();
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, prompt);

        ctx.OpenFile("/some/local/file.mkv");

        Assert.That(dl.DownloadCalls, Is.Empty);
        Assert.That(pb.LoadedFiles, Is.EquivalentTo(new[] { "/some/local/file.mkv" }));
    }

    [Test]
    public async Task RapidLoadCancelsPreviousDownload()
    {
        // Race scenario from the design review: download A is in-flight; user clicks playlist row B. The CTS for A must be cancelled and A's late completion must NOT call playback.LoadFile.
        var pb = new FakePlayback();
        var pendingA = new TaskCompletionSource<string>();
        var dl = new FakeUrlDownloader
        {
            NextProbeResult = new[] { "https://youtu.be/A", "https://youtu.be/B" },
            PendingDownload = pendingA,
        };
        var prompt = new FakeUrlPrompt { NextUrl = "https://youtube.com/playlist?list=Z" };
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, prompt);

        await ctx.OpenUrlAsync();
        // Multi-entry OpenUrl no longer auto-starts; click row 0 to put A in flight.
        ctx.PlayPlaylistItem(0);
        await Task.Yield();
        // Now B is in queue. Swap PendingDownload so B's DownloadAsync resolves synchronously (Task.FromResult path).
        dl.PendingDownload = null;
        dl.DownloadResolver = u => $"/cache/{u}.mp4";

        // Trigger LoadCurrentItem for B.
        ctx.PlayPlaylistItem(1);
        await Task.Yield();
        await Task.Delay(10);

        // A's TCS was cancelled by the CTS swap.
        Assert.That(pendingA.Task.IsCanceled, Is.True, "previous download CTS should have been cancelled when LoadCurrentItem started B");
        // Only B's local path made it to playback.
        Assert.That(pb.LoadedFiles, Is.EquivalentTo(new[] { "/cache/https://youtu.be/B.mp4" }));
        // Every status overlay shown (the interactive probe plus A's and B's load phases) was balanced by a dispose — no overlay is left stuck visible when a load is cancelled or superseded.
        Assert.That(prompt.StatusShown, Is.GreaterThanOrEqualTo(2), "A and B each showed a load status");
        Assert.That(prompt.StatusDisposed, Is.EqualTo(prompt.StatusShown), "every status shown was hidden (A cancelled, B succeeded)");
    }

    [Test]
    public async Task FailedDownloadShowsErrorOnlyForActiveLoad()
    {
        // Pin the "ShowError only fires for the active load's failure" guard. Stale downloads that fail after the user moved on should NOT pop up an error dialog.
        var pb = new FakePlayback();
        var pendingA = new TaskCompletionSource<string>();
        var dl = new FakeUrlDownloader
        {
            NextProbeResult = new[] { "https://youtu.be/A", "https://youtu.be/B" },
            PendingDownload = pendingA,
        };
        var prompt = new FakeUrlPrompt { NextUrl = "https://youtube.com/playlist?list=Z" };
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, prompt);

        await ctx.OpenUrlAsync();
        // Multi-entry OpenUrl no longer auto-starts; click row 0 to put A in flight.
        ctx.PlayPlaylistItem(0);
        await Task.Yield();
        // User moves on to B before A completes.
        dl.PendingDownload = null;
        dl.DownloadResolver = u => $"/cache/{u}.mp4";
        ctx.PlayPlaylistItem(1);
        await Task.Yield();
        await Task.Delay(10);

        // Now A's pending TCS faults — but A is no longer the active load. ShowError must NOT fire for A.
        pendingA.TrySetException(new InvalidOperationException("A failed late"));
        await Task.Yield();
        await Task.Delay(10);

        // No error dialog should have been shown — only B succeeded, A's late failure is suppressed.
        Assert.That(prompt.Errors, Is.Empty, "stale download failure must not surface an error to the user");
    }

    [Test]
    public async Task LoadOpenFileForUrlRoutesThroughDownloader()
    {
        // Drag-drop / CLI / programmatic OpenFile of a URL routes through the downloader the same way Open-URL does. Pre-fix, this path silently bypassed the cache because urlsRequiringDownload was only populated by OpenUrl; the bypass left mpv to stream via its built-in ytdl-hook with nothing landing in url-downloads.
        var pb = new FakePlayback();
        var dl = new FakeUrlDownloader
        {
            DownloadResolver = u => $"/cache/{u}.mp4",
        };
        var prompt = new FakeUrlPrompt();
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, prompt);

        ctx.OpenFile("https://youtu.be/from-drag-drop");
        await Task.Yield();
        await Task.Delay(10);

        Assert.That(dl.DownloadCalls, Is.EquivalentTo(new[] { "https://youtu.be/from-drag-drop" }));
        Assert.That(pb.LoadedFiles, Is.EquivalentTo(new[] { "/cache/https://youtu.be/from-drag-drop.mp4" }));
    }

    [Test]
    public async Task RestorePlaylistForUrlRoutesThroughDownloader()
    {
        // Recent-menu / app-startup restore for a URL with a specific extractor (YouTube) routes through the downloader. Pre-fix this bypassed the cache because urlsRequiringDownload was in-memory only; URI-shape gating + per-load classification closes that gap.
        var pb = new FakePlayback();
        var dl = new FakeUrlDownloader
        {
            DownloadResolver = u => $"/cache/{u}.mp4",
        };
        var prompt = new FakeUrlPrompt();
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, prompt);

        ctx.RestorePlaylist(new[] { "https://youtu.be/from-recent" }, currentIndex: 0, startPaused: false);
        await Task.Yield();
        await Task.Delay(10);

        Assert.That(dl.ClassifyCalls, Is.EquivalentTo(new[] { "https://youtu.be/from-recent" }));
        Assert.That(dl.DownloadCalls, Is.EquivalentTo(new[] { "https://youtu.be/from-recent" }));
        Assert.That(pb.LoadedFiles, Is.EquivalentTo(new[] { "/cache/https://youtu.be/from-recent.mp4" }));
    }

    [TestCase("dvd://1")]
    [TestCase("bd://")]
    [TestCase("smb://server/share/file.mkv")]
    [TestCase("sftp://user@host/file.mp4")]
    [TestCase("rtsp://host/stream")]
    [TestCase("rtmp://host/live")]
    [TestCase("file:///home/zorba/movie.mp4")]
    [TestCase("ftp://host/file.mp4")]
    [TestCase("magnet:?xt=urn:btih:abc")]
    public async Task LoadCurrentItemMpvNativeSchemeBypassesClassification(string nonHttpUri)
    {
        // mpv-native protocols (dvd, bd, smb, sftp, rtsp, rtmp, file, ftp) and magnet links go straight to mpv. Routing them through yt-dlp's classifier would waste a process spawn and end with "Download failed" — yt-dlp has no extractor for any of these. The URI-shape gate (ShouldProbe = http(s) only) keeps them on the mpv-direct path.
        var pb = new FakePlayback();
        var dl = new FakeUrlDownloader();
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, new FakeUrlPrompt());

        ctx.OpenFile(nonHttpUri);
        await Task.Yield();
        await Task.Delay(10);

        Assert.That(pb.LastLoadedFile, Is.EqualTo(nonHttpUri));
        Assert.That(dl.ClassifyCalls, Is.Empty, "non-http(s) URIs must skip classification");
        Assert.That(dl.DownloadCalls, Is.Empty);
    }

    [Test]
    public async Task LoadCurrentItemProbeFailureFallsBackToMpvDirect()
    {
        // yt-dlp not on PATH, or any other classify failure — fall back to handing the URL to mpv. mpv's built-in ytdl-hook will surface its own error if the URL really required yt-dlp; a direct-stream URL just plays.
        var pb = new FakePlayback();
        var dl = new FakeUrlDownloader { ClassifyException = new InvalidOperationException("yt-dlp not found") };
        var prompt = new FakeUrlPrompt();
        var ctx = new VideoContext(pb, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), dl, prompt);

        ctx.OpenFile("https://youtu.be/probe-fails");
        await Task.Yield();
        await Task.Delay(10);

        Assert.That(pb.LastLoadedFile, Is.EqualTo("https://youtu.be/probe-fails"), "probe-failed URL should still reach mpv (mpv's own ytdl-hook may handle it)");
        Assert.That(dl.DownloadCalls, Is.Empty);
        Assert.That(prompt.Errors, Is.Empty, "probe failure is silent — mpv's own error path is the user-facing channel");
    }

    private sealed class FailingProbeDownloader : Vomplayer.Services.IUrlDownloader
    {
        private readonly string message;

        public FailingProbeDownloader(string message)
        {
            this.message = message;
        }

        public Task<bool> IsAvailableAsync(CancellationToken ct) { return Task.FromResult(true); }

        public Task<IReadOnlyList<string>> ProbeAsync(string url, CancellationToken ct)
        {
            throw new InvalidOperationException(message);
        }

        public Task<Vomplayer.Services.UrlLoadKind> ClassifyAsync(string url, CancellationToken ct)
        {
            throw new NotImplementedException();
        }

        public Task<string> DownloadAsync(string url, IProgress<Vomplayer.Services.UrlDownloadProgress>? progress, CancellationToken ct)
        {
            throw new NotImplementedException();
        }
    }
}
