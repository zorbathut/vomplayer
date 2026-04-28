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

        // Loaded but not yet consumed — UserConfig.Placeholder is a throwaway slot while the config plumbing settles. Real read sites will appear here as user-facing settings land.
        _ = UserConfig.LoadOrDefault(UserDataPaths.ConfigFile);
        // Owned by Main so the SQLite connection is closed cleanly after the GTK main loop exits — including on abnormal exit, since the using block fires on any control-flow path. Shared across activations if the NonUnique app ever re-activates within one process; today that's a single window per process, but co-locating recents across hypothetical multi-window matches user intent.
        using var recentFiles = RecentFiles.Open(UserDataPaths.StateDb);

        var app = Gtk.Application.New("net.vomplayer.Vomplayer", Gio.ApplicationFlags.NonUnique);
        app.OnActivate += (sender, _) =>
        {
            BuildAndPresent((Gtk.Application)sender, recentFiles, initialFile, forceSdr);
        };
        return app.RunWithSynchronizationContext(null);
    }

    private static void BuildAndPresent(Gtk.Application app, IRecentFiles recentFiles, string? initialFile, bool forceSdr)
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

        var window = new MainWindow(app, playback, recentFiles, initialFile, forceSdr);
        window.Present();
    }
}
