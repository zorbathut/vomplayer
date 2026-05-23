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

    [Test]
    public void MoveForwardReorders()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c", "d" });
        p.SetCurrent(0);
        p.Move(1, 3);
        Assert.That(p.Items, Is.EqualTo(new[] { "a", "c", "d", "b" }));
    }

    [Test]
    public void MoveBackwardReorders()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c", "d" });
        p.SetCurrent(0);
        p.Move(3, 1);
        Assert.That(p.Items, Is.EqualTo(new[] { "a", "d", "b", "c" }));
    }

    [Test]
    public void MoveAdjustsCurrentIndexWhenCurrentItemMoves()
    {
        // The current item itself is moved; CurrentIndex follows it.
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c", "d" });
        p.SetCurrent(1);
        p.Move(1, 3);
        Assert.That(p.Items, Is.EqualTo(new[] { "a", "c", "d", "b" }));
        Assert.That(p.CurrentIndex, Is.EqualTo(3));
    }

    [Test]
    public void MoveAdjustsCurrentIndexWhenItemMovesPastCurrentForward()
    {
        // from < current <= to (forward move that crosses current). Current shifts left by one.
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c", "d" });
        p.SetCurrent(2);                 // 'c'
        p.Move(0, 3);
        Assert.That(p.Items, Is.EqualTo(new[] { "b", "c", "d", "a" }));
        Assert.That(p.CurrentIndex, Is.EqualTo(1));   // 'c' is now at index 1
    }

    [Test]
    public void MoveAdjustsCurrentIndexWhenItemMovesIntoCurrentSlotForward()
    {
        // The off-by-one trap: from < current AND current == to. Same forward-move adjustment.
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c", "d" });
        p.SetCurrent(2);                 // 'c'
        p.Move(0, 2);
        Assert.That(p.Items, Is.EqualTo(new[] { "b", "c", "a", "d" }));
        Assert.That(p.CurrentIndex, Is.EqualTo(1));   // 'c' is now at index 1
    }

    [Test]
    public void MoveAdjustsCurrentIndexWhenItemMovesPastCurrentBackward()
    {
        // to <= current < from (backward move that crosses current). Current shifts right by one.
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c", "d" });
        p.SetCurrent(1);                 // 'b'
        p.Move(3, 0);
        Assert.That(p.Items, Is.EqualTo(new[] { "d", "a", "b", "c" }));
        Assert.That(p.CurrentIndex, Is.EqualTo(2));   // 'b' is now at index 2
    }

    [Test]
    public void MoveAdjustsCurrentIndexWhenItemMovesIntoCurrentSlotBackward()
    {
        // The symmetric off-by-one trap: to == current AND current < from.
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c", "d" });
        p.SetCurrent(1);                 // 'b'
        p.Move(3, 1);
        Assert.That(p.Items, Is.EqualTo(new[] { "a", "d", "b", "c" }));
        Assert.That(p.CurrentIndex, Is.EqualTo(2));   // 'b' is now at index 2
    }

    [Test]
    public void MoveOutsideCurrentDoesNotShiftIndex()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c", "d" });
        p.SetCurrent(0);                 // 'a'
        p.Move(2, 3);                    // both indices > current
        Assert.That(p.CurrentIndex, Is.EqualTo(0));
    }

    [Test]
    public void MoveSamePositionDoesNotFireChanged()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b", "c" });
        int fires = 0;
        p.Changed += _ => fires++;
        p.Move(1, 1);
        Assert.That(fires, Is.EqualTo(0));
    }

    [Test]
    public void MoveOutOfRangeThrows()
    {
        var p = new Playlist();
        p.Replace(new[] { "a", "b" });
        Assert.Throws<ArgumentOutOfRangeException>(() => p.Move(5, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => p.Move(0, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => p.Move(-1, 0));
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
        p.Move(0, 2);
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
}
