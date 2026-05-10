using System;
using System.Collections.Generic;

namespace Vomplayer.ViewModels;

// Discriminator on Playlist.Changed. Lets subscribers (especially PlaylistAutosave) tell "the user replaced the whole playlist" apart from "the same playlist's items shifted around" without inferring intent from heuristics.
public enum PlaylistChangeKind
{
    Replace,
    Append,
    Move,
    SetCurrent,
    Advance,
}

// Plain-class playlist model. NOT an ObservableObject — Move(from,to) mutates two pieces of observable state (Items + CurrentIndex) and a single PropertyChanged-per-property would fire twice and make consumers redraw twice (with the highlight class briefly on the wrong row between the two notifications). One Changed event per composite mutation matches the "redraw the whole panel" semantics consumers actually need (cf. the ChapterScrubber rebuild on every viewModel.Chapters change).
//
// All mutation paths funnel through Notify(): a sequence-equality check + reference assignment, so no-op mutations (Move(i,i), SetCurrent to same value) skip the event. Replace's choice to RESET CurrentIndex to 0 (or -1 if the new playlist is empty) — even when the new playlist contains the previously-playing file at a different index — is deliberate and matches the user-spec ("drop replaces playlist"); see PlaylistTests.ReplaceDeliberatelyResetsCurrentEvenWhenItemPersists.
public sealed class Playlist
{
    public IReadOnlyList<string> Items { get; private set; } = Array.Empty<string>();

    // -1 sentinel for "empty / not playing". Avoids a separate bool-or-nullable; -1 is unambiguous because real indices are always >= 0.
    public int CurrentIndex { get; private set; } = -1;

    public event Action<PlaylistChangeKind>? Changed;

    public void Replace(IReadOnlyList<string> paths)
    {
        if (paths == null)
        {
            throw new ArgumentNullException(nameof(paths));
        }
        IReadOnlyList<string> newItems = paths.Count == 0 ? Array.Empty<string>() : new List<string>(paths).AsReadOnly();
        int newCurrent = paths.Count == 0 ? -1 : 0;
        bool itemsChanged = !SequenceEquals(Items, newItems);
        bool currentChanged = newCurrent != CurrentIndex;
        if (!itemsChanged && !currentChanged)
        {
            return;
        }
        Items = newItems;
        CurrentIndex = newCurrent;
        Changed?.Invoke(PlaylistChangeKind.Replace);
    }

    public void Append(IReadOnlyList<string> paths)
    {
        if (paths == null)
        {
            throw new ArgumentNullException(nameof(paths));
        }
        if (paths.Count == 0)
        {
            return;
        }
        var combined = new List<string>(Items.Count + paths.Count);
        combined.AddRange(Items);
        combined.AddRange(paths);
        bool wasEmpty = Items.Count == 0;
        Items = combined.AsReadOnly();
        if (wasEmpty)
        {
            CurrentIndex = 0;
        }
        // CurrentIndex unchanged when appending to a non-empty playlist — the currently-playing file stays at the same index, just with more items behind it.
        Changed?.Invoke(PlaylistChangeKind.Append);
    }

    // Reorder: take the item at `from`, remove it, insert at `to`. Throws on out-of-range — out-of-range is a programmer error in single-threaded GTK callers, not a user-facing condition (per CLAUDE.md, silent error handling is banned).
    public void Move(int from, int to)
    {
        if (from < 0 || from >= Items.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(from), from, $"Items count is {Items.Count}");
        }
        if (to < 0 || to >= Items.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(to), to, $"Items count is {Items.Count}");
        }
        if (from == to)
        {
            return;
        }
        var copy = new List<string>(Items);
        var moved = copy[from];
        copy.RemoveAt(from);
        copy.Insert(to, moved);
        // CurrentIndex adjustment:
        //  - If the current item itself moved, CurrentIndex follows it to `to`.
        //  - Forward move (from < to) that crosses current (from < current <= to): current shifts left by 1.
        //  - Backward move (from > to) that crosses current (to <= current < from): current shifts right by 1.
        //  - Otherwise current is on the same side of the moved item before and after; no adjustment.
        int newCurrent = CurrentIndex;
        if (CurrentIndex == from)
        {
            newCurrent = to;
        }
        else if (from < to && from < CurrentIndex && CurrentIndex <= to)
        {
            newCurrent--;
        }
        else if (from > to && to <= CurrentIndex && CurrentIndex < from)
        {
            newCurrent++;
        }
        Items = copy.AsReadOnly();
        CurrentIndex = newCurrent;
        Changed?.Invoke(PlaylistChangeKind.Move);
    }

    public void SetCurrent(int index)
    {
        if (index < 0 || index >= Items.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Items count is {Items.Count}");
        }
        if (CurrentIndex == index)
        {
            return;
        }
        CurrentIndex = index;
        Changed?.Invoke(PlaylistChangeKind.SetCurrent);
    }

    // Bumps CurrentIndex to next item and returns its path; null when already at the end (or empty). Caller is responsible for kicking off playback of the returned path. Bumping atomically with the read avoids a "read next, then advance" race the auto-advance handler would otherwise have to manage.
    public string? Advance()
    {
        if (CurrentIndex < 0 || CurrentIndex >= Items.Count - 1)
        {
            return null;
        }
        CurrentIndex++;
        Changed?.Invoke(PlaylistChangeKind.Advance);
        return Items[CurrentIndex];
    }

    private static bool SequenceEquals(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }
        return true;
    }
}
