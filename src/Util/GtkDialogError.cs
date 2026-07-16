using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Vomplayer.Util;

// Decides whether a GLib.GException from Gtk.FileDialog means "the user dismissed the dialog" (GError domain gtk-dialog-error, code GTK_DIALOG_ERROR_DISMISSED = 2) — the one quiet no-selection case — versus a real failure that must propagate. GirCore 0.7.0's GException exposes only the message publicly; the wrapped GError's domain/code live on the private _errorHandle field, read here via cached reflection (the handle type's GetDomain/GetCode accessors are public). Falls back to the historical message-substring check if GirCore's internals ever move, so a rename degrades to the old heuristic instead of misclassifying every cancel as an error — and the reflection contract is pinned by a test so the degradation is loud at upgrade time.
public static partial class GtkDialogError
{
    private const int DismissedCode = 2; // GTK_DIALOG_ERROR_DISMISSED

    private static readonly FieldInfo? ErrorHandleField = typeof(GLib.GException).GetField("_errorHandle", BindingFlags.NonPublic | BindingFlags.Instance);
    private static uint dialogErrorQuark;

    public static bool IsDismissed(GLib.GException ex)
    {
        if (ErrorHandleField?.GetValue(ex) is GLib.Internal.ErrorHandle handle)
        {
            if (dialogErrorQuark == 0)
            {
                dialogErrorQuark = g_quark_try_string("gtk-dialog-error");
            }
            // Quark 0 means the domain string was never interned in this process — impossible once GTK has thrown a dialog error, but if it somehow happens, fall through to the message check rather than classifying a real error as a dismissal.
            if (dialogErrorQuark != 0)
            {
                return handle.GetDomain() == dialogErrorQuark && handle.GetCode() == DismissedCode;
            }
        }
        return ex.Message != null && ex.Message.Contains("Dismissed", StringComparison.OrdinalIgnoreCase);
    }

    // Same explicit-SONAME convention as the rest of the tree (see Hotkeys.Native).
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_quark_try_string", StringMarshalling = StringMarshalling.Utf8)]
    private static partial uint g_quark_try_string(string str);
}
