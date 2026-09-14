using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Vomplayer.Util;

namespace Vomplayer.Services;

// The slow half of path diagnostics: a one-shot dump of what the sandbox and the host know about a problem path — the sandbox's view of the portal root and mounts, a rung-by-rung "where does the real path stop being visible" ladder, and host-side probes through flatpak-spawn (OS release, flatpak version and overrides, the exported launcher, the portal's document table, mounts, and the file's own realpath/stat/findmnt). Runs once per distinct problem directory per process, on a background thread, and only when the diagnostics log is open (there's nowhere else for it to go). Fire-and-forget: a fast app exit abandons whatever hasn't been appended yet; every section already appended survives.
public static class DiagnosticsEnvironment
{
    // VOMPL_LOG_PATHS=1 dumps once at launch even without a problem (`flatpak override --user --env=VOMPL_LOG_PATHS=1 <app-id>` reaches launcher-started instances).
    public static readonly bool DumpEveryLaunch = Environment.GetEnvironmentVariable("VOMPL_LOG_PATHS") == "1";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly object gate = new();
    private static readonly PathProblemDedupe dedupe = new();
    private static bool dumping;

    public static void Dump(PathProblem? trigger)
    {
        if (!DiagnosticsLog.IsOpen)
        {
            return;
        }
        lock (gate)
        {
            // Two chains interleaving their sections would make the log unreadable, so a dump that arrives mid-dump is skipped — checked before the dedupe is consumed, so the next load from that directory still gets its dump.
            if (dumping)
            {
                DiagnosticsLog.Append($"--- environment dump skipped (one already running) trigger={trigger?.Path ?? "launch"}");
                return;
            }
            if (trigger != null && !dedupe.IsFirstFor(trigger))
            {
                return;
            }
            dumping = true;
        }
        var thread = new Thread(() =>
        {
            try
            {
                Collect(trigger);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[vompl] diagnostics environment dump failed: {ex}");
                DiagnosticsLog.Append($"--- environment dump failed: {ex}");
            }
            finally
            {
                lock (gate)
                {
                    dumping = false;
                }
            }
        });
        thread.IsBackground = true;
        thread.Name = "vompl-diagnostics-dump";
        thread.Start();
    }

    private static void Collect(PathProblem? trigger)
    {
        var sandboxed = FlatpakDetect.IsSandboxed();
        var triggerText = trigger == null ? "trigger=launch" : $"trigger={trigger.Kind} path={trigger.Path}";
        var portalRoot = PathPortal.Root;
        DiagnosticsLog.Append($"--- environment dump {DiagnosticsLog.Stamp(DateTimeOffset.UtcNow)} {triggerText} sandboxed={sandboxed}");

        DiagnosticsLog.Append($"[sandbox: portal root {portalRoot}] (the root is a symlink into /run/flatpak/doc, followed; inside a flatpak that is the app-scoped doc/by-app/<app-id> view, so this lists every document ever forwarded to this app)\n" + HostCommand.Run("ls", new[] { "-laL", portalRoot }, ProbeTimeout));
        DiagnosticsLog.Append("[sandbox: /proc/self/mountinfo]\n" + ReadTextOrError("/proc/self/mountinfo"));

        string? hostPath = trigger?.HostPath;
        if (trigger?.Kind == PathProblemKind.DirectoryUnlistable)
        {
            hostPath = trigger.Path;
        }
        if (hostPath != null)
        {
            DiagnosticsLog.Append("[sandbox: visibility ladder for " + hostPath + "] (which prefix of the real path the sandbox can still reach)\n" + VisibilityLadder(hostPath));
        }

        var appId = AppIdentity.FlatpakId;
        var probes = new List<IReadOnlyList<string>>
        {
            new[] { "cat", "/etc/os-release" },
            new[] { "flatpak", "--version" },
            new[] { "flatpak", "override", "--user", "--show", appId },
            new[] { "flatpak", "override", "--show", appId },
            new[] { "sh", "-c", "/usr/lib/xdg-desktop-portal --version 2>&1 || /usr/libexec/xdg-desktop-portal --version 2>&1" },
            new[] { "sh", "-c", $"grep -H '^Exec=' ~/.local/share/flatpak/exports/share/applications/{appId}.desktop /var/lib/flatpak/exports/share/applications/{appId}.desktop 2>&1" },
            new[] { "sh", "-c", "which dolphin; flatpak list --app --columns=application" },
            // A --file-forwarding export and a FileChooser / OpenURI export record different requesting apps and permissions here: this is the table that says which route handed us the file.
            new[] { "flatpak", "documents", appId, "--columns=id,path,permissions" },
        };
        if (sandboxed)
        {
            probes.Add(new[] { "cat", "/proc/self/mountinfo" });
        }
        if (hostPath != null)
        {
            var hostDir = Path.GetDirectoryName(hostPath) ?? hostPath;
            probes.Add(new[] { "sh", "-c", "t=$(xdg-mime query filetype \"$1\"); echo \"filetype=$t\"; echo \"default=$(xdg-mime query default \"$t\")\"", "sh", hostPath });
            probes.Add(new[] { "realpath", "-e", hostPath });
            probes.Add(new[] { "stat", hostPath });
            probes.Add(new[] { "findmnt", "-T", hostPath });
            probes.Add(new[] { "sh", "-c", "ls -la \"$1\" | head -60", "sh", hostDir });
        }
        foreach (var probe in probes)
        {
            DiagnosticsLog.Append((sandboxed ? "[host] " : "[local] ") + HostCommand.RunOnHost(probe, ProbeTimeout));
        }
        DiagnosticsLog.Append("--- environment dump complete");
    }

    // One line per prefix of the path's directory, root first: absent / listable / exists-but-unlistable (with the exception), plus the realpath spelling when a rung is a symlink, then whether the file itself is there. The deepest listable rung is where the sandbox's mapping of the real path ends. The path is syntactically normalized first so a relative or `..`-bearing spelling can't produce rungs that don't exist.
    internal static string VisibilityLadder(string hostPath)
    {
        var sb = new StringBuilder();
        hostPath = Path.GetFullPath(hostPath);
        var dir = Path.GetDirectoryName(hostPath) ?? "/";
        var parts = dir.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var rung = "/";
        for (int i = 0; i <= parts.Length; i++)
        {
            if (i > 0)
            {
                rung = rung.TrimEnd('/') + "/" + parts[i - 1];
            }
            var real = LibC.RealPath(rung, out int realErrno);
            string listing;
            if (!Directory.Exists(rung))
            {
                // The errno is what separates a missing rung (ENOENT) from a stale network mount root (ESTALE) or a permission wall (EACCES): three different diagnoses from one word otherwise. realpath succeeding on a non-directory means the rung exists as a file.
                listing = realErrno == 0 ? "not a directory" : $"absent (errno {realErrno})";
            }
            else
            {
                try
                {
                    using var entries = Directory.EnumerateFileSystemEntries(rung).GetEnumerator();
                    listing = entries.MoveNext() ? "listable" : "listable (empty)";
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                {
                    listing = $"exists but not listable: {ex.GetType().Name}: {ex.Message}";
                }
            }
            var realNote = real != null && real != rung ? $" (realpath {real})" : "";
            sb.Append($"{rung}: {listing}{realNote}\n");
        }
        sb.Append($"{hostPath}: file {(File.Exists(hostPath) ? "exists" : "absent")}");
        return sb.ToString();
    }

    private static string ReadTextOrError(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return $"<read failed: {ex.GetType().Name}: {ex.Message}>";
        }
    }
}
