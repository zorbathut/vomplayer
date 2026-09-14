using System;
using System.IO;

namespace Vomplayer.Util;

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

    // The real host location of a portal file, from the xattr the portal's FUSE layer exposes; null with the errno when it isn't there or can't be read.
    public static string? ReadHostPath(string path, out int errno)
    {
        return LibC.GetXattr(path, HostPathXattr, out errno);
    }
}
