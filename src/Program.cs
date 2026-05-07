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

        string? initialFile = null;
        foreach (var a in args)
        {
            if (a.StartsWith('-'))
            {
                Console.Error.WriteLine($"[vomplayer] unknown flag: {a}");
                return 2;
            }
            else
            {
                initialFile = a;
            }
        }

        // Config loads before gtk_init — the file is plain text and the deserializer doesn't touch any GTK API. MainWindow turns the string-form bindings into a HotkeyMap on the GTK main thread, after init, when gtk_accelerator_parse is safe to call.
        var configPath = UserDataPaths.ConfigFile;
        var userConfig = UserConfig.LoadOrDefault(configPath);
        // Owned by Main so the SQLite connection is closed cleanly after the GTK main loop exits — including on abnormal exit, since the using block fires on any control-flow path. Shared across activations if the NonUnique app ever re-activates within one process; today that's a single window per process, but co-locating recents across hypothetical multi-window matches user intent.
        using var stateDb = StateDatabase.Open(UserDataPaths.StateDb);
        var recentFiles = new RecentFiles(stateDb.Connection);
        var trackPreferences = new TrackPreferences(stateDb.Connection);

        var app = Gtk.Application.New("io.github.zorbathut.vomplayer", Gio.ApplicationFlags.NonUnique);
        app.OnActivate += (sender, _) =>
        {
            BuildAndPresent((Gtk.Application)sender, recentFiles, trackPreferences, userConfig, configPath, initialFile);
        };
        return app.RunWithSynchronizationContext(null);
    }

    private static void BuildAndPresent(Gtk.Application app, IRecentFiles recentFiles, ITrackPreferences trackPreferences, UserConfig userConfig, string configPath, string? initialFile)
    {
        // gtk_init ran setlocale(LC_ALL, "") already; force LC_NUMERIC=C back before any mpv call. Must happen on the main thread after GTK init, not before Main.
        LibC.ForceCNumericLocale();

        var playback = new Playback.Playback(
            a => Util.IdleSafe.Add((int)GLib.Constants.PRIORITY_DEFAULT_IDLE, a));
        playback.Initialize();

        var window = new MainWindow(app, playback, recentFiles, trackPreferences, userConfig, configPath, initialFile);
        window.Present();
    }
}
