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
        public bool? CurrentOutputIsHdrValue { get; set; }
        public bool? CurrentOutputIsHdr { get { return CurrentOutputIsHdrValue; } }
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
}
