using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Vomplayer.Util;

// Factory for Gtk.DropTargetAsync controllers that accept file drops as text/uri-list, parse the URIs in-process, and deliver local paths to a callback. Replaces the earlier Gtk.DropTarget(GdkFileList) approach because GTK 4's content-deserializer machinery for that path always tries to mediate via the FileTransfer/Documents portals — and the Documents portal hard-rejects paths outside its trusted-roots list ($HOME, XDG dirs, /tmp …) with "Invalid parent directory". On Steam Deck, dragging from /var/mnt/* trips this every time, and we can't out-register the built-in deserializers because the destination MIME format list ends up with portal.filetransfer ahead of text/uri-list regardless of registration order.
//
// Instead, we use Gtk.DropTargetAsync with explicit ContentFormats listing only "text/uri-list". GTK's accept logic only matches sources that offer text/uri-list (which all real file managers do). On drop, we call gdk_drop_read_async ourselves with the explicit mime list — bypassing the deserializer machinery, and therefore the portal call. With --filesystem=host:ro in the manifest, the sandbox already has read access to whatever the source dropped; no portal mediation needed.
//
// The actual stream read runs on a threadpool thread (Task.Run). Wayland data-offer reads are pipe-backed: the source process writes URIs to a kernel pipe asynchronously, so a synchronous Read CAN block on the main thread waiting for data — and if the wl_data_offer_receive request itself was queued in our Wayland output buffer and the main loop is blocked, the source never even gets the request, deadlocking the drop. Off-thread, the main loop keeps spinning, the request is flushed, the source writes, our read completes.
public static class UriListDropTarget
{
    // Creates a DropTargetAsync configured to accept text/uri-list drops. When the user drops files, the URIs are parsed (RFC 2483: lines, # comments, CRLF tolerant), file:// URIs are converted to local paths (percent-decoded via Uri.LocalPath), other schemes pass through, and directories are recursively expanded into their video-file contents (extension-filtered via MediaExtensions.ExpandPaths). The expanded path list is delivered to onFilesDropped on the GTK main thread; an empty list means the drop should be considered rejected (see the drop signal handler).
    public static Gtk.DropTargetAsync Create(System.Action<List<string>> onFilesDropped)
    {
        if (onFilesDropped == null)
        {
            throw new ArgumentNullException(nameof(onFilesDropped));
        }

        var formats = Gdk.ContentFormats.New(new[] { "text/uri-list" });
        var dropTarget = Gtk.DropTargetAsync.New(formats, Gdk.DragAction.Copy | Gdk.DragAction.Move | Gdk.DragAction.Link);
        dropTarget.OnDrop += (sender, args) =>
        {
            var drop = args.Drop;
            ReadAndDispatch(drop, onFilesDropped);
            // Returning true tells GTK we'll handle the drop asynchronously. We're responsible for calling drop.Finish() once we're done; that happens inside ReadAndDispatch's continuation.
            return true;
        };
        return dropTarget;
    }

    // Like Create, but the callback also receives the drop point (x, y) in the target widget's coordinates, frozen at drop time. The playlist panel uses these to compute a positional insert gap. The (x, y) is captured synchronously here; the async read path is identical to Create's.
    public static Gtk.DropTargetAsync CreatePositional(System.Action<List<string>, double, double> onFilesDroppedAt)
    {
        if (onFilesDroppedAt == null)
        {
            throw new ArgumentNullException(nameof(onFilesDroppedAt));
        }

        var formats = Gdk.ContentFormats.New(new[] { "text/uri-list" });
        var dropTarget = Gtk.DropTargetAsync.New(formats, Gdk.DragAction.Copy | Gdk.DragAction.Move | Gdk.DragAction.Link);
        dropTarget.OnDrop += (sender, args) =>
        {
            var drop = args.Drop;
            double x = args.X;
            double y = args.Y;
            ReadAndDispatch(drop, paths => onFilesDroppedAt(paths, x, y));
            return true;
        };
        return dropTarget;
    }

    private static void ReadAndDispatch(Gdk.Drop drop, System.Action<List<string>> onFilesDropped)
    {
        var mimes = GLib.Internal.Utf8StringArrayNullTerminatedOwnedHandle.Create(new[] { "text/uri-list" });
        var handler = new Gio.Internal.AsyncReadyCallbackAsyncHandler((sourceObj, result, _data) =>
        {
            try
            {
                var stream = drop.ReadFinish(result, out var _mime);
                if (stream == null)
                {
                    Console.Error.WriteLine("[vompl] dnd: ReadFinish returned null stream");
                    Finalize(drop, new List<string>(), onFilesDropped);
                    return;
                }

                // Threadpool the read: a synchronous read on the GTK main thread can deadlock waiting for the source's pipe write while the main loop (which would flush our wl_data_offer_receive request to the compositor) is blocked. On the threadpool the main loop keeps running, the request flushes, the source writes, our Read completes. GIO Read is documented thread-safe.
                System.Threading.Tasks.Task.Run(() =>
                {
                    List<string> paths;
                    try
                    {
                        using var ms = new MemoryStream();
                        var buffer = new byte[4096];
                        while (true)
                        {
                            nint n = stream.Read(buffer.AsSpan(), null);
                            if (n <= 0)
                            {
                                break;
                            }
                            ms.Write(buffer, 0, (int)n);
                        }
                        var text = Encoding.UTF8.GetString(ms.ToArray());
                        paths = ParseAndConvert(text);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[vompl] dnd: stream read/parse failed: {ex}");
                        paths = new List<string>();
                    }
                    // Marshal the result back to the GTK main thread before calling onFilesDropped (the consumer touches GTK widgets via the playlist VM). drop.Finish must also be on the main thread.
                    IdleSafe.Add((int)GLib.Constants.PRIORITY_DEFAULT, () => Finalize(drop, paths, onFilesDropped));
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[vompl] dnd: ReadFinish threw: {ex}");
                Finalize(drop, new List<string>(), onFilesDropped);
            }
        });

        Gdk.Internal.Drop.ReadAsync(
            drop.Handle.DangerousGetHandle(),
            (GLib.Internal.Utf8StringArrayNullTerminatedHandle)(object)mimes,
            (int)GLib.Constants.PRIORITY_DEFAULT,
            IntPtr.Zero,
            handler.NativeCallback,
            IntPtr.Zero);
    }

    private static void Finalize(Gdk.Drop drop, List<string> paths, System.Action<List<string>> onFilesDropped)
    {
        try
        {
            onFilesDropped(paths);
        }
        finally
        {
            // Always call drop.Finish so the source knows the drop completed. Use Copy as the action regardless — Move/Link don't apply to our "play this file" semantics.
            drop.Finish(Gdk.DragAction.Copy);
        }
    }

    // Parse a text/uri-list payload (RFC 2483) and convert each entry to either a local filesystem path (for file:// URIs, percent-decoded via Uri.LocalPath) or pass through unchanged (for non-file URIs and bare paths). Then expand directories via MediaExtensions.ExpandPaths.
    internal static List<string> ParseAndConvert(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new List<string>();
        }
        var raw = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r').Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                raw.Add(uri.LocalPath);
            }
            else
            {
                raw.Add(trimmed);
            }
        }
        return MediaExtensions.ExpandPaths(raw, msg => Console.Error.WriteLine($"[vompl] dnd: {msg}"));
    }
}
