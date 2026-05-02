using System;
using Vomplayer.UserData;

namespace Vomplayer;

public static class Program
{
    public static int Main(string[] args)
    {
        string? initialFile = null;
        // Per-video HDR autodetect is the default; --sdr / --no-hdr forces SDR for the whole session (useful when the display isn't in HDR mode and even correctly-tagged PQ output would display wrong).
        bool forceSdr = false;
        foreach (var a in args)
        {
            if (a == "--sdr" || a == "--no-hdr")
            {
                forceSdr = true;
            }
            else if (a.StartsWith('-'))
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
            BuildAndPresent((Gtk.Application)sender, recentFiles, trackPreferences, userConfig, configPath, initialFile, forceSdr);
        };
        return app.RunWithSynchronizationContext(null);
    }

    private static void BuildAndPresent(Gtk.Application app, IRecentFiles recentFiles, ITrackPreferences trackPreferences, UserConfig userConfig, string configPath, string? initialFile, bool forceSdr)
    {
        // gtk_init ran setlocale(LC_ALL, "") already; force LC_NUMERIC=C back before any mpv call. Must happen on the main thread after GTK init, not before Main.
        LibC.ForceCNumericLocale();

        var playback = new Playback.Playback(
            a => GLib.Functions.IdleAdd(
                (int)GLib.Constants.PRIORITY_DEFAULT_IDLE,
                () =>
                {
                    a();
                    return false;
                }));
        playback.Initialize();

        var window = new MainWindow(app, playback, recentFiles, trackPreferences, userConfig, configPath, initialFile, forceSdr);
        window.Present();
    }
}
