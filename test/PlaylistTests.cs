using System;
using Vomplayer.ViewModels;

namespace Vomplayer.Tests;

[TestFixture]
public class PlaylistTests
{
    [Test]
    public void DefaultsAreEmptyAndNoCurrent()
    {
        var p = new Playlist();
        Assert.That(p.Items, Is.Empty);
        Assert.That(p.CurrentIndex, Is.EqualTo(-1));
    }

    [Test]
    public void ReplaceSetsItemsAndCurrentToFirst()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c" });
        Assert.That(p.Items, Is.EqualTo(new[] { "a", "b", "c" }));
        Assert.That(p.CurrentIndex, Is.EqualTo(0));
    }

    [Test]
    public void ReplaceWithEmptyClears()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b" });
        p.Replace(Array.Empty<string>());
        Assert.That(p.Items, Is.Empty);
        Assert.That(p.CurrentIndex, Is.EqualTo(-1));
    }

    [Test]
    public void ReplaceFiresChangedOnce()
    {
        var p = new Playlist();
        int fires = 0;
        p.Changed += _ => fires++;
        p.Replace(new[] { "a", "b" });
        Assert.That(fires, Is.EqualTo(1));
    }

    [Test]
    public void ReplaceWithIdenticalContentAtIndexZeroDoesNotFireChanged()
    {
        // Dedup: if the new playlist has the same sequence AND CurrentIndex would land at the same place (0), Replace is a no-op. Pin so a future "always fire" refactor doesn't make consumers redraw for nothing.
        var p = new Playlist();
        p.Replace(new[] { "a", "b" });
        int fires = 0;
        p.Changed += _ => fires++;
        p.Replace(new[] { "a", "b" });
        Assert.That(fires, Is.EqualTo(0));
    }

    [Test]
    public void ReplaceDeliberatelyResetsCurrentEvenWhenItemPersists()
    {
        // Spec: "drop replaces playlist". Even if the new playlist coincidentally contains the previously-playing file at a different index, Replace deliberately resets CurrentIndex to 0. Pin so a future "preserve if present" feature doesn't sneak in and break the spec.
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c" });
        p.SetCurrent(1);
        p.Replace(new[] { "x", "b", "y" });
        Assert.That(p.CurrentIndex, Is.EqualTo(0));
        Assert.That(p.Items[0], Is.EqualTo("x"));
    }

    [Test]
    public void AppendKeepsCurrentWhenNonEmpty()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b" });
        p.SetCurrent(1);
        p.Append(new[] { "c", "d" });
        Assert.That(p.Items, Is.EqualTo(new[] { "a", "b", "c", "d" }));
        Assert.That(p.CurrentIndex, Is.EqualTo(1));
    }

    [Test]
    public void AppendFromEmptySetsCurrentToZero()
    {
        var p = new Playlist();
        p.Append(new[] { "a", "b" });
        Assert.That(p.Items, Is.EqualTo(new[] { "a", "b" }));
        Assert.That(p.CurrentIndex, Is.EqualTo(0));
    }

    [Test]
    public void AppendEmptyIsNoOp()
    {
        var p = new Playlist();
        int fires = 0;
        p.Changed += _ => fires++;
        p.Append(Array.Empty<string>());
        Assert.That(fires, Is.EqualTo(0));
        Assert.That(p.Items, Is.Empty);
    }

    // ---- MoveMany (gap-based group move; replaced the old single-item Move(from,to)) ----
    // gap is an insertion point in CURRENT-index coordinates: "insert the extracted block before original row `gap`"; gap == Count means end.

    [Test]
    public void MoveManyForwardReorders()
    {
        // Port of the old MoveForwardReorders: move item 1 ('b') to the end → gap == Count.
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c", "d" });
        p.SetCurrent(0);
        p.MoveMany(new[] { 1 }, 4);
        Assert.That(p.Items, Is.EqualTo(new[] { "a", "c", "d", "b" }));
    }

    [Test]
    public void MoveManyBackwardReorders()
    {
        // Port of MoveBackwardReorders: move item 3 ('d') to gap 1 (between 'a' and 'b').
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c", "d" });
        p.SetCurrent(0);
        p.MoveMany(new[] { 3 }, 1);
        Assert.That(p.Items, Is.EqualTo(new[] { "a", "d", "b", "c" }));
    }

    [Test]
    public void MoveManyCurrentItemFollows()
    {
        // Port of MoveAdjustsCurrentIndexWhenCurrentItemMoves: the playing item is the one moved.
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c", "d" });
        p.SetCurrent(1);                 // 'b'
        p.MoveMany(new[] { 1 }, 4);
        Assert.That(p.Items, Is.EqualTo(new[] { "a", "c", "d", "b" }));
        Assert.That(p.CurrentIndex, Is.EqualTo(3));
    }

    [Test]
    public void MoveManyBlockLandingAfterCurrentLeavesCurrent()
    {
        // Port of MoveAdjustsCurrentIndexWhenItemMovesPastCurrentForward: move item 0 ('a') to the end,
        // past the playing 'c'. 'c' shifts left by one (one item removed from before it).
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c", "d" });
        p.SetCurrent(2);                 // 'c'
        p.MoveMany(new[] { 0 }, 4);
        Assert.That(p.Items, Is.EqualTo(new[] { "b", "c", "d", "a" }));
        Assert.That(p.CurrentIndex, Is.EqualTo(1));   // 'c' is now at index 1
    }

    [Test]
    public void MoveManyBlockLandingBeforeCurrentShiftsCurrentRight()
    {
        // Port of MoveAdjustsCurrentIndexWhenItemMovesPastCurrentBackward: move item 3 ('d') to the front,
        // landing before the playing 'b'. 'b' shifts right by the block size (1).
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c", "d" });
        p.SetCurrent(1);                 // 'b'
        p.MoveMany(new[] { 3 }, 0);
        Assert.That(p.Items, Is.EqualTo(new[] { "d", "a", "b", "c" }));
        Assert.That(p.CurrentIndex, Is.EqualTo(2));   // 'b' is now at index 2
    }

    [Test]
    public void MoveManyOutsideCurrentDoesNotShiftIndex()
    {
        // Port of MoveOutsideCurrentDoesNotShiftIndex.
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c", "d" });
        p.SetCurrent(0);                 // 'a'
        p.MoveMany(new[] { 2 }, 4);      // move 'c' to end, all positions after current
        Assert.That(p.Items, Is.EqualTo(new[] { "a", "b", "d", "c" }));
        Assert.That(p.CurrentIndex, Is.EqualTo(0));
    }

    [Test]
    public void MoveManyContiguousBlockMovesTogether()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c", "d", "e" });
        p.MoveMany(new[] { 1, 2 }, 5);   // move {b,c} to the end
        Assert.That(p.Items, Is.EqualTo(new[] { "a", "d", "e", "b", "c" }));
    }

    [Test]
    public void MoveManyNonContiguousBecomesContiguous()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c", "d", "e" });
        p.MoveMany(new[] { 0, 2 }, 4);   // extract {a,c}, insert before original row 4 ('e')
        Assert.That(p.Items, Is.EqualTo(new[] { "b", "d", "a", "c", "e" }));
    }

    [Test]
    public void MoveManyCurrentInsideSelectionFollowsToBlock()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c", "d", "e" });
        p.SetCurrent(2);                 // 'c', which is inside the moved set
        p.MoveMany(new[] { 0, 2 }, 4);
        Assert.That(p.Items, Is.EqualTo(new[] { "b", "d", "a", "c", "e" }));
        Assert.That(p.CurrentIndex, Is.EqualTo(3));   // 'c' rode along to its slot in the block
        Assert.That(p.Items[p.CurrentIndex], Is.EqualTo("c"));
    }

    [Test]
    public void MoveManyCurrentOutsideShiftsWhenBlockLandsBefore()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c", "d", "e" });
        p.SetCurrent(4);                 // 'e'
        p.MoveMany(new[] { 0, 1 }, 3);   // {a,b} land before 'd', ahead of 'e'
        Assert.That(p.Items, Is.EqualTo(new[] { "c", "a", "b", "d", "e" }));
        Assert.That(p.CurrentIndex, Is.EqualTo(4));   // 'e' stays last
        Assert.That(p.Items[p.CurrentIndex], Is.EqualTo("e"));
    }

    [Test]
    public void MoveManyCurrentOutsideUnchangedWhenBlockLandsAfter()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c", "d", "e" });
        p.SetCurrent(0);                 // 'a'
        p.MoveMany(new[] { 3, 4 }, 2);   // {d,e} land after 'a'
        Assert.That(p.Items, Is.EqualTo(new[] { "a", "b", "d", "e", "c" }));
        Assert.That(p.CurrentIndex, Is.EqualTo(0));
    }

    [Test]
    public void MoveManyToEnd()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c" });
        p.MoveMany(new[] { 0 }, 3);      // gap == Count
        Assert.That(p.Items, Is.EqualTo(new[] { "b", "c", "a" }));
    }

    [Test]
    public void MoveManyDropInPlaceDoesNotFireChanged()
    {
        // Dropping a contiguous selection back onto its own span produces an identical list — no Changed.
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c", "d" });
        int fires = 0;
        p.Changed += _ => fires++;
        p.MoveMany(new[] { 1, 2 }, 3);   // {b,c} reinserted exactly where they were
        Assert.That(fires, Is.EqualTo(0));
        Assert.That(p.Items, Is.EqualTo(new[] { "a", "b", "c", "d" }));
    }

    [Test]
    public void MoveManyDuplicateIndicesDeduped()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c", "d" });
        p.MoveMany(new[] { 2, 1, 1 }, 4); // {1,2} after dedupe
        Assert.That(p.Items, Is.EqualTo(new[] { "a", "d", "b", "c" }));
    }

    [Test]
    public void MoveManyFiresMoveKindOnce()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c" });
        int fires = 0;
        PlaylistChangeKind? lastKind = null;
        p.Changed += k => { fires++; lastKind = k; };
        p.MoveMany(new[] { 0 }, 3);
        Assert.That(fires, Is.EqualTo(1));
        Assert.That(lastKind, Is.EqualTo(PlaylistChangeKind.Move));
    }

    [Test]
    public void MoveManyEmptyIndicesIsNoOp()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c" });
        int fires = 0;
        p.Changed += _ => fires++;
        p.MoveMany(Array.Empty<int>(), 1);
        Assert.That(fires, Is.EqualTo(0));
        Assert.That(p.Items, Is.EqualTo(new[] { "a", "b", "c" }));
    }

    [Test]
    public void MoveManyOutOfRangeThrows()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b" });
        Assert.Throws<ArgumentOutOfRangeException>(() => p.MoveMany(new[] { 5 }, 0));   // bad index
        Assert.Throws<ArgumentOutOfRangeException>(() => p.MoveMany(new[] { -1 }, 0));  // negative index
        Assert.Throws<ArgumentOutOfRangeException>(() => p.MoveMany(new[] { 0 }, 5));   // gap > Count
        Assert.Throws<ArgumentOutOfRangeException>(() => p.MoveMany(new[] { 0 }, -1));  // gap < 0
    }

    [Test]
    public void AdvanceReturnsNextAndBumps()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c" });
        var next = p.Advance();
        Assert.That(next, Is.EqualTo("b"));
        Assert.That(p.CurrentIndex, Is.EqualTo(1));
    }

    [Test]
    public void AdvanceAtEndReturnsNullAndDoesNotBump()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b" });
        p.SetCurrent(1);
        var next = p.Advance();
        Assert.That(next, Is.Null);
        Assert.That(p.CurrentIndex, Is.EqualTo(1));
    }

    [Test]
    public void AdvanceOnEmptyReturnsNull()
    {
        var p = new Playlist();
        var next = p.Advance();
        Assert.That(next, Is.Null);
        Assert.That(p.CurrentIndex, Is.EqualTo(-1));
    }

    [Test]
    public void SetCurrentInRangeWorks()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c" });
        p.SetCurrent(2);
        Assert.That(p.CurrentIndex, Is.EqualTo(2));
    }

    [Test]
    public void SetCurrentOutOfRangeThrows()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b" });
        Assert.Throws<ArgumentOutOfRangeException>(() => p.SetCurrent(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => p.SetCurrent(-1));
    }

    [Test]
    public void SetCurrentFiresChangedOnTransition()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b" });
        int fires = 0;
        p.Changed += _ => fires++;
        p.SetCurrent(1);
        Assert.That(fires, Is.EqualTo(1));
        p.SetCurrent(1);   // same value, no fire
        Assert.That(fires, Is.EqualTo(1));
    }

    [Test]
    public void ChangedKindIsReplaceOnReplace()
    {
        var p = new Playlist();
        PlaylistChangeKind? lastKind = null;
        p.Changed += k => lastKind = k;
        p.Replace(new[] { "a" });
        Assert.That(lastKind, Is.EqualTo(PlaylistChangeKind.Replace));
    }

    [Test]
    public void ChangedKindIsAppendOnAppend()
    {
        var p = new Playlist();
        p.Replace(new[] { "a" });
        PlaylistChangeKind? lastKind = null;
        p.Changed += k => lastKind = k;
        p.Append(new[] { "b" });
        Assert.That(lastKind, Is.EqualTo(PlaylistChangeKind.Append));
    }

    [Test]
    public void ChangedKindIsMoveOnMove()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c" });
        PlaylistChangeKind? lastKind = null;
        p.Changed += k => lastKind = k;
        p.MoveMany(new[] { 0 }, 3);
        Assert.That(lastKind, Is.EqualTo(PlaylistChangeKind.Move));
    }

    [Test]
    public void ChangedKindIsSetCurrentOnSetCurrent()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b" });
        PlaylistChangeKind? lastKind = null;
        p.Changed += k => lastKind = k;
        p.SetCurrent(1);
        Assert.That(lastKind, Is.EqualTo(PlaylistChangeKind.SetCurrent));
    }

    [Test]
    public void ChangedKindIsAdvanceOnAdvance()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b" });
        PlaylistChangeKind? lastKind = null;
        p.Changed += k => lastKind = k;
        p.Advance();
        Assert.That(lastKind, Is.EqualTo(PlaylistChangeKind.Advance));
    }

    [Test]
    public void PrependToEmptySetsCurrentToZero()
    {
        var p = new Playlist();
        p.Prepend(new[] { "a" });
        Assert.That(p.Items, Is.EqualTo(new[] { "a" }));
        Assert.That(p.CurrentIndex, Is.EqualTo(0));
    }

    [Test]
    public void PrependPrefixesItemsAndShiftsCurrentIndex()
    {
        var p = new Playlist();
        p.Replace(new[] { "b", "c" });
        p.SetCurrent(1); // current = "c"
        p.Prepend(new[] { "z", "a" });
        Assert.That(p.Items, Is.EqualTo(new[] { "z", "a", "b", "c" }));
        // current followed its item: was index 1, now index 1 + 2 = 3 (still "c").
        Assert.That(p.CurrentIndex, Is.EqualTo(3));
        Assert.That(p.Items[p.CurrentIndex], Is.EqualTo("c"));
    }

    [Test]
    public void PrependFiresChangedOnceWithPrependKind()
    {
        var p = new Playlist();
        p.Replace(new[] { "b" });
        int fires = 0;
        PlaylistChangeKind? lastKind = null;
        p.Changed += k => { fires++; lastKind = k; };
        p.Prepend(new[] { "a" });
        Assert.That(fires, Is.EqualTo(1));
        Assert.That(lastKind, Is.EqualTo(PlaylistChangeKind.Prepend));
    }

    [Test]
    public void PrependEmptyIsNoOp()
    {
        var p = new Playlist();
        p.Replace(new[] { "a" });
        int fires = 0;
        p.Changed += _ => fires++;
        p.Prepend(Array.Empty<string>());
        Assert.That(fires, Is.EqualTo(0));
        Assert.That(p.Items, Is.EqualTo(new[] { "a" }));
        Assert.That(p.CurrentIndex, Is.EqualTo(0));
    }

    // ---- RemoveMany (batch delete; generalizes Remove(int)) ----

    [Test]
    public void RemoveManyRemovesAllSelectedAndShiftsTail()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c", "d", "e" });
        p.RemoveMany(new[] { 1, 3 });
        Assert.That(p.Items, Is.EqualTo(new[] { "a", "c", "e" }));
    }

    [Test]
    public void RemoveManyBeforeCurrentDecrementsByCount()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c", "d", "e" });
        p.SetCurrent(4);                 // 'e'
        p.RemoveMany(new[] { 0, 1 });
        Assert.That(p.Items, Is.EqualTo(new[] { "c", "d", "e" }));
        Assert.That(p.CurrentIndex, Is.EqualTo(2));   // 'e' shifted left by 2
        Assert.That(p.Items[p.CurrentIndex], Is.EqualTo("e"));
    }

    [Test]
    public void RemoveManyIncludingCurrentLandsOnNextSurvivor()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c", "d", "e" });
        p.SetCurrent(2);                 // 'c' playing, inside the removed run
        p.RemoveMany(new[] { 1, 2, 3 });
        Assert.That(p.Items, Is.EqualTo(new[] { "a", "e" }));
        Assert.That(p.CurrentIndex, Is.EqualTo(1));   // the survivor that slid into the slot
        Assert.That(p.Items[p.CurrentIndex], Is.EqualTo("e"));
    }

    [Test]
    public void RemoveManyIncludingCurrentAtTailClampsToNewLast()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c" });
        p.SetCurrent(2);                 // 'c' playing, last row
        p.RemoveMany(new[] { 1, 2 });
        Assert.That(p.Items, Is.EqualTo(new[] { "a" }));
        Assert.That(p.CurrentIndex, Is.EqualTo(0));   // clamped to new last
        Assert.That(p.Items[p.CurrentIndex], Is.EqualTo("a"));
    }

    [Test]
    public void RemoveManyAllItemsClearsToEmptyMinusOne()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c" });
        p.RemoveMany(new[] { 0, 1, 2 });
        Assert.That(p.Items, Is.Empty);
        Assert.That(p.CurrentIndex, Is.EqualTo(-1));
    }

    [Test]
    public void RemoveManyNonContiguousCurrentSurvivesBetween()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c", "d", "e" });
        p.SetCurrent(2);                 // 'c' survives; one removed index below it
        p.RemoveMany(new[] { 0, 4 });
        Assert.That(p.Items, Is.EqualTo(new[] { "b", "c", "d" }));
        Assert.That(p.CurrentIndex, Is.EqualTo(1));
        Assert.That(p.Items[p.CurrentIndex], Is.EqualTo("c"));
    }

    [Test]
    public void RemoveManyDuplicateIndicesDeduped()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c" });
        p.RemoveMany(new[] { 1, 1 });
        Assert.That(p.Items, Is.EqualTo(new[] { "a", "c" }));  // 'b' removed once, not double
    }

    [Test]
    public void RemoveManyFiresRemoveKindOnce()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c", "d" });
        int fires = 0;
        PlaylistChangeKind? lastKind = null;
        p.Changed += k => { fires++; lastKind = k; };
        p.RemoveMany(new[] { 0, 2 });
        Assert.That(fires, Is.EqualTo(1));
        Assert.That(lastKind, Is.EqualTo(PlaylistChangeKind.Remove));
    }

    [Test]
    public void RemoveManyEmptyIsNoOp()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b" });
        int fires = 0;
        p.Changed += _ => fires++;
        p.RemoveMany(Array.Empty<int>());
        Assert.That(fires, Is.EqualTo(0));
        Assert.That(p.Items, Is.EqualTo(new[] { "a", "b" }));
    }

    [Test]
    public void RemoveManyOutOfRangeThrows()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b" });
        Assert.Throws<ArgumentOutOfRangeException>(() => p.RemoveMany(new[] { 5 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => p.RemoveMany(new[] { -1 }));
    }

    // ---- Insert (positional insert; gap semantics) ----

    [Test]
    public void InsertAtMiddleShiftsTailAndCurrent()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c" });
        p.SetCurrent(2);                 // 'c'
        p.Insert(1, new[] { "x", "y" });
        Assert.That(p.Items, Is.EqualTo(new[] { "a", "x", "y", "b", "c" }));
        Assert.That(p.CurrentIndex, Is.EqualTo(4));   // 'c' pushed right by 2
        Assert.That(p.Items[p.CurrentIndex], Is.EqualTo("c"));
    }

    [Test]
    public void InsertBeforeCurrentShiftsCurrent()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c" });
        p.SetCurrent(1);                 // 'b'
        p.Insert(0, new[] { "x" });
        Assert.That(p.Items, Is.EqualTo(new[] { "x", "a", "b", "c" }));
        Assert.That(p.CurrentIndex, Is.EqualTo(2));
        Assert.That(p.Items[p.CurrentIndex], Is.EqualTo("b"));
    }

    [Test]
    public void InsertAtCurrentSlotShiftsCurrent()
    {
        // Boundary: index == CurrentIndex. The playing item follows its content to the right.
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c" });
        p.SetCurrent(1);                 // 'b'
        p.Insert(1, new[] { "x" });
        Assert.That(p.Items, Is.EqualTo(new[] { "a", "x", "b", "c" }));
        Assert.That(p.CurrentIndex, Is.EqualTo(2));
        Assert.That(p.Items[p.CurrentIndex], Is.EqualTo("b"));
    }

    [Test]
    public void InsertAfterCurrentLeavesCurrent()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c" });
        p.SetCurrent(0);                 // 'a'
        p.Insert(2, new[] { "x" });
        Assert.That(p.Items, Is.EqualTo(new[] { "a", "b", "x", "c" }));
        Assert.That(p.CurrentIndex, Is.EqualTo(0));
    }

    [Test]
    public void InsertAtEndAppends()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b" });
        p.Insert(2, new[] { "c" });      // index == Count
        Assert.That(p.Items, Is.EqualTo(new[] { "a", "b", "c" }));
        Assert.That(p.CurrentIndex, Is.EqualTo(0));  // current 'a' unaffected
    }

    [Test]
    public void InsertIntoEmptySetsCurrentToZero()
    {
        var p = new Playlist();
        p.Insert(0, new[] { "a", "b" });
        Assert.That(p.Items, Is.EqualTo(new[] { "a", "b" }));
        Assert.That(p.CurrentIndex, Is.EqualTo(0));
    }

    [Test]
    public void InsertEmptyPathsIsNoOp()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b" });
        int fires = 0;
        p.Changed += _ => fires++;
        p.Insert(1, Array.Empty<string>());
        Assert.That(fires, Is.EqualTo(0));
        Assert.That(p.Items, Is.EqualTo(new[] { "a", "b" }));
    }

    [Test]
    public void InsertFiresInsertKindOnce()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b" });
        int fires = 0;
        PlaylistChangeKind? lastKind = null;
        p.Changed += k => { fires++; lastKind = k; };
        p.Insert(1, new[] { "x" });
        Assert.That(fires, Is.EqualTo(1));
        Assert.That(lastKind, Is.EqualTo(PlaylistChangeKind.Insert));
    }

    [Test]
    public void InsertOutOfRangeGapThrows()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b" });
        Assert.Throws<ArgumentOutOfRangeException>(() => p.Insert(3, new[] { "x" }));   // gap > Count
        Assert.Throws<ArgumentOutOfRangeException>(() => p.Insert(-1, new[] { "x" }));  // gap < 0
    }
}
