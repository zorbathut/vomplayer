using System;
using System.Collections.Generic;
using System.IO;

namespace Vomplayer.Util;

public enum PathProblemKind
{
    // The path is inside the document portal's FUSE mount: a per-file directory the sandbox was handed because it can't see the file's real location. Playable, but "its directory" is meaningless.
    DocumentPortal,
    // A portal path whose origin directory the sandbox can see, but whose origin file was rejected (missing, a directory, or not the same file). Granting the folder would change nothing here. Directory is always set for this kind.
    DocumentPortalOriginRejected,
    // An ordinary local path whose directory exists but can't be enumerated.
    DirectoryUnlistable,
}

// One detected problem with a path the user asked to play. Path is the normalized absolute spelling (the playlist's own spelling is on the load line that precedes it in the log). For the two portal kinds, HostPath and Directory are the origin the portal reports (the FUSE file's host-path xattr) — a portal problem with a non-null HostPath means the origin was seen and declined, not merely reported — and HostPathErrno is why the xattr couldn't be read when it wasn't (ENODATA on a portal too old to set it, ENOTSUP/EACCES when the read is refused); for DirectoryUnlistable, Directory is the unlistable directory itself.
public sealed record PathProblem(string Path, PathProblemKind Kind, string? HostPath, int HostPathErrno, string? Directory);

// "Have I seen this problem's directory before?" — the warning row's once-per-directory-per-session gate, so a run of double-clicks into the same hidden folder produces one notice. Problems without a known directory dedupe on the path itself.
public sealed class PathProblemDedupe
{
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);

    public bool IsFirstFor(PathProblem problem)
    {
        if (problem == null)
        {
            throw new ArgumentNullException(nameof(problem));
        }
        return seen.Add(problem.Directory ?? problem.Path);
    }
}

// Rules for recognising document-portal paths and for deciding whether a local path's directory is usable. Everything a flatpak's file forwarding, FileChooser portal, or OpenURI portal hands us for a file outside the sandbox's filesystem grants lands under Root as /run/user/<uid>/doc/<id>/<basename>; the flatpak runtime mounts the app-scoped doc/by-app/<app-id> view over that same root, so the id directory holds exactly one file.
public static class PathPortal
{
    public const string HostPathXattr = "user.document-portal.host-path";

    // $XDG_RUNTIME_DIR/doc, with the systemd default runtime dir as the fallback. Read per call so tests can steer it through the environment the way UserDataPaths' overrides work.
    public static string Root
    {
        get
        {
            var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (string.IsNullOrEmpty(runtimeDir))
            {
                runtimeDir = $"/run/user/{LibC.GetUid()}";
            }
            return Path.Combine(runtimeDir, "doc");
        }
    }

    // Syntactic normalization of a local path, or null when the string isn't one: a URI, or something Path.GetFullPath rejects (empty, embedded NUL — junk a raw drop payload can carry). Both the directory key and the portal rule need the same spelling, so both go through here.
    public static string? TryNormalize(string pathOrUri)
    {
        if (string.IsNullOrEmpty(pathOrUri) || UriShape.LooksLikeUri(pathOrUri))
        {
            return null;
        }
        try
        {
            return Path.GetFullPath(pathOrUri);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"[vompl] path: GetFullPath('{pathOrUri}') failed: {ex.Message}");
            return null;
        }
    }

    // Purely syntactic: is `path` strictly below `portalRoot`? Callers pass a normalized absolute local path (TryNormalize) so `..` segments can't dodge the prefix.
    public static bool IsPortalPath(string path, string portalRoot)
    {
        return path.Length > portalRoot.Length + 1 && path.StartsWith(portalRoot + "/", StringComparison.Ordinal);
    }

    // The real host location of a portal file, from the xattr the portal's FUSE layer exposes; null with the errno when it isn't there or can't be read. Raw: see TryReadOrigin for the usable form.
    public static string? ReadHostPath(string path, out int errno)
    {
        return LibC.GetXattr(path, HostPathXattr, out errno);
    }

    // The origin as a normalized local path, or null when the portal reports none (errno says why) or reports something unusable. The xattr is a C string, so a NUL terminator that made it into the value is dropped before normalizing; every consumer of the origin — resolution, diagnosis, the environment dump's probes — goes through here so none of them ever sees the raw bytes.
    public static string? TryReadOrigin(string portalPath, out int errno)
    {
        var raw = ReadHostPath(portalPath, out errno);
        if (raw == null)
        {
            return null;
        }
        var origin = TryNormalize(raw.TrimEnd('\0'));
        if (origin == null)
        {
            Console.Error.WriteLine($"[vompl] path: portal origin unusable for '{portalPath}': '{raw}'");
        }
        return origin;
    }

    // Can the directory be enumerated? False, quietly, for one that isn't there at all; false with a stderr report for one that exists but refuses listing — the refusal is a finding.
    public static bool DirectoryListable(string directory)
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
        {
            Console.Error.WriteLine($"[vompl] path: directory '{directory}' can't be listed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // Decides whether the directory side of a path is usable for sibling navigation and per-directory memory. `directoryKey` is what TrackPreferences.TryGetDirectoryKey resolved for the same path (null for URIs and portal paths). A portal path is always a problem — even without a host-path xattr, its directory is a one-file FUSE dir. A directory that doesn't exist at all is not reported here: the load itself fails loudly in that case (an existing-but-unlistable one is reported, and DirectoryListable has already written the exception to stderr).
    public static PathProblem? Diagnose(string pathOrUri, string portalRoot, string? directoryKey)
    {
        var full = TryNormalize(pathOrUri);
        if (full == null)
        {
            return null;
        }
        if (IsPortalPath(full, portalRoot))
        {
            var origin = TryReadOrigin(full, out int errno);
            var originDir = origin == null ? null : Path.GetDirectoryName(origin);
            // A portal path still here at load time means the origin was rejected; when its directory is nonetheless reachable, the problem is the file, not the sandbox's view of the folder.
            var kind = originDir != null && DirectoryListable(originDir) ? PathProblemKind.DocumentPortalOriginRejected : PathProblemKind.DocumentPortal;
            return new PathProblem(full, kind, origin, errno, originDir);
        }
        if (directoryKey == null || !Directory.Exists(directoryKey) || DirectoryListable(directoryKey))
        {
            return null;
        }
        return new PathProblem(full, PathProblemKind.DirectoryUnlistable, null, 0, directoryKey);
    }
}
