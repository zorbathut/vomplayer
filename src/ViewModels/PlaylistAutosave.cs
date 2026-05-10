using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using Vomplayer.UserData;

namespace Vomplayer.ViewModels;

// Owns the autosave lifecycle for the in-memory Playlist(s). Holds the current GUID, listens to Playlist.Changed and the title-source PropertyChanged events on each bound context, and writes to ISavedPlaylists on every relevant transition.
//
// GUID-mint rule (decided purely from the Changed payload + bind state — no caller coordination):
//   - Replace on Primary, with no Secondary bound or Secondary empty → new GUID (the whole playlist is being replaced)
//   - Replace on Primary, with Secondary bound and non-empty → same GUID (only one slot is being replaced)
//   - Replace on Secondary → same GUID
//   - Append / SetCurrent / Move / Advance → same GUID (incremental mutation)
//
// loading flag: BeginRestore sets it; Persist short-circuits while raised; EndRestore clears it WITHOUT triggering a save. Without this gate, restoring a saved entry would fire two Changed events (Replace + SetCurrent) and overwrite the stored title with placeholder filenames before mpv's media-title arrives.
public sealed class PlaylistAutosave
{
    private readonly ISavedPlaylists repo;
    private VideoContext? primary;
    private VideoContext? secondary;
    private bool loading;
    private Action<PlaylistChangeKind>? primaryChangedHandler;
    private PropertyChangedEventHandler? primaryPropertyChangedHandler;
    private Action<PlaylistChangeKind>? secondaryChangedHandler;
    private PropertyChangedEventHandler? secondaryPropertyChangedHandler;

    public Guid CurrentGuid { get; private set; }

    // Fired after every successful write (Save / Touch). MainWindow subscribes to rebuild the Recent menu — the menu reflects the current GUID's row plus the existing rows in their (potentially-reordered) recency.
    public event Action? Saved;

    public PlaylistAutosave(ISavedPlaylists repo)
    {
        if (repo == null)
        {
            throw new ArgumentNullException(nameof(repo));
        }
        this.repo = repo;
    }

    public void BindPrimary(VideoContext ctx)
    {
        if (ctx == null)
        {
            throw new ArgumentNullException(nameof(ctx));
        }
        if (primary != null)
        {
            throw new InvalidOperationException("Primary already bound");
        }
        primary = ctx;
        primaryChangedHandler = kind => OnChanged(ctx, kind);
        primaryPropertyChangedHandler = OnPrimaryPropertyChanged;
        ctx.Playlist.Changed += primaryChangedHandler;
        ctx.PropertyChanged += primaryPropertyChangedHandler;
    }

    public void BindSecondary(VideoContext ctx)
    {
        if (ctx == null)
        {
            throw new ArgumentNullException(nameof(ctx));
        }
        if (secondary != null)
        {
            throw new InvalidOperationException("Secondary already bound");
        }
        secondary = ctx;
        secondaryChangedHandler = kind => OnChanged(ctx, kind);
        secondaryPropertyChangedHandler = OnSecondaryPropertyChanged;
        ctx.Playlist.Changed += secondaryChangedHandler;
        ctx.PropertyChanged += secondaryPropertyChangedHandler;
    }

    public void UnbindSecondary()
    {
        if (secondary == null)
        {
            return;
        }
        if (secondaryChangedHandler != null)
        {
            secondary.Playlist.Changed -= secondaryChangedHandler;
            secondaryChangedHandler = null;
        }
        if (secondaryPropertyChangedHandler != null)
        {
            secondary.PropertyChanged -= secondaryPropertyChangedHandler;
            secondaryPropertyChangedHandler = null;
        }
        secondary = null;
        // After Secondary leaves, the in-memory playlist becomes single-stream. Re-persist so the row's stream_count drops to 1 and the startup-autoload rule starts treating it as single-stream. Persist itself is gated on `loading`, so a DisablePip mid-Restore is suppressed — the restoring caller is responsible for writing the final state.
        Persist();
    }

    public void BeginRestore()
    {
        loading = true;
    }

    public void EndRestore()
    {
        loading = false;
    }

    public void SetCurrentGuid(Guid guid)
    {
        CurrentGuid = guid;
    }

    // Fire Saved manually. Used by ViewModelMain.LoadFromSaved after Touch — the Touch itself doesn't go through Persist (no payload changed), but the menu still needs to rebuild because last_used_at moved.
    public void RaiseSaved()
    {
        Saved?.Invoke();
    }

    private void OnChanged(VideoContext source, PlaylistChangeKind kind)
    {
        if (loading)
        {
            return;
        }
        if (kind == PlaylistChangeKind.Replace && ReferenceEquals(source, primary) && SecondaryHasNoItems())
        {
            // Primary's playlist was wholesale-replaced and there's no parallel Secondary content to preserve — the user is starting a fresh session. Mint a new GUID so the old session's row is left intact in the Recent menu.
            CurrentGuid = Guid.NewGuid();
        }
        Persist();
    }

    private void OnPrimaryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (loading)
        {
            return;
        }
        if (e.PropertyName == nameof(VideoContext.MediaTitle) || e.PropertyName == nameof(VideoContext.CurrentFilePath))
        {
            // Title sources changed; re-persist with the recomputed title.
            Persist();
        }
    }

    private void OnSecondaryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (loading)
        {
            return;
        }
        if (e.PropertyName == nameof(VideoContext.MediaTitle) || e.PropertyName == nameof(VideoContext.CurrentFilePath))
        {
            Persist();
        }
    }

    private bool SecondaryHasNoItems()
    {
        return secondary == null || secondary.Playlist.Items.Count == 0;
    }

    private void Persist()
    {
        if (loading)
        {
            return;
        }
        if (primary == null)
        {
            return;
        }
        var streams = new List<SavedPlaylistStream>(2);
        if (primary.Playlist.Items.Count > 0)
        {
            streams.Add(new SavedPlaylistStream(0, primary.Playlist.CurrentIndex, primary.Playlist.Items));
        }
        if (secondary != null && secondary.Playlist.Items.Count > 0)
        {
            streams.Add(new SavedPlaylistStream(1, secondary.Playlist.CurrentIndex, secondary.Playlist.Items));
        }
        if (streams.Count == 0)
        {
            // Empty-on-empty: nothing to save. Don't mint a GUID against nothing — wait for the first real Replace.
            return;
        }
        if (CurrentGuid == Guid.Empty)
        {
            // First save in this process / after a load with no prior CurrentGuid. Mint now so Save has something to key by.
            CurrentGuid = Guid.NewGuid();
        }
        string title = ComputeTitle();
        repo.Save(CurrentGuid, title, streams);
        Saved?.Invoke();
    }

    private string ComputeTitle()
    {
        string a = TitleFor(primary);
        if (secondary != null && secondary.Playlist.Items.Count > 0)
        {
            string b = TitleFor(secondary);
            return $"{a} + {b}";
        }
        return a;
    }

    private static string TitleFor(VideoContext? ctx)
    {
        if (ctx == null)
        {
            return "";
        }
        if (!string.IsNullOrEmpty(ctx.MediaTitle))
        {
            return ctx.MediaTitle!;
        }
        // Fall back to the basename of the current item — mpv hasn't reported a media-title yet (file not loaded, or loaded but pre-FileLoaded). Once mpv reports, the autosave's PropertyChanged subscription fires and the row is rewritten with the real title.
        if (ctx.Playlist.CurrentIndex >= 0 && ctx.Playlist.CurrentIndex < ctx.Playlist.Items.Count)
        {
            string path = ctx.Playlist.Items[ctx.Playlist.CurrentIndex];
            try
            {
                if (path.Contains("://"))
                {
                    return path;
                }
                var name = Path.GetFileName(path);
                return string.IsNullOrEmpty(name) ? path : name;
            }
            catch (ArgumentException)
            {
                return path;
            }
        }
        return "";
    }
}
