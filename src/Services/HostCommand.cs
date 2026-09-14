using System;
using System.Collections.Generic;

namespace Vomplayer.Services;

// How the app runs commands on the host system.
public static class HostCommand
{
    // The argv that reaches a host-side command from wherever we are. Inside a flatpak sandbox the host is reached via flatpak-spawn --host; --watch-bus binds the host process to flatpak-spawn's D-Bus connection, so when the in-sandbox flatpak-spawn is killed the kernel closes its FDs (including the bus socket) and the portal tears down the host process — a cancelled yt-dlp download or a timed-out probe leaves no orphan, and neither does an app crash. (flatpak-spawn forwards no signals; the bus-drop is what does the work — see flatpak/flatpak#4827.) Pure so it's unit-testable; the environment probe lives in FlatpakDetect.IsSandboxed.
    public static IReadOnlyList<string> Build(bool inFlatpak, IReadOnlyList<string> command)
    {
        if (command == null || command.Count == 0)
        {
            throw new ArgumentException("command must name an executable", nameof(command));
        }
        if (!inFlatpak)
        {
            return command;
        }
        var argv = new List<string> { "flatpak-spawn", "--host", "--watch-bus" };
        argv.AddRange(command);
        return argv;
    }
}
