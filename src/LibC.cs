using System;
using System.Runtime.InteropServices;

namespace Vomplayer;

internal static partial class LibC
{
    private const int LC_NUMERIC = 1;

    [LibraryImport("libc.so.6", EntryPoint = "setlocale", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr Setlocale(int category, string locale);

    // mpv parses numeric option values via strtod etc., which respects LC_NUMERIC. mpv refuses to initialize on non-C LC_NUMERIC. GTK's init calls setlocale(LC_ALL, "") which switches to the user locale; we must re-force LC_NUMERIC=C after gtk_init has run and before mpv_create, not before Main.
    public static void ForceCNumericLocale()
    {
        Setlocale(LC_NUMERIC, "C");
    }
}
