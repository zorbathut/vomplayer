using System;
using System.Threading.Tasks;
using Vomplayer.UserData;

namespace Vomplayer;

public static class Program
{
    public static int Main(string[] args)
    {
        // GLib log diagnostics. Install before any GTK / GLib call so we own the writer slot exclusively (g_log_set_writer_func g_error's on a second call). Adds a managed stack trace to every ERROR / CRITICAL — most importantly to the abort path used by g_assert, where without this we'd see only the GLib message and no clue which C# code path was active.
        GLibLogDiag.Install();

        // Cheap last-resort safety nets for the never-swallow policy. Most surfacing already works through GirCore's MainLoopSynchronizationContext (which catches Post exceptions and routes them to GLib.UnhandledException → stderr + Environment.Exit(1)) — so async-RelayCommand faults and signal-handler throws are already loud by default. These two handlers cover the residual cases the SyncContext doesn't see: finalizer-thread exceptions and faulted Tasks that get GC'd while still unobserved.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Console.Error.WriteLine($"[vomplayer] UNHANDLED EXCEPTION (terminating={e.IsTerminating}): {ex?.ToString() ?? e.ExceptionObject?.ToString() ?? "<null>"}");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Console.Error.WriteLine($"[vomplayer] UNOBSERVED TASK EXCEPTION: {e.Exception}");
            e.SetObserved();
        };

        // Local argv check — reject unknown flags up front rather than relying on GApplication's parse. Stops typo'd flags from reaching the primary or silently being treated as a file path.
        foreach (var a in args)
        {
            if (a.StartsWith('-'))
            {
                Console.Error.WriteLine($"[vomplayer] unknown flag: {a}");
                return 2;
            }
        }

        // Config loads before gtk_init — the file is plain text and the deserializer doesn't touch any GTK API. MainWindow turns the string-form bindings into a HotkeyMap on the GTK main thread, after init, when gtk_accelerator_parse is safe to call.
        var configPath = UserDataPaths.ConfigFile;
        var userConfig = UserConfig.LoadOrDefault(configPath);

        var flags = StartupHelpers.ComputeAppFlags(userConfig.Application.SingleInstance);
        var app = Gtk.Application.New("io.github.zorbathut.vomplayer", flags);

        // Build the argv we feed into Run(). g_application_run expects argv[0] to be the program name and consumes it; .NET Main's `args` already had argv[0] stripped, so we prepend a placeholder. The forwarded-to-primary path will see this same argv minus argv[0] in OnCommandLine.
        var runArgs = new string[args.Length + 1];
        runArgs[0] = "vomplayer";
        Array.Copy(args, 0, runArgs, 1, args.Length);

        // Register before opening state.db so a remote process can forward + exit without ever touching SQLite. g_application_register is idempotent — Run() will re-register internally with no effect.
        try
        {
            if (!app.Register(null))
            {
                Console.Error.WriteLine("[vomplayer] g_application_register returned false; cannot register on the session bus (is DBus available?)");
                return 3;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vomplayer] g_application_register threw: {ex.Message} (is DBus available?)");
            return 3;
        }

        if (app.IsRemote)
        {
            // Remote: forward the command line to the primary and exit. GApplication's local_command_line should forward via D-Bus and return synchronously, but GirCore 0.7.0's Application.Run wrapper doesn't return on a remote — the process hangs indefinitely after the D-Bus call completes. Workaround: spawn a background thread that force-exits the process shortly after Run starts. By then the synchronous D-Bus CommandLine call has been delivered and the primary's OnCommandLine handler has fired (verified empirically). The 500 ms window is the round-trip budget; on a saturated session bus it might need bumping.
            var quitter = new System.Threading.Thread(() =>
            {
                System.Threading.Thread.Sleep(500);
                Environment.Exit(0);
            });
            quitter.IsBackground = true;
            quitter.Start();
            return app.Run(runArgs);
        }

        // Primary path. Owned by Main so the SQLite connection is closed cleanly after the GTK main loop exits — including on abnormal exit, since the using block fires on any control-flow path.
        using var stateDb = StateDatabase.Open(UserDataPaths.StateDb);
        var recentFiles = new RecentFiles(stateDb.Connection);
        var savedPlaylists = new SavedPlaylists(stateDb.Connection);
        var trackPreferences = new TrackPreferences(stateDb.Connection);

        // Captured by OnCommandLine so the first-launch handler builds the window and subsequent remote-forwards reuse it. Today there's at most one window per process; if multi-window ever lands, both branches still apply.
        MainWindow? window = null;
        app.OnCommandLine += (sender, signalArgs) =>
        {
            var cmd = signalArgs.CommandLine;
            var forwardedArgv = cmd.GetArguments(out _);
            var cwd = cmd.GetCwd();
            var paths = StartupHelpers.ResolveCommandLineFiles(forwardedArgv, cwd, msg => Console.Error.WriteLine($"[vomplayer] {msg}"));
            if (window == null)
            {
                // First-launch path (whether primary started with no args, with a file, or got a forwarded command line). Pass paths[0] as initialFile so OnRenderContextReady consumes it through Primary.OpenFile; extra files are dropped, matching the historical "one positional arg" behavior.
                window = BuildAndPresent((Gtk.Application)sender, recentFiles, savedPlaylists, trackPreferences, userConfig, configPath, paths.Count > 0 ? paths[0] : null);
            }
            else if (paths.Count > 0)
            {
                window.LoadPathsExternal(paths);
                window.Present();
            }
            else
            {
                // No-arg re-activation while the window already exists — just raise.
                window.Present();
            }
            return 0;
        };

        return app.RunWithSynchronizationContext(runArgs);
    }

    private static MainWindow BuildAndPresent(Gtk.Application app, IRecentFiles recentFiles, ISavedPlaylists savedPlaylists, ITrackPreferences trackPreferences, UserConfig userConfig, string configPath, string? initialFile)
    {
        // gtk_init ran setlocale(LC_ALL, "") already; force LC_NUMERIC=C back before any mpv call. Must happen on the main thread after GTK init, not before Main.
        LibC.ForceCNumericLocale();

        var playback = new Playback.Playback(
            a => Util.IdleSafe.Add((int)GLib.Constants.PRIORITY_DEFAULT_IDLE, a));
        playback.Initialize();

        // Safety-net dispose for exit paths that bypass MainWindow.OnWindowCloseRequest (any future app.Quit() trigger, or a normal-but-non-window-close shutdown). On the typical close-the-window exit, OnWindowCloseRequest's chain has already disposed primary playback via Primary VideoContext.Dispose; this call is a no-op via the MpvDispatcher's idempotent disposed flag. Doesn't cover SIGKILL or process abort — those are OS-level concerns .NET can't intercept.
        app.OnShutdown += (_, _) => playback.Dispose();

        var window = new MainWindow(app, playback, recentFiles, savedPlaylists, trackPreferences, userConfig, configPath, initialFile);
        window.Present();
        return window;
    }
}
