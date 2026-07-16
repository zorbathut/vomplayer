using System;
using System.Collections.Generic;
using System.IO;

namespace Vomplayer;

// Pure-function helpers used by Program.Main. Extracted so they can be unit-tested without a GTK main loop.
internal static class StartupHelpers
{
    // Translates the user-facing "open files in a new window" toggle into the GApplication flag set we register with. HandlesCommandLine is used (rather than HandlesOpen) because GirCore 0.7.0's OpenSignalArgs.Files getter currently throws on the GFile[] marshalling — the command-line signal hands us a plain string[] which works around that.
    //   reuse window (default): HandlesCommandLine alone → GApplication claims the bus name; second invocations forward their command line via D-Bus to the primary's OnCommandLine.
    //   new window:             HandlesCommandLine | NonUnique → GApplication never tries to be remote; every process is its own primary. HandlesCommandLine is still set so OnCommandLine is the single file-load entry point on both modes.
    internal static Gio.ApplicationFlags ComputeAppFlags(bool openInNewWindow)
    {
        var flags = Gio.ApplicationFlags.HandlesCommandLine;
        if (openInNewWindow)
        {
            flags |= Gio.ApplicationFlags.NonUnique;
        }
        return flags;
    }

    // Translates a forwarded command-line (argv as it left the remote, plus the remote's cwd) into the list of files the running primary should load. Each positional arg is rooted against the caller's cwd if it's not already absolute and doesn't have a URI scheme (so `./foo.mp4` from another shell reaches the right file). The flag-rejection loop that exists in Program.Main is duplicated here for the remote-arg path — a forwarded command line might come from a future remote that was launched with different flag-acceptance rules.
    internal static IReadOnlyList<string> ResolveCommandLineFiles(IReadOnlyList<string> argv, string? cwd, Action<string> onSkip)
    {
        if (argv == null)
        {
            throw new ArgumentNullException(nameof(argv));
        }
        if (onSkip == null)
        {
            throw new ArgumentNullException(nameof(onSkip));
        }
        var result = new List<string>();
        // argv[0] is the program name (g_application_run passes it through); skip it.
        for (int i = 1; i < argv.Count; i++)
        {
            var a = argv[i];
            if (string.IsNullOrEmpty(a))
            {
                continue;
            }
            if (a.StartsWith('-'))
            {
                onSkip($"unknown flag from forwarded command line: {a}");
                continue;
            }
            result.Add(RootRelativePath(a, cwd));
        }
        return result;
    }

    // Resolves a positional arg to an absolute path or URI. Local paths get rooted against the caller's cwd; URIs are returned unchanged so OpenFile can route them through the URL load path.
    private static string RootRelativePath(string arg, string? cwd)
    {
        if (Util.UriShape.LooksLikeUri(arg))
        {
            return arg;
        }
        if (Path.IsPathRooted(arg) || string.IsNullOrEmpty(cwd))
        {
            return arg;
        }
        return Path.GetFullPath(Path.Combine(cwd, arg));
    }
}
