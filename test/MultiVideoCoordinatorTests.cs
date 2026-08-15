using System;
using System.Collections.Generic;
using Vomplayer.Playback;
using Vomplayer.ViewModels;

namespace Vomplayer.Tests;

[TestFixture]
public class MultiVideoCoordinatorTests
{
    // Owns one FakePlayback + the ViewModel built around it. EnableSecondary spins up a second FakePlayback + VideoContext, hands it to the VM.
    private sealed class Harness : IDisposable
    {
        public FakePlayback PrimaryPlayback { get; }
        public FakePlayback? SecondaryPlayback { get; private set; }
        public ViewModelMain Vm { get; }

        public Harness()
        {
            PrimaryPlayback = new FakePlayback();
            Vm = new ViewModelMain(PrimaryPlayback, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader { Available = false }, new FakeUrlPrompt());
        }

        public VideoContext NewSecondary()
        {
            SecondaryPlayback = new FakePlayback();
            return new VideoContext(SecondaryPlayback, new FakeFilePicker(), new FakeRecentFiles(), new FakeTrackPreferences(), new FakeUrlDownloader { Available = false }, new FakeUrlPrompt());
        }

        public void EnablePip()
        {
            Vm.EnablePip(NewSecondary());
        }

        // Establish a known sync offset the way the runtime does: a Secondary FileLoaded clears to pending, and the drift controller's first both-advancing tick baselines from the current divergence. Call after EnablePip with both durations already set (the controller gates on them — the guard asserts loudly instead of leaving the offset silently pending). Restores each stream's core-idle flag afterwards so the calling test's scenario is undisturbed; positions are left at the given values.
        public void EstablishOffset(double primaryPos, double secondaryPos)
        {
            Assert.That(PrimaryPlayback.DurationSeconds, Is.GreaterThan(0), "EstablishOffset requires Primary's duration to be set first");
            Assert.That(SecondaryPlayback!.DurationSeconds, Is.GreaterThan(0), "EstablishOffset requires Secondary's duration to be set first");
            SecondaryPlayback!.RaiseFileLoaded();
            PrimaryPlayback.PositionSeconds = primaryPos;
            SecondaryPlayback!.PositionSeconds = secondaryPos;
            bool primaryIdle = PrimaryPlayback.IsCoreIdle;
            bool secondaryIdle = SecondaryPlayback!.IsCoreIdle;
            PrimaryPlayback.IsCoreIdle = false;
            SecondaryPlayback!.IsCoreIdle = false;
            Vm.ApplyDriftCorrection();
            PrimaryPlayback.IsCoreIdle = primaryIdle;
            SecondaryPlayback!.IsCoreIdle = secondaryIdle;
        }

        // Drive a single context to file-EOF: load a playlist, fire FileLoaded, set duration, set EOF. Mirrors a natural file end.
        public static void ReachFileEof(VideoContext ctx, FakePlayback pb)
        {
            pb.RaiseFileLoaded();
            pb.DurationSeconds = 60;
            pb.IsEofReached = true;
        }

        public void Dispose()
        {
            Vm.Dispose();
        }
    }

    [Test]
    public void PipOffAdvancesPrimaryAtEofLikeSingleVideo()
    {
        // Regression check: with PiP off, the coordinator must NOT have stolen advance dispatch from VideoContext. Primary should auto-advance at EOF exactly as it did pre-coordinator.
        using var h = new Harness();
        h.Vm.LoadPaths(new[] { "/a.mp4", "/b.mp4" }, replace: true);
        Harness.ReachFileEof(h.Vm.Primary, h.PrimaryPlayback);
        Assert.That(h.Vm.Primary.Playlist.CurrentIndex, Is.EqualTo(1));
        Assert.That(h.PrimaryPlayback.LoadedFiles, Is.EqualTo(new[] { "/a.mp4", "/b.mp4" }));
    }

    [Test]
    public void PipOnBothAtEofWithNextAdvancesBoth()
    {
        using var h = new Harness();
        h.Vm.LoadPaths(new[] { "/a1.mp4", "/a2.mp4" }, replace: true);
        h.EnablePip();
        h.Vm.Secondary!.LoadPaths(new[] { "/b1.mp4", "/b2.mp4" }, replace: true);

        // Primary reaches EOF first — must NOT advance alone (lockstep gate).
        Harness.ReachFileEof(h.Vm.Primary, h.PrimaryPlayback);
        Assert.That(h.Vm.Primary.Playlist.CurrentIndex, Is.EqualTo(0), "lockstep: primary alone must not advance");
        Assert.That(h.PrimaryPlayback.LoadedFiles, Is.EqualTo(new[] { "/a1.mp4" }));

        // Secondary reaches EOF — both advance.
        Harness.ReachFileEof(h.Vm.Secondary!, h.SecondaryPlayback!);
        Assert.That(h.Vm.Primary.Playlist.CurrentIndex, Is.EqualTo(1));
        Assert.That(h.Vm.Secondary!.Playlist.CurrentIndex, Is.EqualTo(1));
        Assert.That(h.PrimaryPlayback.LoadedFiles, Is.EqualTo(new[] { "/a1.mp4", "/a2.mp4" }));
        Assert.That(h.SecondaryPlayback!.LoadedFiles, Is.EqualTo(new[] { "/b1.mp4", "/b2.mp4" }));
    }

    [Test]
    public void NextTrackInSyncModeActsOnPrimaryOnly()
    {
        // Prev/Next track is a per-video command (like PlayPlaylistItem / Open*), NOT a transport command — so in sync mode (PiP on, no selection) it targets Primary only and does NOT fan out to Secondary.
        using var h = new Harness();
        h.Vm.LoadPaths(new[] { "/a1.mp4", "/a2.mp4" }, replace: true);
        h.EnablePip();
        h.Vm.Secondary!.LoadPaths(new[] { "/b1.mp4", "/b2.mp4" }, replace: true);
        Assert.That(h.Vm.SelectedSlot, Is.Null, "broadcast/sync mode");

        h.Vm.NextTrack();

        Assert.That(h.Vm.Primary.Playlist.CurrentIndex, Is.EqualTo(1), "primary advanced");
        Assert.That(h.Vm.Secondary!.Playlist.CurrentIndex, Is.EqualTo(0), "secondary untouched");
        Assert.That(h.PrimaryPlayback.LoadedFiles, Is.EqualTo(new[] { "/a1.mp4", "/a2.mp4" }));
        Assert.That(h.SecondaryPlayback!.LoadedFiles, Is.EqualTo(new[] { "/b1.mp4" }), "secondary did not load a new file");
    }

    [Test]
    public void PipOnBothAtEofPrimaryOutOfItemsDoesNotAdvance()
    {
        using var h = new Harness();
        h.Vm.LoadPaths(new[] { "/a.mp4" }, replace: true);
        h.EnablePip();
        h.Vm.Secondary!.LoadPaths(new[] { "/b1.mp4", "/b2.mp4" }, replace: true);

        Harness.ReachFileEof(h.Vm.Primary, h.PrimaryPlayback);
        Harness.ReachFileEof(h.Vm.Secondary!, h.SecondaryPlayback!);
        // Primary at last item; lockstep halts the pair.
        Assert.That(h.Vm.Primary.Playlist.CurrentIndex, Is.EqualTo(0));
        Assert.That(h.Vm.Secondary!.Playlist.CurrentIndex, Is.EqualTo(0));
        Assert.That(h.PrimaryPlayback.LoadedFiles, Is.EqualTo(new[] { "/a.mp4" }));
        Assert.That(h.SecondaryPlayback!.LoadedFiles, Is.EqualTo(new[] { "/b1.mp4" }));
    }

    [Test]
    public void PipOnBothAtEofSecondaryOutOfItemsDoesNotAdvance()
    {
        using var h = new Harness();
        h.Vm.LoadPaths(new[] { "/a1.mp4", "/a2.mp4" }, replace: true);
        h.EnablePip();
        h.Vm.Secondary!.LoadPaths(new[] { "/b.mp4" }, replace: true);

        Harness.ReachFileEof(h.Vm.Primary, h.PrimaryPlayback);
        Harness.ReachFileEof(h.Vm.Secondary!, h.SecondaryPlayback!);
        Assert.That(h.Vm.Primary.Playlist.CurrentIndex, Is.EqualTo(0));
        Assert.That(h.Vm.Secondary!.Playlist.CurrentIndex, Is.EqualTo(0));
    }

    [Test]
    public void PipOnBothAtEofPreservesPausedContextPauseState()
    {
        // User paused one half (Primary) while waiting for the longer half (Secondary) to finish. Lockstep advance must NOT unpause the half the user deliberately paused. AdvanceAndLoadIfPossible calls Playback.LoadFile with startPaused=false (auto-advance treats the next file as "keep playing"); the coordinator captures the pre-advance pause state and re-applies pause=true via SetPaused after the advance so the deliberately-paused context lands paused on the next track.
        using var h = new Harness();
        h.Vm.LoadPaths(new[] { "/a1.mp4", "/a2.mp4" }, replace: true);
        h.EnablePip();
        h.Vm.Secondary!.LoadPaths(new[] { "/b1.mp4", "/b2.mp4" }, replace: true);

        // Both contexts running, then user pauses primary.
        h.PrimaryPlayback.IsPaused = false;
        h.SecondaryPlayback!.IsPaused = false;
        h.PrimaryPlayback.IsPaused = true;

        Harness.ReachFileEof(h.Vm.Primary, h.PrimaryPlayback);
        Harness.ReachFileEof(h.Vm.Secondary!, h.SecondaryPlayback!);

        // Both advanced.
        Assert.That(h.Vm.Primary.Playlist.CurrentIndex, Is.EqualTo(1));
        Assert.That(h.Vm.Secondary!.Playlist.CurrentIndex, Is.EqualTo(1));

        // Primary was paused → exactly one SetPaused(true) recorded, and it lands AFTER both LoadFile calls completed (synchronous main-thread path; VM doesn't issue any SetPaused before the advance, only after). An incorrectly-ordered fix that re-paused before LoadFile would still pass the membership check, so we pin the exact log shape.
        Assert.That(h.PrimaryPlayback.SetPausedCalls, Is.EqualTo(new[] { true }));
        // Secondary was running → coordinator must not touch it at all. LoadFile(startPaused=false) issues pause=no on the real Playback dispatcher, which carries through unmodified.
        Assert.That(h.SecondaryPlayback!.SetPausedCalls, Is.Empty);
    }

    [Test]
    public void NoSelectionTransportFansOutToBoth()
    {
        using var h = new Harness();
        h.EnablePip();
        Assert.That(h.Vm.SelectedSlot, Is.Null, "default after EnablePip is broadcast/sync");

        // Both contexts need a duration > 0 for SeekTo to compute a normalized time.
        h.PrimaryPlayback.DurationSeconds = 60;
        h.SecondaryPlayback!.DurationSeconds = 60;

        // Sync-mode SeekTo: Primary takes the absolute target from the normalized scrubber value; Secondary is absolute-pinned to primaryTarget + offset. EnablePip established offset = 0, so a 0.5 click puts both at 30. The asymmetric-offset test below pins the offset-preserving behavior.
        h.Vm.SeekTo(0.5);
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 30.0 }));
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 30.0 }));

        // SeekRelative stays a relative fan-out (offset-preserving by construction) — both move by the same delta.
        h.Vm.SeekRelative(5);
        Assert.That(h.PrimaryPlayback.SeekRelativeCalls, Is.EqualTo(new[] { 5.0 }));
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls, Is.EqualTo(new[] { 5.0 }));

        h.Vm.StepFrameForward();
        h.Vm.StepFrameBack();
        Assert.That(h.PrimaryPlayback.StepFrameForwardCalls, Is.EqualTo(1));
        Assert.That(h.PrimaryPlayback.StepFrameBackCalls, Is.EqualTo(1));
        Assert.That(h.SecondaryPlayback!.StepFrameForwardCalls, Is.EqualTo(1));
        Assert.That(h.SecondaryPlayback!.StepFrameBackCalls, Is.EqualTo(1));

        // StepChapter fans out too: Primary takes an absolute seek to the chapter target (cue, preroll 0), Secondary is absolute-pinned to target + offset. Primary at position 0 with a chapter at 45s and offset 0 → both seek to 45.
        h.PrimaryPlayback.Chapters = new[] { new MediaChapter(0, "a", 0), new MediaChapter(1, "b", 45) };
        h.Vm.StepChapter(1);
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 30.0, 45.0 }));
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 30.0, 45.0 }));
    }

    [Test]
    public void SyncSeekToAppliesAbsoluteDeltaToSecondary()
    {
        // When the videos have different durations and/or a non-zero established offset, a scrubber click that takes Primary "back N seconds" must take Secondary back the same N seconds — not "back N% of its own duration". The new model pins Secondary to the absolute target primaryTarget + offset, which is offset-preserving by construction.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 600;   // 10:00
        h.SecondaryPlayback!.DurationSeconds = 900; // 15:00, asymmetric on purpose
        // Establish offset = +220 (Secondary 220s ahead): Primary at 8:00, Secondary at 11:40.
        h.EstablishOffset(480, 700);

        // Click takes Primary from 8:00 (480s) to 5:00 (300s). Secondary is absolute-pinned to 300 + 220 = 520 — which is 700 - 180, i.e. the same -180s net move Primary made, NOT 0.5 * 900 = 450.
        h.Vm.SeekTo(0.5);
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 300.0 }));
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 520.0 }), "Secondary absolute-pinned to primaryTarget + offset");
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls, Is.Empty, "Secondary is absolute-pinned, not relatively moved");
    }

    [Test]
    public void SyncStepChapterFromBeforeFirstChapter()
    {
        // Primary's position is BEFORE chapter 0's time (rare — chapters that don't start at 0). ChapterStep handles it: current = -1, +1 → chapter 0's landing (its cue, preroll 0). Primary seeks there; Secondary mirrors the delta.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 300;
        h.SecondaryPlayback!.DurationSeconds = 300;
        h.PrimaryPlayback.Chapters = new[]
        {
            new MediaChapter(0, "Act 1", 30),  // chapter 0 starts at 30s, not 0
            new MediaChapter(1, "Act 2", 120),
        };
        h.PrimaryPlayback.PositionSeconds = 10;  // before chapter 0

        h.Vm.StepChapter(1);   // lands at chapter 0 (time 30). Secondary absolute-pinned to 30 + offset(0) = 30.
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 30.0 }));
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 30.0 }));

        // Step back while still before chapter 0 (position unchanged at 10): nothing earlier exists → true no-op, no seeks added.
        h.Vm.StepChapter(-1);
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 30.0 }), "previous before the first chapter is a no-op");
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 30.0 }));
    }

    [Test]
    public void SyncSeekToBaselinesOffsetOnFirstUseAfterLoad()
    {
        // A file load clears the offset to "pending" (null); the first sync transport that needs it baselines ONCE from the current divergence, then locks it. Here Primary loads a new file (offset cleared) and the streams sit at a +12s divergence; the first SeekTo must adopt offset=12 and absolute-pin Secondary to primaryTarget + 12 — never SeekRelative, never re-read on later seeks.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 100;
        h.SecondaryPlayback!.DurationSeconds = 100;

        // FileLoaded clears the offset to pending.
        h.PrimaryPlayback.RaiseFileLoaded();
        // Streams diverge by +12 at the moment of the first seek.
        h.PrimaryPlayback.PositionSeconds = 8;
        h.SecondaryPlayback!.PositionSeconds = 20;  // offset to be baselined = 20 - 8 = 12

        h.Vm.SeekTo(0.5);  // primaryTarget = 50; Secondary absolute = 50 + 12 = 62
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 50.0 }));
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 62.0 }));
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls, Is.Empty, "Secondary must be absolute-pinned, not relatively moved");

        // Offset is now locked at 12: a second seek pins against the SAME offset, independent of the (unechoed) positions.
        h.Vm.SeekTo(0.3);  // primaryTarget = 30; Secondary absolute = 30 + 12 = 42
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 50.0, 30.0 }));
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 62.0, 42.0 }));
    }

    [Test]
    public void SyncTransportNeverChangesEstablishedOffset()
    {
        // The headline invariant: once an offset is established, NO sync-mode transport (SeekTo, SeekRelative, StepChapter, StepFrame) may change it. Only an intentional asymmetric adjustment (an isolated transport action, or a differed-pair converge) does. We establish offset = 5, run every transport kind, then prove the offset survived by checking a follow-up correction still defends 5.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 200;
        h.SecondaryPlayback!.DurationSeconds = 200;
        h.PrimaryPlayback.VideoFps = 30;
        h.SecondaryPlayback!.VideoFps = 30;
        h.PrimaryPlayback.Chapters = new[] { new MediaChapter(0, "a", 0), new MediaChapter(1, "b", 80) };

        // Establish offset = 5 (Secondary 5s ahead).
        h.EstablishOffset(0, 5);

        // Now simulate the streams drifting during playback so the LIVE divergence (12) differs from the STORED offset (5). If any seek re-derived the offset from live positions instead of reading the stored value, the assertions below would see primaryTarget + 12, not + 5 — this is what pins "stored, not re-derived".
        h.PrimaryPlayback.PositionSeconds = 40;
        h.SecondaryPlayback!.PositionSeconds = 52;  // live divergence = 12 ≠ stored offset 5

        // Every sync transport kind. Each absolute seek must pin Secondary to primaryTarget + 5 (the stored offset), never primaryTarget + 12 (the live divergence).
        h.Vm.SeekTo(0.25);  // primaryTarget = 50; Secondary = 55, NOT 62
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 55.0 }), "absolute pin uses the stored offset (5), not the live divergence (12)");
        h.Vm.SeekRelative(10);    // relative fan-out, offset-preserving
        h.Vm.StepChapter(1);      // chapter 1 at 80; Secondary = 85, NOT 92
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 55.0, 85.0 }), "chapter pin uses the stored offset (5), not the live divergence (12)");
        h.Vm.StepFrameForward();  // frame-step both, equal fps → no correction

        // The offset must still be 5. Introduce a > 1 s drift so the controller hard-resyncs and confirm it defends 5 (seek to Primary.Pos + 5), not some shifted value. IsCoreIdle=false marks both streams as actually advancing — the shared fake faithfully defaults to mpv's pre-load core-idle=true, which would gate the correction.
        h.PrimaryPlayback.IsPaused = false;
        h.SecondaryPlayback!.IsPaused = false;
        h.PrimaryPlayback.IsCoreIdle = false;
        h.SecondaryPlayback!.IsCoreIdle = false;
        h.PrimaryPlayback.PositionSeconds = 100;
        h.SecondaryPlayback!.PositionSeconds = 106.5;  // drift = (106.5 - 100) - 5 = 1.5 > hard-resync threshold
        h.SecondaryPlayback!.SeekCalls.Clear();
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 105.0 }), "offset unchanged at 5 → hard resync targets Primary.Pos + 5 = 105");
    }

    [Test]
    public void SyncStepChapterAppliesAbsoluteDeltaToSecondary()
    {
        // Per-context StepChapter would advance each video to its own next chapter — chapter timestamps differ wildly between videos (one per scene vs. one per act, etc.) so the streams drift apart. Rule: seek Primary to its chapter target and absolute-pin Secondary to target + offset, so Secondary tracks Primary's move rather than its own chapter grid.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 300;
        h.SecondaryPlayback!.DurationSeconds = 300;
        h.PrimaryPlayback.Chapters = new[]
        {
            new MediaChapter(0, "Intro", 0),
            new MediaChapter(1, "Act 1", 60),
            new MediaChapter(2, "Act 2", 180),
        };
        // Establish offset = +65: Primary at 30 (in Intro, chapter 0), Secondary 65s ahead at 95.
        h.EstablishOffset(30, 95);

        // Step +1 → Primary's target is chapter 1 at 60s; Secondary is absolute-pinned to 60 + 65 = 125 (= 95 + Primary's +30 move), NOT to its own chapter 1.
        h.Vm.StepChapter(1);
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 60.0 }));
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 125.0 }), "Secondary absolute-pinned to target + offset, not its own chapter step");
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls, Is.Empty);
    }

    // SeekToChapter routes through SeekTo's normalized contract (divide-by-then-multiply-by the same duration), so a tiny FP tolerance is used; the real seek is F3-formatted at the mpv boundary anyway.

    [Test]
    public void SeekToChapterLandsOnCueWithDefaultPreroll()
    {
        using var h = new Harness();
        h.PrimaryPlayback.DurationSeconds = 300;
        h.PrimaryPlayback.Chapters = new[] { new MediaChapter(0, "a", 0), new MediaChapter(1, "b", 120) };
        h.Vm.SeekToChapter(120);
        Assert.That(h.PrimaryPlayback.SeekCalls.Count, Is.EqualTo(1));
        Assert.That(h.PrimaryPlayback.SeekCalls[0], Is.EqualTo(120.0).Within(1e-6));
    }

    [Test]
    public void SeekToChapterAppliesPreroll()
    {
        using var h = new Harness();
        h.Vm.ChapterSeekPrerollSeconds = 5;
        h.PrimaryPlayback.DurationSeconds = 300;
        h.PrimaryPlayback.Chapters = new[] { new MediaChapter(0, "a", 0), new MediaChapter(1, "b", 120) };
        h.Vm.SeekToChapter(120);   // 120 - 5 = 115 (previous-cue floor of 0 doesn't bind)
        Assert.That(h.PrimaryPlayback.SeekCalls[0], Is.EqualTo(115.0).Within(1e-6));
    }

    [Test]
    public void SeekToChapterClampsPrerollAtZeroForEarlyChapter()
    {
        using var h = new Harness();
        h.Vm.ChapterSeekPrerollSeconds = 10;
        h.PrimaryPlayback.DurationSeconds = 300;
        h.PrimaryPlayback.Chapters = new[] { new MediaChapter(0, "a", 5), new MediaChapter(1, "b", 120) };
        h.Vm.SeekToChapter(5);     // 5 - 10 < 0 → floored at 0
        Assert.That(h.PrimaryPlayback.SeekCalls[0], Is.EqualTo(0.0).Within(1e-6));
    }

    [Test]
    public void SeekToChapterInSyncModeAppliesPrerollAndFansOutToSecondary()
    {
        // Marker click in PiP sync mode routes through SeekTo: Primary takes the prerolled absolute target, Secondary is absolute-pinned to target + offset — same offset-preserving contract as any sync-mode absolute seek.
        using var h = new Harness();
        h.EnablePip();
        h.Vm.ChapterSeekPrerollSeconds = 5;
        h.PrimaryPlayback.DurationSeconds = 300;
        h.SecondaryPlayback!.DurationSeconds = 300;
        h.PrimaryPlayback.Chapters = new[] { new MediaChapter(0, "a", 0), new MediaChapter(1, "b", 120) };
        // Establish offset = -20: Secondary 20s behind Primary.
        h.EstablishOffset(20, 0);

        h.Vm.SeekToChapter(120);   // target 120 - 5 = 115; Secondary absolute-pinned to 115 + (-20) = 95
        Assert.That(h.PrimaryPlayback.SeekCalls.Count, Is.EqualTo(1));
        Assert.That(h.PrimaryPlayback.SeekCalls[0], Is.EqualTo(115.0).Within(1e-6));
        Assert.That(h.SecondaryPlayback!.SeekCalls.Count, Is.EqualTo(1));
        Assert.That(h.SecondaryPlayback!.SeekCalls[0], Is.EqualTo(95.0).Within(1e-6));
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls, Is.Empty);
    }

    [Test]
    public void StepChapterSingleVideoAppliesPreroll()
    {
        using var h = new Harness();
        h.Vm.ChapterSeekPrerollSeconds = 5;
        h.PrimaryPlayback.DurationSeconds = 300;
        h.PrimaryPlayback.Chapters = new[] { new MediaChapter(0, "a", 0), new MediaChapter(1, "b", 60), new MediaChapter(2, "c", 180) };
        h.PrimaryPlayback.PositionSeconds = 30;   // in chapter 0
        h.Vm.StepChapter(1);   // → chapter 1 at 60, preroll 5 → 55 (seeks the computed target directly, no round-trip)
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 55.0 }));
    }

    [Test]
    public void StepChapterAdvancesFromSubFrameLandingWhilePaused()
    {
        // Regression for the paused "forward-forward sticks" bug: mpv's exact seek to a cue lands on the frame at-or-just-below it, so the echoed position is a sub-frame below the target. A follow-up "next" must still advance rather than recompute (and re-cue) the same chapter. (While playing this never showed because playback advances past the cue first.)
        using var h = new Harness();
        h.PrimaryPlayback.DurationSeconds = 300;
        h.PrimaryPlayback.Chapters = new[] { new MediaChapter(0, "a", 0), new MediaChapter(1, "b", 60), new MediaChapter(2, "c", 120) };
        h.PrimaryPlayback.PositionSeconds = 10;
        h.Vm.StepChapter(1);                        // → 60
        h.PrimaryPlayback.PositionSeconds = 59.96;  // mpv's exact-seek landing, echoed back while paused
        h.Vm.StepChapter(1);                        // must advance to 120, not re-cue 60
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 60.0, 120.0 }));
    }

    [Test]
    public void SyncStepChapterAppliesPreroll()
    {
        using var h = new Harness();
        h.EnablePip();
        h.Vm.ChapterSeekPrerollSeconds = 5;
        h.PrimaryPlayback.DurationSeconds = 300;
        h.SecondaryPlayback!.DurationSeconds = 300;
        h.PrimaryPlayback.Chapters = new[] { new MediaChapter(0, "a", 0), new MediaChapter(1, "b", 60), new MediaChapter(2, "c", 180) };
        h.PrimaryPlayback.PositionSeconds = 30;   // in chapter 0 (offset 0 from EnablePip, both aligned)
        h.Vm.StepChapter(1);   // Primary target chapter 1 (60) - preroll 5 = 55; Secondary absolute-pinned to 55 + 0 = 55
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.EqualTo(new[] { 55.0 }));
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 55.0 }));
    }

    [Test]
    public void SyncStepChapterPastLastChapterIsNoOp()
    {
        // Stepping past the last chapter has nowhere to go: ChapterStep returns null → true no-op. Neither context moves (no mpv-clamp fan-out), and the sync offset is left untouched.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 300;
        h.SecondaryPlayback!.DurationSeconds = 300;
        h.PrimaryPlayback.Chapters = new[]
        {
            new MediaChapter(0, "Intro", 0),
            new MediaChapter(1, "Outro", 200),
        };
        h.PrimaryPlayback.PositionSeconds = 250;  // currently in Outro (chapter 1, the last)

        h.Vm.StepChapter(1);   // past the end
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls, Is.Empty);
    }

    [Test]
    public void SyncStepFrameAppliesAbsoluteDeltaToSecondary()
    {
        // Per-context frame-step advances each video by 1/fps_video — when fps differs (Primary 24, Secondary 60), the videos drift relative to each other by ~25 ms per step. Rule: frame-step both (atomic-pause-and-step on each, no SetPaused/SeekRelative race window where Secondary could decode extra frames between two dispatcher commands), then apply a *correction* SeekRelative on Secondary equal to Primary.frameDuration - Secondary.frameDuration, so Secondary's net move equals Primary's frame duration regardless of fps mismatch.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 60;
        h.SecondaryPlayback!.DurationSeconds = 60;
        h.PrimaryPlayback.VideoFps = 24;
        h.SecondaryPlayback!.VideoFps = 60;

        h.Vm.StepFrameForward();
        Assert.That(h.PrimaryPlayback.StepFrameForwardCalls, Is.EqualTo(1));
        Assert.That(h.SecondaryPlayback!.StepFrameForwardCalls, Is.EqualTo(1), "Secondary frame-steps too — atomic pause-and-step on its end avoids the SetPaused/SeekRelative dispatcher race");
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls.Count, Is.EqualTo(1));
        // Correction = Primary.frame - Secondary.frame = 1/24 - 1/60. Net Secondary move = (its own 1/60 frame) + (1/24 - 1/60 correction) = 1/24 = Primary.frame. ✓
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls[0], Is.EqualTo(1.0 / 24 - 1.0 / 60).Within(1e-9));

        h.Vm.StepFrameBack();
        Assert.That(h.PrimaryPlayback.StepFrameBackCalls, Is.EqualTo(1));
        Assert.That(h.SecondaryPlayback!.StepFrameBackCalls, Is.EqualTo(1));
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls.Count, Is.EqualTo(2));
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls[1], Is.EqualTo(-1.0 / 24 - -1.0 / 60).Within(1e-9));
    }

    [Test]
    public void SyncStepFrameWithEqualFpsSkipsCorrection()
    {
        // When Primary and Secondary share fps, the correction is exactly 0 — emitting a no-op SeekRelative would still be safe but pollutes the dispatcher; verify we elide it.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 60;
        h.SecondaryPlayback!.DurationSeconds = 60;
        h.PrimaryPlayback.VideoFps = 30;
        h.SecondaryPlayback!.VideoFps = 30;

        h.Vm.StepFrameForward();
        Assert.That(h.PrimaryPlayback.StepFrameForwardCalls, Is.EqualTo(1));
        Assert.That(h.SecondaryPlayback!.StepFrameForwardCalls, Is.EqualTo(1));
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls, Is.Empty);
    }

    [Test]
    public void SyncStepFrameWithoutPrimaryFpsFallsBackToFanOut()
    {
        // When Primary has no fps (audio-only / pre-load / VFR with no container-fps), the absolute-seconds delta of one frame is unknown. Fall back to per-context StepFrame — drift is then bounded to ~one frame per step, matching pre-fix behavior. Same fallback exists in production for sources that don't report container-fps.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 60;
        h.SecondaryPlayback!.DurationSeconds = 60;
        // Both fps null by default — leaving as-is.

        h.Vm.StepFrameForward();
        Assert.That(h.PrimaryPlayback.StepFrameForwardCalls, Is.EqualTo(1));
        Assert.That(h.SecondaryPlayback!.StepFrameForwardCalls, Is.EqualTo(1), "no fps → fan-out");
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SetPausedCalls, Is.Empty);
    }

    [Test]
    public void SelectedSecondaryRoutesTransportToSecondaryOnly()
    {
        using var h = new Harness();
        h.EnablePip();
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        Assert.That(h.Vm.SelectedSlot, Is.EqualTo(ViewModelMain.VideoSlot.Secondary));

        h.PrimaryPlayback.DurationSeconds = 60;
        h.SecondaryPlayback!.DurationSeconds = 60;

        h.Vm.SeekTo(0.25);
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 15.0 }));

        h.Vm.StepFrameForward();
        Assert.That(h.PrimaryPlayback.StepFrameForwardCalls, Is.EqualTo(0));
        Assert.That(h.SecondaryPlayback!.StepFrameForwardCalls, Is.EqualTo(1));
    }

    [Test]
    public void SelectedSecondaryRoutesPlayPauseToSecondaryOnly()
    {
        // PlayPause with a selection is isolated: only the selected stream toggles. Primary's pause state is untouched. This is the "cue PiP without disturbing primary" workflow.
        using var h = new Harness();
        h.EnablePip();
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.PrimaryPlayback.DurationSeconds = 60;
        h.SecondaryPlayback!.DurationSeconds = 60;

        h.Vm.PlayPauseCommand.Execute(null);
        Assert.That(h.PrimaryPlayback.TogglePauseCalls, Is.EqualTo(0), "primary not toggled");
        Assert.That(h.PrimaryPlayback.SetPausedCalls, Is.Empty, "primary not set");
        Assert.That(h.SecondaryPlayback!.TogglePauseCalls, Is.EqualTo(1), "secondary toggled");
    }

    [Test]
    public void NoSelectionPlayPauseSyncsBothFromPrimaryFlip()
    {
        // When no selection and both already in sync: PlayPause sync-broadcasts SetPaused(target) on every stream, target = !Primary.IsPaused. Both streams converge to one state.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 60;
        h.SecondaryPlayback!.DurationSeconds = 60;
        // Both start IsPaused=true (default). Primary's flip → target=false (play).
        h.Vm.PlayPauseCommand.Execute(null);
        Assert.That(h.PrimaryPlayback.SetPausedCalls, Is.EqualTo(new[] { false }));
        Assert.That(h.SecondaryPlayback!.SetPausedCalls, Is.EqualTo(new[] { false }));
        Assert.That(h.PrimaryPlayback.IsPaused, Is.False);
        Assert.That(h.SecondaryPlayback!.IsPaused, Is.False);

        // Hit again: Primary flips to true, both converge to true.
        h.Vm.PlayPauseCommand.Execute(null);
        Assert.That(h.PrimaryPlayback.SetPausedCalls, Is.EqualTo(new[] { false, true }));
        Assert.That(h.SecondaryPlayback!.SetPausedCalls, Is.EqualTo(new[] { false, true }));
    }

    [Test]
    public void NoSelectionPlayPauseConvergesDriftedStreams()
    {
        // Drift scenario: user previously had Secondary selected and toggled it independently, leaving Primary playing + Secondary paused (or vice versa). Then they deselect and hit Space — both must converge to the same state. Tiebreaker: Primary's flip drives, so all streams end at NOT(primary's previous state).
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 60;
        h.SecondaryPlayback!.DurationSeconds = 60;
        h.PrimaryPlayback.IsPaused = false;       // primary playing
        h.SecondaryPlayback!.IsPaused = true;     // secondary paused — drift
        // No selection (broadcast/sync). PlayPause: target = !Primary.IsPaused = true → both end paused.
        h.Vm.PlayPauseCommand.Execute(null);
        Assert.That(h.PrimaryPlayback.SetPausedCalls, Is.EqualTo(new[] { true }));
        Assert.That(h.SecondaryPlayback!.SetPausedCalls, Is.EqualTo(new[] { true }));
        Assert.That(h.PrimaryPlayback.IsPaused, Is.True);
        Assert.That(h.SecondaryPlayback!.IsPaused, Is.True);

        // Inverse drift: Primary paused, Secondary playing. PlayPause → target=false → both play.
        h.PrimaryPlayback.SetPausedCalls.Clear();
        h.SecondaryPlayback!.SetPausedCalls.Clear();
        h.PrimaryPlayback.IsPaused = true;
        h.SecondaryPlayback!.IsPaused = false;
        h.Vm.PlayPauseCommand.Execute(null);
        Assert.That(h.PrimaryPlayback.SetPausedCalls, Is.EqualTo(new[] { false }));
        Assert.That(h.SecondaryPlayback!.SetPausedCalls, Is.EqualTo(new[] { false }));
    }

    [Test]
    public void PlayPauseConvergingDifferedStreamsSetsSyncPoint()
    {
        // Repro: user plays one track alone (isolated), leaves the pair in differed play states, then hits Space in sync mode. The already-playing track drifted since the offset was last established, so the stored offset is stale. Bringing the two streams to a common play state via Space only actually STARTS one of them (the other was already playing) — an intentional asymmetric action: capture the offset from the live divergence.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 60;
        h.SecondaryPlayback!.DurationSeconds = 60;

        // A stale established offset of 0 (both were at 0 then).
        h.EstablishOffset(0, 0);

        // Streams now in DIFFERENT play states: Secondary has been playing and drifted to 40, Primary is paused at 10.
        h.PrimaryPlayback.IsPaused = true;
        h.PrimaryPlayback.PositionSeconds = 10;
        h.SecondaryPlayback!.IsPaused = false;
        h.SecondaryPlayback!.PositionSeconds = 40;

        h.Vm.PlayPauseCommand.Execute(null);  // target = !Primary.IsPaused = play; differed states → sync point

        // Both converge to playing.
        Assert.That(h.PrimaryPlayback.SetPausedCalls, Is.EqualTo(new[] { false }));
        Assert.That(h.SecondaryPlayback!.SetPausedCalls, Is.EqualTo(new[] { false }));

        // The sync point is now the live divergence (40 - 10 = 30), not the stale 0. Prove it: a > 1 s drift hard-resyncs against offset 30. Both streams are advancing post-converge — clear the fake's faithful core-idle default so the correction gate opens.
        h.PrimaryPlayback.IsCoreIdle = false;
        h.SecondaryPlayback!.IsCoreIdle = false;
        h.PrimaryPlayback.PositionSeconds = 20;
        h.SecondaryPlayback!.PositionSeconds = 51.5;  // divergence 31.5 vs offset 30 → drift 1.5 > hard-resync threshold
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 50.0 }), "fresh sync point = 30 → hard resync targets Primary.Pos + 30 = 50");
    }

    [Test]
    public void PlayPauseStartingBothFromSameStateDefendsExistingOffset()
    {
        // Counterpart to the differed-state test: when both streams start in the SAME play state (both paused) and Space starts them together, that's a genuine both-track action — it must NOT recapture the offset (which would bake in prior drift). The continuous controller absorbs startup skew against the established offset.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 60;
        h.SecondaryPlayback!.DurationSeconds = 60;

        // Establish offset = 5, then pause both.
        h.EstablishOffset(0, 5);
        h.PrimaryPlayback.IsPaused = true;
        h.SecondaryPlayback!.IsPaused = true;

        h.Vm.PlayPauseCommand.Execute(null);  // both paused (same state) → play together, offset defended

        // Offset must still be the established 5 (not recaptured). A > 1 s drift hard-resyncs to Primary.Pos + 5. Both streams are advancing now — clear the fake's faithful core-idle default so the correction gate opens.
        h.PrimaryPlayback.IsPaused = false;
        h.SecondaryPlayback!.IsPaused = false;
        h.PrimaryPlayback.IsCoreIdle = false;
        h.SecondaryPlayback!.IsCoreIdle = false;
        h.PrimaryPlayback.PositionSeconds = 30;
        h.SecondaryPlayback!.PositionSeconds = 36.5;  // drift = (36.5 - 30) - 5 = 1.5 > hard-resync threshold
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 35.0 }), "offset unchanged at 5 → hard resync targets 30 + 5 = 35");
    }

    [Test]
    public void SyncPauseTogetherPreservesEstablishedOffset()
    {
        // The literal reported bug: both streams playing in sync with an established offset, user pauses them together — the offset must not move. Pausing gates the drift controller (core-idle), so the diagnostic read is the authoritative assert here.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.EstablishOffset(0, 5);

        h.Vm.PlayPauseCommand.Execute(null);  // play/play → pause/pause

        Assert.That(h.PrimaryPlayback.SetPausedCalls, Is.EqualTo(new[] { true }));
        Assert.That(h.SecondaryPlayback!.SetPausedCalls, Is.EqualTo(new[] { true }));
        Assert.That(h.Vm.GetSyncDiagnostic().TargetOffsetSeconds!.Value, Is.EqualTo(5.0).Within(1e-9), "pausing together must not move the sync offset");
    }

    [Test]
    public void PlayPauseConvergingDifferedStreamsToPauseAlsoSetsSyncPoint()
    {
        // The pause-direction converge: Primary playing, Secondary already paused → Space pauses the pair. Only Primary actually changed state — the same asymmetric "join" as the play direction, capturing the offset from the live (on-screen) divergence.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 60;
        h.SecondaryPlayback!.DurationSeconds = 60;
        h.EstablishOffset(0, 0);  // stale established offset

        h.PrimaryPlayback.IsPaused = false;
        h.PrimaryPlayback.PositionSeconds = 20;
        h.SecondaryPlayback!.IsPaused = true;
        h.SecondaryPlayback!.PositionSeconds = 8;  // frozen while paused; live divergence = -12

        h.Vm.PlayPauseCommand.Execute(null);  // target = !Primary.IsPaused = pause both

        Assert.That(h.PrimaryPlayback.SetPausedCalls, Is.EqualTo(new[] { true }));
        Assert.That(h.SecondaryPlayback!.SetPausedCalls, Is.EqualTo(new[] { true }));
        Assert.That(h.Vm.GetSyncDiagnostic().TargetOffsetSeconds!.Value, Is.EqualTo(-12.0).Within(1e-9), "the joining pause captures the on-screen arrangement");
    }

    [Test]
    public void IsolatedTransportOnFilelessStreamDoesNotVoidOffset()
    {
        // The isolated void is gated on the selected stream actually having a file: on a fileless stream every underlying transport is a gated no-op, and a true no-op must not invalidate the sync (same principle as the chapterless StepChapter).
        using var h = new Harness();
        h.EnablePip();  // offset = 0 established
        h.PrimaryPlayback.DurationSeconds = 60;  // Secondary stays fileless (Duration 0)
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);

        h.Vm.PlayPauseCommand.Execute(null);
        h.Vm.SeekTo(0.5);
        h.Vm.SeekRelative(10);
        h.Vm.StepFrameForward();

        Assert.That(h.Vm.GetSyncDiagnostic().TargetOffsetSeconds!.Value, Is.EqualTo(0.0).Within(1e-9), "gated no-ops on a fileless stream must not void the offset");
    }

    [Test]
    public void SelectionRoundTripPreservesEstablishedOffset()
    {
        // Selection is orthogonal to the sync offset: a select/deselect round-trip with no isolated transport action in between must leave the established offset untouched — even if the streams drifted while isolated (the controller is gated then). This is also the fullscreen controls auto-hide path, which deselects via a TIMER (MainWindow's OnControlsHideTimeout → SetSelected(null)): a timer must never be able to move the user's sync.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.EstablishOffset(0, 5);

        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.SecondaryPlayback!.PositionSeconds = 5.3;  // decoder drift while isolated — no VM command issued
        h.Vm.SetSelected(null);

        Assert.That(h.Vm.GetSyncDiagnostic().TargetOffsetSeconds!.Value, Is.EqualTo(5.0).Within(1e-9), "deselect must not recapture from the drifted positions");
        // The 0.3 s drift against the preserved offset is corrected, not adopted as the new target.
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 0.95 }).Within(1e-9), "controller defends 5, catching the isolated-era drift");
    }

    [Test]
    public void IsolatedPlayPauseClearsOffsetToPending()
    {
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.EstablishOffset(0, 5);
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        Assert.That(h.Vm.GetSyncDiagnostic().TargetOffsetSeconds, Is.Not.Null, "selection alone must not invalidate the offset");

        h.Vm.PlayPauseCommand.Execute(null);

        Assert.That(h.Vm.GetSyncDiagnostic().TargetOffsetSeconds, Is.Null, "an isolated pause/play is an asymmetric adjustment — the old offset is void");
    }

    [Test]
    public void IsolatedSeekToClearsOffsetToPendingAndRebaselinesInSync()
    {
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.EstablishOffset(0, 5);
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        Assert.That(h.Vm.GetSyncDiagnostic().TargetOffsetSeconds, Is.Not.Null, "selection alone must not invalidate the offset");

        h.Vm.SeekTo(0.5);  // isolated: seeks Secondary alone to 30
        Assert.That(h.Vm.GetSyncDiagnostic().TargetOffsetSeconds, Is.Null, "an isolated seek is an asymmetric adjustment — the old offset is void");

        // Back in sync, the first both-advancing tick baselines from the adjusted divergence.
        h.Vm.SetSelected(null);
        h.PrimaryPlayback.PositionSeconds = 0;
        h.SecondaryPlayback!.PositionSeconds = 7;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.Vm.GetSyncDiagnostic().TargetOffsetSeconds!.Value, Is.EqualTo(7.0).Within(1e-9), "pending re-baselines from the post-adjustment divergence");
        // And the new offset is defended.
        h.PrimaryPlayback.PositionSeconds = 10;
        h.SecondaryPlayback!.PositionSeconds = 17.3;  // drift = (17.3 - 10) - 7 = +0.3
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 0.95 }).Within(1e-9));
    }

    [Test]
    public void IsolatedSeekRelativeClearsOffsetToPending()
    {
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.EstablishOffset(0, 5);
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        Assert.That(h.Vm.GetSyncDiagnostic().TargetOffsetSeconds, Is.Not.Null, "selection alone must not invalidate the offset");

        h.Vm.SeekRelative(10);

        Assert.That(h.Vm.GetSyncDiagnostic().TargetOffsetSeconds, Is.Null, "an isolated relative seek is an asymmetric adjustment — the old offset is void");
        Assert.That(h.SecondaryPlayback!.SeekRelativeCalls, Is.EqualTo(new[] { 10.0 }));
        Assert.That(h.PrimaryPlayback.SeekRelativeCalls, Is.Empty);
    }

    [Test]
    public void IsolatedStepFrameClearsOffsetToPending()
    {
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.EstablishOffset(0, 5);
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        Assert.That(h.Vm.GetSyncDiagnostic().TargetOffsetSeconds, Is.Not.Null, "selection alone must not invalidate the offset");

        h.Vm.StepFrameForward();

        Assert.That(h.Vm.GetSyncDiagnostic().TargetOffsetSeconds, Is.Null, "an isolated frame-step is an asymmetric adjustment — the old offset is void");
    }

    [Test]
    public void IsolatedStepChapterClearsOffsetToPending()
    {
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.SecondaryPlayback!.Chapters = new[] { new MediaChapter(0, "a", 0), new MediaChapter(1, "b", 60) };
        h.EstablishOffset(0, 5);
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        Assert.That(h.Vm.GetSyncDiagnostic().TargetOffsetSeconds, Is.Not.Null, "selection alone must not invalidate the offset");

        h.Vm.StepChapter(1);  // Secondary at 5 → its chapter 1 at 60

        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 60.0 }));
        Assert.That(h.Vm.GetSyncDiagnostic().TargetOffsetSeconds, Is.Null, "an isolated chapter step is an asymmetric adjustment — the old offset is void");
    }

    [Test]
    public void IsolatedStepChapterWithoutChaptersLeavesOffsetEstablished()
    {
        // The chapterless resolver returns null → nothing moved → a true no-op. An operation that did nothing must not invalidate the offset (a pending re-baseline would silently absorb the live drift into the target).
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.EstablishOffset(0, 5);
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);

        h.Vm.StepChapter(1);  // Secondary has no chapters → true no-op

        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
        Assert.That(h.Vm.GetSyncDiagnostic().TargetOffsetSeconds!.Value, Is.EqualTo(5.0).Within(1e-9), "a true no-op must not invalidate the offset");
    }

    [Test]
    public void SyncPlayPauseResolvesPendingOffsetAtGestureTime()
    {
        // The both-paused cueing endgame: after isolated adjustments the offset is pending and both streams sit frozen at the cued positions. The same-state Space that starts them together must resolve the offset THEN — from the exact frozen positions — not at the first both-advancing tick, which would bake each decoder's startup skew into the target the controller is supposed to correct.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.DurationSeconds = 60;
        h.SecondaryPlayback!.DurationSeconds = 60;
        h.SecondaryPlayback!.RaiseFileLoaded();  // offset → pending
        h.PrimaryPlayback.IsPaused = true;
        h.SecondaryPlayback!.IsPaused = true;
        h.PrimaryPlayback.PositionSeconds = 10;
        h.SecondaryPlayback!.PositionSeconds = 17;  // frozen cued divergence = 7

        h.Vm.PlayPauseCommand.Execute(null);  // pause/pause → play/play

        Assert.That(h.Vm.GetSyncDiagnostic().TargetOffsetSeconds!.Value, Is.EqualTo(7.0).Within(1e-9), "pending resolves from the frozen positions at the gesture");

        // Post-play decoder startup skew is corrected toward 7, not adopted as the target.
        h.PrimaryPlayback.IsCoreIdle = false;
        h.SecondaryPlayback!.IsCoreIdle = false;
        h.PrimaryPlayback.PositionSeconds = 11;
        h.SecondaryPlayback!.PositionSeconds = 17.9;  // drift = (17.9 - 11) - 7 = -0.1
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 1.05 }).Within(1e-9), "startup skew corrected against the gesture-time offset");
    }

    [Test]
    public void PerVideoCommandsTargetSingleTarget()
    {
        // Volume / Mute always target SingleTarget (Selected ?? Primary), regardless of selection-vs-broadcast state.
        using var h = new Harness();
        h.EnablePip();
        Assert.That(h.Vm.SelectedSlot, Is.Null);

        h.Vm.SetVolume(50);
        Assert.That(h.PrimaryPlayback.SetVolumeCalls, Is.EqualTo(new[] { 50.0 }));
        Assert.That(h.SecondaryPlayback!.SetVolumeCalls, Is.Empty);

        h.Vm.ToggleMute();
        Assert.That(h.PrimaryPlayback.ToggleMuteCalls, Is.EqualTo(1));
        Assert.That(h.SecondaryPlayback!.ToggleMuteCalls, Is.EqualTo(0));

        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.Vm.SetVolume(25);
        Assert.That(h.PrimaryPlayback.SetVolumeCalls, Is.EqualTo(new[] { 50.0 }), "primary unaffected after selection swap");
        Assert.That(h.SecondaryPlayback!.SetVolumeCalls, Is.EqualTo(new[] { 25.0 }));

        h.Vm.ToggleMute();
        Assert.That(h.PrimaryPlayback.ToggleMuteCalls, Is.EqualTo(1));
        Assert.That(h.SecondaryPlayback!.ToggleMuteCalls, Is.EqualTo(1));
    }

    [Test]
    public void OpenFileTargetsSingleTarget()
    {
        using var h = new Harness();
        h.EnablePip();
        h.Vm.OpenFile("/primary-only.mp4");
        Assert.That(h.PrimaryPlayback.LoadedFiles, Is.EqualTo(new[] { "/primary-only.mp4" }));
        Assert.That(h.SecondaryPlayback!.LoadedFiles, Is.Empty);

        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.Vm.OpenFile("/secondary-only.mp4");
        Assert.That(h.PrimaryPlayback.LoadedFiles, Is.EqualTo(new[] { "/primary-only.mp4" }));
        Assert.That(h.SecondaryPlayback!.LoadedFiles, Is.EqualTo(new[] { "/secondary-only.mp4" }));
    }

    [Test]
    public void DisablePipDisposesSecondaryAndResumesPrimaryAutoAdvance()
    {
        using var h = new Harness();
        h.Vm.LoadPaths(new[] { "/a1.mp4", "/a2.mp4" }, replace: true);
        h.EnablePip();
        Assert.That(h.Vm.Primary.AutoAdvanceEnabled, Is.False, "primary auto-advance suppressed during PiP");

        h.Vm.DisablePip();
        Assert.That(h.Vm.Secondary, Is.Null);
        Assert.That(h.Vm.IsPipEnabled, Is.False);
        Assert.That(h.Vm.Primary.AutoAdvanceEnabled, Is.True, "primary auto-advance restored");

        // Primary's auto-advance must work again — drive it to file EOF and watch the per-context handler advance.
        Harness.ReachFileEof(h.Vm.Primary, h.PrimaryPlayback);
        Assert.That(h.Vm.Primary.Playlist.CurrentIndex, Is.EqualTo(1));
    }

    [Test]
    public void EnableDisableEnableCyclePreservesNoState()
    {
        using var h = new Harness();
        h.EnablePip();
        h.Vm.Secondary!.LoadPaths(new[] { "/old.mp4" }, replace: true);
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);

        h.Vm.DisablePip();
        Assert.That(h.Vm.SelectedSlot, Is.Null, "DisablePip resets selection to broadcast/sync");

        // Re-enable: must be a fresh secondary (different instance, empty playlist, no preserved selection state).
        h.EnablePip();
        Assert.That(h.Vm.Secondary!.Playlist.Items, Is.Empty, "fresh secondary has empty playlist");
        Assert.That(h.Vm.SelectedSlot, Is.Null, "selection stays null on re-enable");
    }

    [Test]
    public void SetSelectedSecondaryWhileSecondaryNullSnapsToNull()
    {
        using var h = new Harness();
        // PiP off — Secondary doesn't exist; SetSelected(Secondary) coerces to null.
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        Assert.That(h.Vm.SelectedSlot, Is.Null);
    }

    [Test]
    public void SelectionSwapRefiresProxyPropertiesSoViewReadsTarget()
    {
        using var h = new Harness();
        h.PrimaryPlayback.Volume = 80;
        h.EnablePip();
        h.SecondaryPlayback!.Volume = 50;
        // Capture VM-level PropertyChanged for Volume and the read-back via the proxy property.
        var observedVolumes = new List<double>();
        h.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModelMain.Volume))
            {
                observedVolumes.Add(h.Vm.Volume);
            }
        };

        // Selection swap to Secondary should fire Volume PropertyChanged so the view re-reads — and reads Secondary's 50, not Primary's 80.
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        Assert.That(observedVolumes, Does.Contain(50.0), "view received Secondary's volume on selection swap");
        Assert.That(h.Vm.Volume, Is.EqualTo(50.0), "proxy reads from selected target");

        // Swap back to null (broadcast/sync, default target = Primary): view must see Primary's value again.
        observedVolumes.Clear();
        h.Vm.SetSelected(null);
        Assert.That(observedVolumes, Does.Contain(80.0));
        Assert.That(h.Vm.Volume, Is.EqualTo(80.0));
    }

    [Test]
    public void OffTargetContextChangesAreNotForwardedToView()
    {
        // Volume change on Secondary while no selection (target=Primary) must NOT fire VM PropertyChanged for Volume — the view shouldn't see the off-target context's churn.
        using var h = new Harness();
        h.EnablePip();
        // SelectedSlot=null ⇒ target=Primary.
        var observedVolumeChanges = 0;
        h.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModelMain.Volume))
            {
                observedVolumeChanges++;
            }
        };

        // Mutate Secondary's volume — view should not react.
        h.SecondaryPlayback!.Volume = 25;
        Assert.That(observedVolumeChanges, Is.EqualTo(0), "off-target volume change is filtered");
    }

    [Test]
    public void DisablePipWithPrimaryAtEofAdvancesPrimary()
    {
        // Scenario: in PiP mode the pair has been parked at file-EOF (lockstep). User toggles PiP off. Primary should immediately advance to its next item rather than getting stuck at EOF.
        using var h = new Harness();
        h.Vm.LoadPaths(new[] { "/a1.mp4", "/a2.mp4" }, replace: true);
        h.EnablePip();
        h.Vm.Secondary!.LoadPaths(new[] { "/b.mp4" }, replace: true);  // Secondary has no next.
        Harness.ReachFileEof(h.Vm.Primary, h.PrimaryPlayback);
        Harness.ReachFileEof(h.Vm.Secondary!, h.SecondaryPlayback!);
        // Lockstep halts the pair (Secondary at end) — Primary is parked at EOF.
        Assert.That(h.Vm.Primary.Playlist.CurrentIndex, Is.EqualTo(0));

        h.Vm.DisablePip();
        Assert.That(h.Vm.Primary.Playlist.CurrentIndex, Is.EqualTo(1), "Primary advances on PiP-off when at EOF + has next");
        Assert.That(h.PrimaryPlayback.LoadedFiles, Is.EqualTo(new[] { "/a1.mp4", "/a2.mp4" }));
    }

    [Test]
    public void SelectionOnShorterPlaylistDoesNotChangeLockstepHaltDecision()
    {
        // Coordinator's lockstep gate looks at HasNextItem on both contexts independent of which is selected. Verify swapping selection between Primary (shorter) and Secondary (longer) doesn't change the outcome.
        using var h = new Harness();
        h.Vm.LoadPaths(new[] { "/a.mp4" }, replace: true);              // shorter
        h.EnablePip();
        h.Vm.Secondary!.LoadPaths(new[] { "/b1.mp4", "/b2.mp4" }, replace: true);  // longer

        // No selection. Both reach EOF → no advance.
        Harness.ReachFileEof(h.Vm.Primary, h.PrimaryPlayback);
        Harness.ReachFileEof(h.Vm.Secondary!, h.SecondaryPlayback!);
        Assert.That(h.Vm.Primary.Playlist.CurrentIndex, Is.EqualTo(0));
        Assert.That(h.Vm.Secondary!.Playlist.CurrentIndex, Is.EqualTo(0));

        // Swap selection; same outcome.
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        // Re-fire IsEofReached to re-trigger the lockstep gate.
        h.PrimaryPlayback.IsEofReached = false;
        h.PrimaryPlayback.IsEofReached = true;
        Assert.That(h.Vm.Primary.Playlist.CurrentIndex, Is.EqualTo(0));
    }

    // === Drift controller tests (PiP Sync — continuous drift correction) ===

    // Drive both contexts past the drift controller's gate prerequisites: a positive duration on each, both actually advancing (IsCoreIdle=false — the gate the controller reads), and IsPaused=false for the transport paths that still read it. Used by the correction tests below to focus on the offset/drift logic without re-asserting gate plumbing in every test. CorrectionGateBlocksWhenSeeking deliberately overrides individual gates.
    private static void ReadySyncMode(Harness h)
    {
        h.PrimaryPlayback.DurationSeconds = 60;
        h.SecondaryPlayback!.DurationSeconds = 60;
        h.PrimaryPlayback.IsPaused = false;
        h.SecondaryPlayback!.IsPaused = false;
        h.PrimaryPlayback.IsCoreIdle = false;
        h.SecondaryPlayback!.IsCoreIdle = false;
    }

    [Test]
    public void EnablePipCapturesZeroOffset()
    {
        // EnablePip lands with both contexts at content time 0 → captured offset is 0 by construction. Distinguish "captured 0" from "captured null/pending": a 0.2 s divergence against offset=0 is a coarse catch-up → SetSpeed(0.95). If EnablePip had left targetOffset null, the first tick would lazy-capture offset=0.2 → drift 0 → deadband idle → NO speed nudge.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.PrimaryPlayback.PositionSeconds = 10;
        h.SecondaryPlayback!.PositionSeconds = 10.2;  // Secondary ahead by 0.2 vs offset 0 → coarse
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 0.95 }).Within(1e-9), "EnablePip captured offset=0; a 0.2 s ahead-drift slows Secondary to 0.95");
    }

    [Test]
    public void FileLoadedOnPrimaryClearsOffset()
    {
        // FileLoaded resets one context's position to 0; the previous offset is no longer meaningful. After clear, the next tick takes the lazy-fallback path: baseline from current positions (drift 0 → idle), then defend the fresh offset.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        // Establish a non-trivial offset = 12 - 5 = 7.
        h.EstablishOffset(5, 12);

        // Sanity: an immediate tick with the same positions sees drift 0 → idle (no seek, no nudge).
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.Empty);

        // FileLoaded clears the offset to pending.
        h.PrimaryPlayback.RaiseFileLoaded();
        // First post-clear tick lazy-captures offset = 13 - 5 = 8 (drift 0 → idle). If the clear hadn't happened, offset would still be 7 → drift = (13 - 5) - 7 = 1 → we'd see a nudge/seek. Empty confirms the lazy-capture path.
        h.SecondaryPlayback!.PositionSeconds = 13;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty, "first post-clear tick should lazy-capture, not correct");
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.Empty);

        // Against the fresh offset (8), a 0.3 s ahead-drift is a coarse catch-up (not a >1 s hard resync — which is what a stale offset of 7 would have produced: drift (13.3-5)-7 = 1.3).
        h.SecondaryPlayback!.PositionSeconds = 13.3;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty, "fresh offset 8 → 0.3 s drift stays in coarse, no hard resync");
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 0.95 }).Within(1e-9));
    }

    [Test]
    public void FileLoadedOnSecondaryClearsOffset()
    {
        // Symmetric: Secondary FileLoaded must also clear (Secondary's position got reset to 0).
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.EstablishOffset(5, 12);
        // Offset established = 7.

        h.SecondaryPlayback!.RaiseFileLoaded();
        // Lazy-capture round: no correction even with what would be drift against the OLD offset.
        h.PrimaryPlayback.PositionSeconds = 6;
        h.SecondaryPlayback!.PositionSeconds = 14;  // delta = 8, would be drift=1 against old offset 7
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty, "FileLoaded cleared offset → lazy-capture takes the round");
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.Empty);
    }

    [Test]
    public void IsolatedAdjustmentRebaselinesOffsetAfterReturnToSync()
    {
        // The user's intentional asymmetric adjustment: select Secondary, move it alone (the isolated action clears the offset to pending), return to sync. The deselect itself writes nothing; the first both-advancing tick baselines from the adjusted divergence, and from then on the controller defends the new offset.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.PrimaryPlayback.PositionSeconds = 0;
        h.SecondaryPlayback!.PositionSeconds = 0;

        // User selects Secondary and steps it +5s ahead. The isolated transport action is what invalidates the offset — selection alone leaves it untouched.
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        Assert.That(h.Vm.GetSyncDiagnostic().TargetOffsetSeconds, Is.Not.Null, "selection alone must not invalidate the offset");
        h.Vm.StepFrameForward();
        h.SecondaryPlayback!.PositionSeconds = 5;
        Assert.That(h.Vm.GetSyncDiagnostic().TargetOffsetSeconds, Is.Null, "isolated adjustment cleared the offset to pending");
        h.Vm.SetSelected(null);

        // First tick baselines offset = 5 from the adjusted divergence (drift 0 → idle).
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.Empty);
        Assert.That(h.Vm.GetSyncDiagnostic().TargetOffsetSeconds!.Value, Is.EqualTo(5.0).Within(1e-9));

        // No drift: both advance by 10s, offset preserved → idle.
        h.PrimaryPlayback.PositionSeconds = 10;
        h.SecondaryPlayback!.PositionSeconds = 15;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.Empty);

        // Drift introduced: Secondary 0.1 s behind expected → coarse catch-up speeds it up to 1.05.
        h.SecondaryPlayback!.PositionSeconds = 14.9;  // drift = (14.9 - 10) - 5 = -0.1
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 1.05 }).Within(1e-9), "Secondary behind → speed up to 1.05");
    }

    [Test]
    public void SyncToSelectedGatesCorrection()
    {
        // Entering selected mode disables the slave: ApplyDriftCorrection must early-out on a non-null SelectedSlot regardless of drift size or offset state — no seek, no speed nudge.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.PrimaryPlayback.PositionSeconds = 0;
        h.SecondaryPlayback!.PositionSeconds = 0;
        h.Vm.SetSelected(ViewModelMain.VideoSlot.Primary);

        // Even with a huge implied drift, no correction.
        h.SecondaryPlayback!.PositionSeconds = 30;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.Empty);
    }

    [Test]
    public void SyncStepChapterNoChaptersIsNoOpAndPreservesOffset()
    {
        // No chapters on Primary → ChapterStep returns null → true no-op. Nothing moves and the captured offset stays valid. Verify the offset survives: a subsequent > 1 s drift hard-resyncs against offset = 5.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        // Establish a non-zero offset.
        h.EstablishOffset(0, 5);

        h.Vm.StepChapter(1);  // Primary has no chapters → no-op

        // Offset preserved: a subsequent > 1 s drift hard-resyncs to Primary.Pos + 5, it does NOT lazy-capture.
        h.PrimaryPlayback.PositionSeconds = 30;
        h.SecondaryPlayback!.PositionSeconds = 99;  // drift (99 - 30) - 5 = 64 → hard resync to 30 + 5 = 35
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 35.0 }), "offset preserved → hard resync still defends the invariant");
    }

    [Test]
    public void SyncStepChapterPastLastIsNoOpAndPreservesOffset()
    {
        // Same no-op semantics when the target is out of range (stepping past the last chapter): nothing moves, offset preserved.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.PrimaryPlayback.Chapters = new[]
        {
            new MediaChapter(0, "Intro", 0),
            new MediaChapter(1, "Outro", 200),
        };
        h.EstablishOffset(0, 5);

        h.PrimaryPlayback.PositionSeconds = 250;  // in the last chapter
        h.Vm.StepChapter(1);  // past the end → no-op

        h.PrimaryPlayback.PositionSeconds = 300;
        h.SecondaryPlayback!.PositionSeconds = 999;  // drift → hard resync to 300 + 5 = 305
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 305.0 }), "offset preserved → hard resync still defends the invariant");
    }

    [Test]
    public void HardResyncAboveOneSecondSeeksRegardlessOfSign()
    {
        // > 1 s drift → snap Secondary to Primary.Pos + offset with a hard seek, sign-agnostic.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);

        // Secondary far ahead.
        h.PrimaryPlayback.PositionSeconds = 10;
        h.SecondaryPlayback!.PositionSeconds = 11.5;  // drift +1.5
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 10.0 }));

        // Secondary far behind — same absolute target.
        h.SecondaryPlayback!.SeekCalls.Clear();
        h.SecondaryPlayback!.PositionSeconds = 8.5;  // drift -1.5
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 10.0 }));
    }

    [Test]
    public void CoarseCatchupNudgesSpeedInCatchupDirection()
    {
        // 50 ms < |drift| ≤ 1 s → full ±5% nudge, no seek. Ahead → slow (0.95); behind → speed up (1.05).
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);

        h.PrimaryPlayback.PositionSeconds = 10;
        h.SecondaryPlayback!.PositionSeconds = 10.2;  // ahead 0.2
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 0.95 }).Within(1e-9));
    }

    [Test]
    public void CoarseCatchupBehindSpeedsUp()
    {
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);

        h.PrimaryPlayback.PositionSeconds = 10;
        h.SecondaryPlayback!.PositionSeconds = 9.8;  // behind 0.2
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 1.05 }).Within(1e-9));
    }

    [Test]
    public void CoarseCatchupLatchesUntilOvershoot()
    {
        // Enter CatchUp behind → 1.05. Even after drift drops below 50 ms (same side), hold full 1.05 (no new SetSpeed). Only an overshoot past zero releases to the fine approach.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.PrimaryPlayback.PositionSeconds = 10;

        // Behind by 0.2 → CatchUp, 1.05.
        h.SecondaryPlayback!.PositionSeconds = 9.8;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 1.05 }).Within(1e-9));

        // Still behind, now only 0.03 (< 50 ms) but same sign → latched, hold 1.05 (no redundant SetSpeed).
        h.SecondaryPlayback!.PositionSeconds = 9.97;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 1.05 }).Within(1e-9), "latch holds full rate through the 50 ms boundary");

        // Overshoot: now 0.005 ahead → sign flip → release to Approach → within deadband → back to 1.0.
        h.SecondaryPlayback!.PositionSeconds = 10.005;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 1.05, 1.0 }).Within(1e-9), "overshoot releases the latch and the deadband settles to 1.0");
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
    }

    [Test]
    public void FineProportionalSettleScalesWithDrift()
    {
        // In Approach mode, deadband < |drift| ≤ 50 ms → proportional nudge = 1 - drift (5% at 50 ms, 0% at the deadband edge).
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.PrimaryPlayback.PositionSeconds = 10;

        // Ahead 0.035 → 0.965.
        h.SecondaryPlayback!.PositionSeconds = 10.035;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Has.Count.EqualTo(1));
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls[0], Is.EqualTo(0.965).Within(1e-9));

        // Behind 0.035 → 1.035.
        h.SecondaryPlayback!.PositionSeconds = 9.965;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Has.Count.EqualTo(2));
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls[1], Is.EqualTo(1.035).Within(1e-9));
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
    }

    [Test]
    public void DeadbandIdleIssuesNoCorrection()
    {
        // 10 ms drift is within the 20 ms deadband → command 1.0. Speed already 1.0 → no SetSpeed post, no seek.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);

        h.PrimaryPlayback.PositionSeconds = 10;
        h.SecondaryPlayback!.PositionSeconds = 10.010;  // drift +10 ms
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.Empty);
    }

    [Test]
    public void HardResyncFromCatchupResetsSpeedFirst()
    {
        // A blow-out past 1 s while latched in CatchUp must reset speed to 1.0 (before the seek) and drop the latch.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.PrimaryPlayback.PositionSeconds = 10;

        // Enter CatchUp behind → 1.05.
        h.SecondaryPlayback!.PositionSeconds = 9.8;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 1.05 }).Within(1e-9));

        // Now 1.5 s behind → hard resync: SetSpeed(1.0) then Seek to Primary.Pos + 0.
        h.SecondaryPlayback!.PositionSeconds = 8.5;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 1.05, 1.0 }).Within(1e-9));
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.EqualTo(new[] { 10.0 }));
    }

    [Test]
    public void GateFromCatchupResetsSpeedToOne()
    {
        // A gate hit (here core-idle) while nudging must release the speed back to 1.0 so a paused/stalled Secondary doesn't sit at 1.05.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.PrimaryPlayback.PositionSeconds = 10;

        h.SecondaryPlayback!.PositionSeconds = 9.8;  // behind → 1.05
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 1.05 }).Within(1e-9));

        // Secondary starts buffering (core-idle) → gate → speed released to 1.0.
        h.SecondaryPlayback!.IsCoreIdle = true;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 1.05, 1.0 }).Within(1e-9));
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
    }

    [Test]
    public void CorrectionGatedWhenSeeking()
    {
        // IsSeeking on either context blocks correction (a user seek — or the just-issued hard resync — is in flight). No seek, no nudge.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);

        h.SecondaryPlayback!.PositionSeconds = 5;  // would be huge drift
        h.SecondaryPlayback!.IsSeeking = true;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.Empty);

        h.SecondaryPlayback!.IsSeeking = false;
        h.PrimaryPlayback.IsSeeking = true;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.Empty);
    }

    [Test]
    public void CorrectionGatedWhenCoreIdle()
    {
        // Either stream core-idle (paused / cache stall / EOF) blocks correction — nothing advancing to measure.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);

        h.PrimaryPlayback.PositionSeconds = 10;
        h.SecondaryPlayback!.PositionSeconds = 10.5;  // would be coarse
        h.SecondaryPlayback!.IsCoreIdle = true;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty);
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.Empty);
    }

    [Test]
    public void CorrectionWithPipOffIsNoOp()
    {
        // No secondary → the tick must early-out cleanly (the timer keeps firing across enable/disable churn).
        using var h = new Harness();
        h.PrimaryPlayback.DurationSeconds = 60;
        h.PrimaryPlayback.IsCoreIdle = false;
        Assert.DoesNotThrow(() => h.Vm.ApplyDriftCorrection());
        Assert.That(h.PrimaryPlayback.SeekCalls, Is.Empty);
    }

    [Test]
    public void DisablePipClearsOffsetThenReEnableStartsFresh()
    {
        // After DisablePip, the offset must be cleared. Re-EnablePip starts at offset=0 again — verify by establishing a non-zero offset, disabling, re-enabling, and confirming a correction with equal positions is a no-op (offset back to 0) while a real drift nudges against the fresh 0.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);

        h.EstablishOffset(0, 5);

        h.Vm.DisablePip();
        h.EnablePip();             // Vm.Secondary is a fresh context; offset reset to 0.
        ReadySyncMode(h);

        // With offset=0, equal positions → no drift, idle.
        h.PrimaryPlayback.PositionSeconds = 10;
        h.SecondaryPlayback!.PositionSeconds = 10;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty, "fresh EnablePip must not have inherited offset=5 from prior session");
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.Empty);

        // And drift against the fresh offset=0 nudges correctly.
        h.SecondaryPlayback!.PositionSeconds = 10.2;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 0.95 }).Within(1e-9), "coarse catch-up against fresh offset 0");
    }

    [Test]
    public void CorrectionLazyCapturesWhenOffsetIsNull()
    {
        // The lazy-fallback path: file-load auto-play / lockstep advance / EnablePip-while-already-playing all leave targetOffset null when the first tick fires. That tick baselines from current positions (drift 0 → idle), then later drift nudges.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.PrimaryPlayback.RaiseFileLoaded();  // clears offset

        h.PrimaryPlayback.PositionSeconds = 10;
        h.SecondaryPlayback!.PositionSeconds = 13;
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SeekCalls, Is.Empty, "first tick lazy-captures");
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.Empty);

        // Now drift against the captured offset (3) is detectable → coarse nudge.
        h.SecondaryPlayback!.PositionSeconds = 13.3;  // drift = (13.3 - 10) - 3 = +0.3
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 0.95 }).Within(1e-9));
    }

    [Test]
    public void GetSyncDiagnosticReportsOffsetDriftAndSpeed()
    {
        using var h = new Harness();

        // PiP off → disabled snapshot.
        var off = h.Vm.GetSyncDiagnostic();
        Assert.That(off.Enabled, Is.False);
        Assert.That(off.Mode, Is.EqualTo("off"));

        h.EnablePip();
        ReadySyncMode(h);
        h.PrimaryPlayback.PositionSeconds = 10;
        h.SecondaryPlayback!.PositionSeconds = 10.2;  // ahead 0.2

        h.Vm.ApplyDriftCorrection();  // coarse → 0.95, mode=catchup
        var d = h.Vm.GetSyncDiagnostic();
        Assert.That(d.Enabled, Is.True);
        Assert.That(d.TargetOffsetSeconds!.Value, Is.EqualTo(0.0).Within(1e-9));
        Assert.That(d.CurrentOffsetSeconds, Is.EqualTo(0.2).Within(1e-9));
        Assert.That(d.DriftSeconds!.Value, Is.EqualTo(0.2).Within(1e-9));
        Assert.That(d.Speed, Is.EqualTo(0.95).Within(1e-9));
        Assert.That(d.Mode, Is.EqualTo("catchup"));
    }

    [Test]
    public void EnteringIsolatedModeReleasesALatchedCatchupSpeed()
    {
        // The isolated-mode gate must not just stop correcting — it must release an in-flight ±5% speed nudge, or the user-controlled stream keeps drifting at 0.95x forever.
        using var h = new Harness();
        h.EnablePip();
        ReadySyncMode(h);
        h.PrimaryPlayback.PositionSeconds = 10;
        h.SecondaryPlayback!.PositionSeconds = 10.2;  // drift 0.2 → latched catch-up
        h.Vm.ApplyDriftCorrection();
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 0.95 }).Within(1e-9));

        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        h.Vm.ApplyDriftCorrection();  // isolated gate → ResetSecondarySpeed
        Assert.That(h.SecondaryPlayback!.SetSpeedCalls, Is.EqualTo(new[] { 0.95, 1.0 }).Within(1e-9), "the latched nudge must be released on entering isolated mode");
    }

    [Test]
    public void GetSyncDiagnosticReportsPendingOffsetAndIsolatedMode()
    {
        using var h = new Harness();
        h.EnablePip();
        // A Secondary FileLoaded clears the offset to pending — the snapshot must express that as null offset AND null drift (not a bogus zero).
        h.SecondaryPlayback!.RaiseFileLoaded();
        var pending = h.Vm.GetSyncDiagnostic();
        Assert.That(pending.Enabled, Is.True);
        Assert.That(pending.TargetOffsetSeconds, Is.Null);
        Assert.That(pending.DriftSeconds, Is.Null);
        Assert.That(pending.Mode, Is.EqualTo("approach"));

        h.Vm.SetSelected(ViewModelMain.VideoSlot.Secondary);
        Assert.That(h.Vm.GetSyncDiagnostic().Mode, Is.EqualTo("isolated"));
    }

    [Test]
    public void DisablePipUnsubscribesTheOldSecondaryFileLoadedHandler()
    {
        // The coordinator subscribes Secondary.Playback.FileLoaded → ClearTargetOffset at EnablePip. If DisablePip left that handler attached, a stale event from the old session's playback would silently null a FRESH session's offset.
        using var h = new Harness();
        h.EnablePip();
        var oldSecondary = h.SecondaryPlayback!;
        h.Vm.DisablePip();
        h.EnablePip();  // fresh session; offset = 0 (non-null)

        oldSecondary.RaiseFileLoaded();

        Assert.That(h.Vm.GetSyncDiagnostic().TargetOffsetSeconds, Is.Not.Null, "a stale FileLoaded from the disabled session must not clear the fresh session's offset");
    }

    [Test]
    public void SyncPlayPauseWithFilelessPrimaryDrivesFromLoadedSecondary()
    {
        // Files dropped only onto the PiP secondary is a supported flow. In sync mode, Space must be able to both start AND pause the loaded stream: the flip target must derive from a stream whose play state can actually change. A fileless Primary's IsPaused never flips (SetPaused's Duration gate is a no-op), so deriving the target blindly from Primary computes the same value forever — Space could start the Secondary but never pause it.
        using var h = new Harness();
        h.EnablePip();
        h.PrimaryPlayback.IsPaused = true;   // fileless: Duration stays 0, so this can never change
        h.SecondaryPlayback!.DurationSeconds = 60;
        h.SecondaryPlayback!.IsPaused = true;

        h.Vm.PlayPauseCommand.Execute(null);
        Assert.That(h.SecondaryPlayback!.IsPaused, Is.False, "first Space starts the loaded secondary");

        h.Vm.PlayPauseCommand.Execute(null);
        Assert.That(h.SecondaryPlayback!.IsPaused, Is.True, "second Space pauses it again");
    }

    [Test]
    public void SyncPlayPauseDoesNotCaptureOffsetAgainstFilelessStream()
    {
        // Both offset writes in the sync PlayPause path — the eager pending-resolution and the differed-pre-state "join" capture — gate on both streams having files. A fileless stream's SetPaused is a gated no-op (it can't join anything), and resolving or capturing Secondary.Position − Primary.Position against a stream with no position would store garbage. The EnablePip-established offset must survive.
        using var h = new Harness();
        h.EnablePip();  // offset = 0
        h.PrimaryPlayback.IsPaused = true;  // fileless
        h.SecondaryPlayback!.DurationSeconds = 100;
        h.SecondaryPlayback!.PositionSeconds = 30;
        h.SecondaryPlayback!.IsPaused = false;  // playing → pre-states differ

        h.Vm.PlayPauseCommand.Execute(null);

        var d = h.Vm.GetSyncDiagnostic();
        Assert.That(d.TargetOffsetSeconds!.Value, Is.EqualTo(0.0).Within(1e-9), "join capture must not fire against a fileless stream");
    }
}
