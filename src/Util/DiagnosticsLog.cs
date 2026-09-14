using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Vomplayer.Util;

// Append-only, human-readable record of how files reach the app and what the sandbox can see of them — the thing to copy off a machine that misbehaves. Everything written here is cheap (a few lines per launch and per load); the slow environment dump lives in Services.DiagnosticsEnvironment and appends through the same sink. One static writer behind a lock; every section is a separate flushed append so a crash-exit keeps everything written so far. Unopened (tests, or before Program opens it) the file side is inert and only the stderr mirror speaks. Several "open in new window" processes share one file with only a per-process lock, so their sections may interleave; the per-line stamps and pids keep that readable.
public static class DiagnosticsLog
{
    // Rotation happens only at a launch banner, so one launch's sections — an environment dump can be a few hundred KB — are never split across the two files.
    internal const int MaxBytes = 4 * 1024 * 1024;
    // How many recent-file rows the launch banner lists: enough to show what paths looked like across the last several sessions.
    public const int LaunchRecentsCount = 30;

    private static readonly object gate = new();
    private static string? logPath;

    public static void Open(string path)
    {
        if (path == null)
        {
            throw new ArgumentNullException(nameof(path));
        }
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                logPath = path;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                Console.Error.WriteLine($"[vompl] diagnostics log unavailable ({path}): {ex.Message}");
            }
        }
    }

    public static bool IsOpen
    {
        get
        {
            lock (gate)
            {
                return logPath != null;
            }
        }
    }

    internal static void Close()
    {
        lock (gate)
        {
            logPath = null;
        }
    }

    public static void Append(string section)
    {
        if (section == null)
        {
            throw new ArgumentNullException(nameof(section));
        }
        lock (gate)
        {
            if (logPath == null)
            {
                return;
            }
            try
            {
                File.AppendAllText(logPath, section + "\n");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[vompl] diagnostics log write failed ({logPath}): {ex.Message}");
            }
        }
    }

    public static void WriteLaunch(IReadOnlyList<string> recentPaths, IReadOnlyList<string> preferenceDirs)
    {
        RotateIfOverCap();
        string? flatpakInfo = null;
        if (File.Exists("/.flatpak-info"))
        {
            flatpakInfo = File.ReadAllText("/.flatpak-info");
        }
        var env = new[] { "XDG_RUNTIME_DIR", "HOME", "FLATPAK_ID", "XDG_DATA_HOME", "VOMPL_STATE_DIR", "VOMPL_LOG_PATHS" }
            .Select(name => new KeyValuePair<string, string?>(name, Environment.GetEnvironmentVariable(name)))
            .ToList();
        Append(FormatLaunch(DateTimeOffset.UtcNow, Environment.ProcessId, flatpakInfo, env, recentPaths, preferenceDirs));
    }

    private static void RotateIfOverCap()
    {
        lock (gate)
        {
            if (logPath == null)
            {
                return;
            }
            try
            {
                var info = new FileInfo(logPath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(logPath, logPath + ".1", overwrite: true);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[vompl] diagnostics log rotation failed ({logPath}): {ex.Message}");
            }
        }
    }

    internal static string FormatLaunch(DateTimeOffset now, int pid, string? flatpakInfo, IReadOnlyList<KeyValuePair<string, string?>> env, IReadOnlyList<string> recentPaths, IReadOnlyList<string> preferenceDirs)
    {
        var sb = new StringBuilder();
        sb.Append($"=== launch {Stamp(now)} pid {pid} ===\n");
        sb.Append("This file may contain: recently played paths, remembered directories, host mount tables, exported document paths, and installed flatpak app ids. Review before sharing.\n");
        sb.Append("[/.flatpak-info]\n");
        sb.Append(flatpakInfo ?? "<absent: not running in a flatpak sandbox>\n");
        sb.Append("[environment]\n");
        foreach (var kv in env)
        {
            sb.Append($"{kv.Key}={kv.Value ?? "<unset>"}\n");
        }
        sb.Append($"[recent files, newest first ({recentPaths.Count})]\n");
        foreach (var p in recentPaths)
        {
            sb.Append(p).Append('\n');
        }
        sb.Append($"[track-preference directories ({preferenceDirs.Count})]\n");
        foreach (var d in preferenceDirs)
        {
            sb.Append(d).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    // A raw external arrival — argv, a drop payload, a picker result — before any conversion, so the log shows exactly what the outside world handed us.
    public static void Arrival(string source, string detail)
    {
        Console.Error.WriteLine($"[vompl] path: arrival {source}: {detail.Replace("\r", "").Replace("\n", " | ")}");
        Append($"--- arrival {Stamp(DateTimeOffset.UtcNow)} source={source}\n{detail.TrimEnd('\n')}");
    }

    // Per load: the path the playlist holds, the directory key it resolved to, the realpath spelling of that directory, and whether saved preferences exist under either spelling.
    public static void Load(string pathOrUri, string? directoryKey, IReadOnlyList<string> knownDirectories)
    {
        string? realDir = null;
        int errno = 0;
        bool keyKnown = false;
        bool realKnown = false;
        if (directoryKey != null)
        {
            realDir = LibC.RealPath(directoryKey, out errno);
            keyKnown = knownDirectories.Contains(directoryKey);
            realKnown = realDir != null && knownDirectories.Contains(realDir);
        }
        var text = FormatLoad(pathOrUri, directoryKey, realDir, keyKnown, realKnown, errno);
        Console.Error.WriteLine($"[vompl] path: {string.Join(" | ", text.Split('\n').Select(line => line.Trim()))}");
        Append($"--- load {Stamp(DateTimeOffset.UtcNow)}\n{text}");
    }

    internal static string FormatLoad(string pathOrUri, string? directoryKey, string? realDir, bool keyKnown, bool realKnown, int realPathErrno)
    {
        string realText;
        if (directoryKey == null)
        {
            realText = "<n/a>";
        }
        else if (realDir == null)
        {
            realText = $"<realpath failed: errno {realPathErrno}>";
        }
        else
        {
            realText = realDir;
        }
        return $"load path={pathOrUri}\n  directory-key={directoryKey ?? "<none>"}\n  realpath-directory={realText}\n  preferences-saved-under: key={keyKnown} realpath={realKnown}";
    }

    public static void Problem(PathProblem problem)
    {
        if (problem == null)
        {
            throw new ArgumentNullException(nameof(problem));
        }
        var hostText = problem.HostPath ?? $"<none: errno {problem.HostPathErrno}>";
        var text = $"problem kind={problem.Kind} path={problem.Path} host-path={hostText} directory={problem.Directory ?? "<none>"}";
        Console.Error.WriteLine($"[vompl] path: {text}");
        Append($"--- {text}");
    }

    public static string Stamp(DateTimeOffset when)
    {
        return when.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
    }
}
