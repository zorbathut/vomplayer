using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Vomplayer.Services;

// How the app runs commands on the host system, and — for the diagnostics dump — how one command's whole story (command line, stdout, stderr, how it ended) is rendered as text so a probe that fails or hangs is still a line in the log rather than a missing section.
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

    public static string RunOnHost(IReadOnlyList<string> command, TimeSpan timeout)
    {
        var argv = Build(FlatpakDetect.IsSandboxed(), command);
        return Run(argv[0], argv.Skip(1), timeout);
    }

    public static string Run(string exe, IEnumerable<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        var header = "$ " + exe + (psi.ArgumentList.Count > 0 ? " " + string.Join(" ", psi.ArgumentList) : "");
        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
        {
            return $"{header}\n(failed to start: {ex.Message})";
        }
        if (process == null)
        {
            return $"{header}\n(failed to start: Process.Start returned null)";
        }
        using (process)
        {
            // A probe that reads stdin must see EOF, not block on whatever the app inherited.
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            string ending;
            if (process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                ending = $"(exit {process.ExitCode})";
            }
            else
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The process exited between the timeout and the kill — the state we wanted anyway.
                }
                ending = $"(timed out after {timeout.TotalSeconds:F1}s, killed)";
            }
            // Bounded wait for the pipe readers: after a kill a grandchild could still hold the pipe open, and this text is all we get from a probe — a partial capture beats hanging the dump. A reader still pending when the process is disposed faults on the closed stream; that fault is expected and reported below as "not fully captured", so observe it rather than let it surface later as an unobserved-task exception.
            try
            {
                Task.WaitAll(new Task[] { stdout, stderr }, TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // A reader that faulted inside the window is not a probe failure: AppendCaptured renders it as "not fully captured" and observes the fault. Run's contract is that a probe always yields a line, never an exception.
            }
            var sb = new System.Text.StringBuilder();
            sb.Append(header).Append('\n');
            AppendCaptured(sb, stdout, "stdout");
            AppendCaptured(sb, stderr, "stderr");
            sb.Append(ending);
            return sb.ToString();
        }
    }

    private static void AppendCaptured(System.Text.StringBuilder sb, Task<string> capture, string name)
    {
        if (!capture.IsCompletedSuccessfully)
        {
            capture.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            sb.Append($"({name} not fully captured)\n");
            return;
        }
        var text = capture.Result;
        if (text.Length == 0)
        {
            return;
        }
        if (name == "stderr")
        {
            sb.Append("[stderr]\n");
        }
        sb.Append(text);
        if (!text.EndsWith('\n'))
        {
            sb.Append('\n');
        }
    }
}
