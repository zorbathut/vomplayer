using System;
using System.Collections.Generic;

namespace Vomplayer.ViewModels;

// Discriminator on Playlist.Changed. Lets subscribers (especially PlaylistAutosave) tell "the user replaced the whole playlist" apart from "the same playlist's items shifted around" without inferring intent from heuristics.
public enum PlaylistChangeKind
{
    Replace,
    Append,
    Prepend,
    Insert,
    Move,
    Remove,
    SetCurrent,
    Advance,
}

// Plain-class playlist model. NOT an ObservableObject — a composite mutation like MoveMany touches two pieces of observable state (Items + CurrentIndex) and a PropertyChanged-per-property would fire twice, making consumers redraw twice (with the highlight class briefly on the wrong row between the two notifications). One Changed event per composite mutation matches the "redraw the whole panel" semantics consumers actually need (cf. the ChapterScrubber rebuild on every viewModel.Chapters change).
//
// Mutations dedup inline, so no-op calls (SetCurrent to the same value, a move that lands where it started) skip the event. Replace's choice to RESET CurrentIndex to 0 (or -1 if the new playlist is empty) — even when the new playlist contains the previously-playing file at a different index — is deliberate and matches the user-spec ("drop replaces playlist"); see PlaylistTests.ReplaceDeliberatelyResetsCurrentEvenWhenItemPersists.
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

    // Insert paths at the FRONT. Mirror of Append for the prepend-from-directory case (PreviousTrack walking into the file before the first playlist item). CurrentIndex shifts right by paths.Count so the currently-playing item keeps following its row; on an empty playlist it starts at the new first item. The caller (PreviousTrack) then PlayPlaylistItem(0)s to move onto the prepended item.
    public void Prepend(IReadOnlyList<string> paths)
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
        combined.AddRange(paths);
        combined.AddRange(Items);
        Items = combined.AsReadOnly();
        if (CurrentIndex < 0)
        {
            CurrentIndex = 0;
        }
        else
        {
            CurrentIndex += paths.Count;
        }
        Changed?.Invoke(PlaylistChangeKind.Prepend);
    }

    // Insert `paths` at `index` (gap semantics: the new items end up starting at `index`, pushing the item previously at `index` and everything after it to the right). index == Items.Count appends. Out-of-range throws — programmer-error contract shared with Move/Remove/SetCurrent. Empty paths is a no-op (no Changed). Used by the panel's positional drag-and-drop-of-external-files path.
    public void Insert(int index, IReadOnlyList<string> paths)
    {
        if (paths == null)
        {
            throw new ArgumentNullException(nameof(paths));
        }
        if (index < 0 || index > Items.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Items count is {Items.Count}");
        }
        if (paths.Count == 0)
        {
            return;
        }
        var combined = new List<string>(Items.Count + paths.Count);
        combined.AddRange(Items);
        combined.InsertRange(index, paths);
        if (CurrentIndex < 0)
        {
            CurrentIndex = 0;
        }
        else if (CurrentIndex >= index)
        {
            // The playing item sat at or after the insertion gap, so the inserted block pushed it right. Follow it so the highlight stays on the same content.
            CurrentIndex += paths.Count;
        }
        Items = combined.AsReadOnly();
        Changed?.Invoke(PlaylistChangeKind.Insert);
    }

    // Reorder a (possibly multi-item, possibly non-contiguous) selection to a single gap. `gap` ∈ [0, Count] is an insertion point in CURRENT-index coordinates ("insert the block before original row `gap`"; gap == Count means end). The selected items are extracted in ascending-index order and re-inserted as a contiguous block at the gap. Replaces the old single-item Move(from,to): gap semantics are unambiguous for non-contiguous selections and map directly to the panel's drop-indicator. Throws on out-of-range index/gap; empty indices and drop-in-place (resulting list + CurrentIndex unchanged) are no-ops that don't fire Changed.
    public void MoveMany(IReadOnlyList<int> indices, int gap)
    {
        if (indices == null)
        {
            throw new ArgumentNullException(nameof(indices));
        }
        if (gap < 0 || gap > Items.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(gap), gap, $"Items count is {Items.Count}");
        }
        // Dedupe + sort ascending. The selection arrives from GTK in arbitrary order and could in principle carry duplicates; a sorted distinct set makes the block well-defined and the index math below order-independent.
        var sel = new SortedSet<int>();
        foreach (int i in indices)
        {
            if (i < 0 || i >= Items.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(indices), i, $"Items count is {Items.Count}");
            }
            sel.Add(i);
        }
        if (sel.Count == 0)
        {
            return;
        }

        // block = selected items in ascending-index order; remaining = the rest, order preserved.
        var selList = new List<int>(sel);
        var block = new List<string>(selList.Count);
        foreach (int i in selList)
        {
            block.Add(Items[i]);
        }
        var remaining = new List<string>(Items.Count - selList.Count);
        for (int i = 0; i < Items.Count; i++)
        {
            if (!sel.Contains(i))
            {
                remaining.Add(Items[i]);
            }
        }

        // Translate the gap (in original coordinates) to a position in `remaining`: subtract the selected indices that fell below it.
        int selBelowGap = 0;
        foreach (int i in selList)
        {
            if (i < gap)
            {
                selBelowGap++;
            }
        }
        int insertPos = gap - selBelowGap;

        var newItems = new List<string>(Items.Count);
        newItems.AddRange(remaining.GetRange(0, insertPos));
        newItems.AddRange(block);
        newItems.AddRange(remaining.GetRange(insertPos, remaining.Count - insertPos));

        // CurrentIndex follows its item: into the block if the playing item was selected, otherwise to its new spot in `remaining` (shifted right by the block size when the block landed at or before it).
        int newCurrent = CurrentIndex;
        if (CurrentIndex >= 0)
        {
            int posInBlock = selList.IndexOf(CurrentIndex);
            if (posInBlock >= 0)
            {
                newCurrent = insertPos + posInBlock;
            }
            else
            {
                int selBelowCurrent = 0;
                foreach (int i in selList)
                {
                    if (i < CurrentIndex)
                    {
                        selBelowCurrent++;
                    }
                }
                int r = CurrentIndex - selBelowCurrent;
                newCurrent = r >= insertPos ? r + block.Count : r;
            }
        }

        var newReadOnly = newItems.AsReadOnly();
        if (SequenceEquals(Items, newReadOnly) && newCurrent == CurrentIndex)
        {
            // Drop-in-place (selection reinserted exactly where it was): no observable change, so don't fire.
            return;
        }
        Items = newReadOnly;
        CurrentIndex = newCurrent;
        Changed?.Invoke(PlaylistChangeKind.Move);
    }

    // Delete the items at `indices` (the Del key and the context-menu remove; single- and multi-row both route here). Indices are deduped and may arrive in any order. Out-of-range throws (programmer-error contract, same as Move/SetCurrent — CLAUDE.md bans silent handling); empty input is a no-op (no Changed). Fires a single PlaylistChangeKind.Remove.
    //
    // CurrentIndex adjustment: survivors shift left by the count of removed indices below them; if the playing item was itself removed, CurrentIndex lands on the survivor that slid into its slot — clamped to the new last row if the tail was removed. The panel's remove handler plays this new current row so the highlight stays honest; keeping CurrentIndex in range (never -1 while non-empty) is also what lets the autosave round-trip without RestorePlaylist clamping a -1 back to 0. List now empty → -1 (the "nothing playing" sentinel).
    public void RemoveMany(IReadOnlyList<int> indices)
    {
        if (indices == null)
        {
            throw new ArgumentNullException(nameof(indices));
        }
        var removed = new SortedSet<int>();
        foreach (int i in indices)
        {
            if (i < 0 || i >= Items.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(indices), i, $"Items count is {Items.Count}");
            }
            removed.Add(i);
        }
        if (removed.Count == 0)
        {
            return;
        }
        var copy = new List<string>(Items.Count - removed.Count);
        for (int i = 0; i < Items.Count; i++)
        {
            if (!removed.Contains(i))
            {
                copy.Add(Items[i]);
            }
        }
        int newCurrent;
        if (copy.Count == 0)
        {
            newCurrent = -1;
        }
        else
        {
            int removedBeforeCur = 0;
            foreach (int i in removed)
            {
                if (i < CurrentIndex)
                {
                    removedBeforeCur++;
                }
            }
            int shifted = CurrentIndex - removedBeforeCur;
            if (removed.Contains(CurrentIndex))
            {
                // The playing item was deleted: land on the survivor that slid into its slot, or the new last row if we deleted the tail.
                newCurrent = Math.Clamp(shifted, 0, copy.Count - 1);
            }
            else
            {
                newCurrent = shifted;
            }
        }
        Items = copy.AsReadOnly();
        CurrentIndex = newCurrent;
        Changed?.Invoke(PlaylistChangeKind.Remove);
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
