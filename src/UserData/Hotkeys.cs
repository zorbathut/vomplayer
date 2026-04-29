using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Vomplayer.UserData;

// User-bindable actions. Order here is the order they appear in the preferences UI; keep it stable so saved configs round-trip predictably.
public enum HotkeyAction
{
    Open,
    Quit,
    PlayPause,
    ToggleFullscreen,
    // Conditional: only consumes the key when actually fullscreen. Bound to Escape by default; an unconditional key controller would steal Escape from any future popup/dialog/text-entry that legitimately wants it.
    ExitFullscreen,
    ToggleDiagnosticOverlay,
    ShowPreferences,
    SeekBack5,
    SeekForward5,
    SeekBack10,
    SeekForward10,
    FrameStepBack,
    FrameStepForward,
    SeekStart,
    SeekEnd,
    ChapterPrev,
    ChapterNext,
    VolumeUp,
    VolumeDown,
    ToggleMute,
}

// A single input trigger — either a key combo or a mouse click. Stored as text in TOML, parsed at load.
//
// Key triggers use GTK's accelerator syntax (e.g. "f", "F11", "<Primary>O", "space", "Escape") so the existing user-mental-model from menu accelerators carries over. Modifiers from the parser are masked against gtk_accelerator_get_default_mod_mask so platform-specific quirks (NumLock, CapsLock, etc.) don't poison comparisons. Keyvals are canonicalized to their lowercase form via gdk_keyval_to_lower at every entry point — parser, defaults map, live events from the key controller, dialog capture — so `<Shift>F` (parser yields KEY_F) and a live Shift+f event (delivered as KEY_F) compare equal, and so CapsLock-on `f` (delivered as KEY_F+LockMask) lines up with a `f` binding after the Lock bit is masked out. Without this canonicalization the default `<Shift>F` binding never fires and CapsLock makes plain-letter bindings stop matching.
//
// Mouse triggers use a custom syntax: "MouseClick<button>" / "MouseDoubleClick<button>" where button is the Gdk button index (1=primary, 2=middle, 3=secondary). Single-click and double-click are tracked separately so the user can bind different actions to each — GTK's GestureClick fires `pressed` for each press in a sequence with NPress incrementing, so we choose the binding by NPress at dispatch time. This means "single click" actions fire once on the first press of a double-click sequence too; that's an inherent GestureClick property and the existing double-click-toggle-fullscreen behavior already lives with it.
public abstract partial record Trigger
{
    public abstract string Format();

    public sealed record Key(uint Keyval, Gdk.ModifierType Modifiers) : Trigger
    {
        public override string Format()
        {
            return Native.AcceleratorName(Keyval, Modifiers);
        }
    }

    public sealed record MouseClick(uint Button, int ClickCount) : Trigger
    {
        public override string Format()
        {
            string prefix = ClickCount >= 2 ? "MouseDoubleClick" : "MouseClick";
            return $"{prefix}{Button}";
        }
    }

    // Canonical-form factory — every Key trigger from outside this file should go through this so the keyval and modifier mask are normalized identically. Required because gtk_accelerator_parse and live events disagree on letter-key casing: parser-side, "<Shift>F" yields keyval=KEY_F and live events deliver the same; but plain "f" yields keyval=KEY_f and a live `f` event also delivers KEY_f. The discrepancy bites when CapsLock is held — the live keyval flips to uppercase even with no Shift modifier — and we need bindings to remain stable. Canonicalizing both sides to lowercase resolves all three cases at once.
    public static Key MakeKey(uint keyval, Gdk.ModifierType modifiers)
    {
        var defaultMask = Native.AcceleratorGetDefaultModMask();
        return new Key(Native.KeyvalToLower(keyval), modifiers & defaultMask);
    }

    // Returns null on a malformed string so callers (config loader, dialog input) can choose to skip-and-log rather than crash.
    public static Trigger? TryParse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        text = text.Trim();
        if (text.StartsWith("MouseDoubleClick", StringComparison.Ordinal))
        {
            return TryParseMouse(text.AsSpan("MouseDoubleClick".Length), clickCount: 2);
        }
        if (text.StartsWith("MouseClick", StringComparison.Ordinal))
        {
            return TryParseMouse(text.AsSpan("MouseClick".Length), clickCount: 1);
        }
        if (Native.TryParseAccelerator(text, out uint key, out Gdk.ModifierType mods))
        {
            return MakeKey(key, mods);
        }
        return null;
    }

    private static MouseClick? TryParseMouse(ReadOnlySpan<char> rest, int clickCount)
    {
        if (rest.IsEmpty)
        {
            return null;
        }
        if (!uint.TryParse(rest, out uint button) || button == 0)
        {
            return null;
        }
        return new MouseClick(button, clickCount);
    }

    // Direct P/Invoke against libgtk-4.so.1 instead of GirCore's Gtk.Functions wrapper. Two reasons: (1) gtk_accelerator_parse isn't exposed by GirCore 0.7.0 — its binding generator skips functions with multiple out parameters returned alongside a bool return; (2) GirCore's wrappers route through a "Gtk" SONAME that the runtime can't resolve in the test process (Gtk.Application has never been initialized there to register a DllImportResolver). The rest of the project pinvokes the explicit `.so.1` SONAME for the same reason — bare `.so` names are dev-package symlinks that don't exist on runtime-only hosts. gboolean returns are kept as int so the source-generated marshaller doesn't need a MarshalAs hint.
    internal static partial class Native
    {
        private const string GtkLib = "libgtk-4.so.1";

        [LibraryImport(GtkLib, EntryPoint = "gtk_accelerator_parse", StringMarshalling = StringMarshalling.Utf8)]
        private static partial int gtk_accelerator_parse(string accelerator, out uint key, out uint mods);

        [LibraryImport(GtkLib, EntryPoint = "gtk_accelerator_name", StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr gtk_accelerator_name(uint key, uint mods);

        [LibraryImport(GtkLib, EntryPoint = "gtk_accelerator_get_default_mod_mask")]
        private static partial uint gtk_accelerator_get_default_mod_mask();

        // gdk_keyval_to_lower maps an upper-case letter keyval to its lower-case sibling and leaves non-letter keyvals (F11, space, Escape, …) unchanged. Lives in libgtk-4.so.1 in GTK 4 — gdk symbols ship from the same SO as gtk in GTK4 (no separate libgdk).
        [LibraryImport(GtkLib, EntryPoint = "gdk_keyval_to_lower")]
        private static partial uint gdk_keyval_to_lower(uint keyval);

        public static uint KeyvalToLower(uint keyval)
        {
            return gdk_keyval_to_lower(keyval);
        }

        public static bool TryParseAccelerator(string text, out uint key, out Gdk.ModifierType mods)
        {
            int ok = gtk_accelerator_parse(text, out key, out uint rawMods);
            mods = (Gdk.ModifierType)rawMods;
            return ok != 0;
        }

        // Returns a freshly-allocated string the caller must free with g_free. We Marshal.PtrToStringUTF8 (copy) and free the original. A null return is undocumented but defensive: surface it as the empty string so a caller-side .Format() never throws NullReferenceException.
        public static string AcceleratorName(uint key, Gdk.ModifierType mods)
        {
            var ptr = gtk_accelerator_name(key, (uint)mods);
            if (ptr == IntPtr.Zero)
            {
                return string.Empty;
            }
            try
            {
                return Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
            }
            finally
            {
                GLibFree(ptr);
            }
        }

        public static Gdk.ModifierType AcceleratorGetDefaultModMask()
        {
            return (Gdk.ModifierType)gtk_accelerator_get_default_mod_mask();
        }

        [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_free")]
        private static partial void GLibFree(IntPtr mem);
    }
}

// Action → list of triggers. Multiple triggers per action so power users can keep both "f" and "F11" bound to fullscreen, etc. Mutable so the preferences dialog can edit in place; copy via `Clone` before opening the dialog so Cancel discards changes.
public sealed class HotkeyMap
{
    // Cached so the per-key/click-event Lookup doesn't allocate. Wrapped as ReadOnlyCollection so a caller can't mutate the cached array (which would corrupt every subsequent Lookup) and so the published surface matches the comment's "constant" framing.
    public static readonly System.Collections.ObjectModel.ReadOnlyCollection<HotkeyAction> AllActions =
        new(Enum.GetValues<HotkeyAction>());

    private readonly Dictionary<HotkeyAction, List<Trigger>> bindings = new();

    public IReadOnlyList<Trigger> Get(HotkeyAction action)
    {
        if (bindings.TryGetValue(action, out var list))
        {
            return list;
        }
        return Array.Empty<Trigger>();
    }

    public void Set(HotkeyAction action, IEnumerable<Trigger> triggers)
    {
        bindings[action] = triggers.ToList();
    }

    public void Add(HotkeyAction action, Trigger trigger)
    {
        if (!bindings.TryGetValue(action, out var list))
        {
            list = new List<Trigger>();
            bindings[action] = list;
        }
        if (!list.Contains(trigger))
        {
            list.Add(trigger);
        }
    }

    public void Remove(HotkeyAction action, Trigger trigger)
    {
        if (bindings.TryGetValue(action, out var list))
        {
            list.Remove(trigger);
        }
    }

    // Bind a trigger to an action, removing it from any other action it was previously assigned to. Used by the preferences dialog so adding e.g. Ctrl+Q to Open doesn't silently shadow the existing Ctrl+Q→Quit binding. Returns the previous owner if the trigger was bound elsewhere, so the caller can surface "moved from X" feedback if it wants.
    public HotkeyAction? Bind(HotkeyAction action, Trigger trigger)
    {
        HotkeyAction? previousOwner = null;
        foreach (var kv in bindings)
        {
            if (kv.Key == action)
            {
                continue;
            }
            if (kv.Value.Remove(trigger))
            {
                previousOwner = kv.Key;
            }
        }
        Add(action, trigger);
        return previousOwner;
    }

    // Find which action a trigger fires. First-match wins, in the iteration order of the HotkeyAction enum, so if the user accidentally binds the same key to two actions the lower-valued action takes precedence (a deterministic, debuggable rule). Returns null when no action matches — the caller is then free to let the event propagate.
    public HotkeyAction? Lookup(Trigger trigger)
    {
        foreach (var action in AllActions)
        {
            if (bindings.TryGetValue(action, out var list))
            {
                foreach (var t in list)
                {
                    if (t.Equals(trigger))
                    {
                        return action;
                    }
                }
            }
        }
        return null;
    }

    public HotkeyMap Clone()
    {
        var copy = new HotkeyMap();
        foreach (var kv in bindings)
        {
            copy.bindings[kv.Key] = new List<Trigger>(kv.Value);
        }
        return copy;
    }

    // Reasonable defaults: matches the pre-customization behavior from MainWindow.cs (f/F + F11 + double-click for fullscreen, Space for play/pause, Escape only-when-fullscreen for exit, Ctrl+O / Ctrl+Q for open / quit). The diagnostic overlay had no key by default; leaving it empty here keeps that. Trigger.MakeKey is the canonical-form factory — see the Trigger doc for why all key triggers must go through it.
    public static HotkeyMap Default()
    {
        var m = new HotkeyMap();
        m.Set(HotkeyAction.Open, new Trigger[]
        {
            Trigger.MakeKey((uint)Gdk.Constants.KEY_o, Gdk.ModifierType.ControlMask),
        });
        m.Set(HotkeyAction.Quit, new Trigger[]
        {
            Trigger.MakeKey((uint)Gdk.Constants.KEY_q, Gdk.ModifierType.ControlMask),
        });
        m.Set(HotkeyAction.PlayPause, new Trigger[]
        {
            Trigger.MakeKey((uint)Gdk.Constants.KEY_space, 0),
            Trigger.MakeKey((uint)Gdk.Constants.KEY_k, 0),
        });
        m.Set(HotkeyAction.ToggleFullscreen, new Trigger[]
        {
            Trigger.MakeKey((uint)Gdk.Constants.KEY_f, 0),
            Trigger.MakeKey((uint)Gdk.Constants.KEY_F, Gdk.ModifierType.ShiftMask),
            Trigger.MakeKey((uint)Gdk.Constants.KEY_F11, 0),
            new Trigger.MouseClick((uint)Gdk.Constants.BUTTON_PRIMARY, ClickCount: 2),
        });
        m.Set(HotkeyAction.ExitFullscreen, new Trigger[]
        {
            Trigger.MakeKey((uint)Gdk.Constants.KEY_Escape, 0),
        });
        m.Set(HotkeyAction.ToggleDiagnosticOverlay, Array.Empty<Trigger>());
        m.Set(HotkeyAction.ShowPreferences, Array.Empty<Trigger>());
        m.Set(HotkeyAction.SeekBack5, new Trigger[]
        {
            Trigger.MakeKey((uint)Gdk.Constants.KEY_Left, 0),
        });
        m.Set(HotkeyAction.SeekForward5, new Trigger[]
        {
            Trigger.MakeKey((uint)Gdk.Constants.KEY_Right, 0),
        });
        m.Set(HotkeyAction.SeekBack10, new Trigger[]
        {
            Trigger.MakeKey((uint)Gdk.Constants.KEY_j, 0),
        });
        m.Set(HotkeyAction.SeekForward10, new Trigger[]
        {
            Trigger.MakeKey((uint)Gdk.Constants.KEY_l, 0),
        });
        m.Set(HotkeyAction.FrameStepBack, new Trigger[]
        {
            Trigger.MakeKey((uint)Gdk.Constants.KEY_comma, 0),
        });
        m.Set(HotkeyAction.FrameStepForward, new Trigger[]
        {
            Trigger.MakeKey((uint)Gdk.Constants.KEY_period, 0),
        });
        m.Set(HotkeyAction.SeekStart, new Trigger[]
        {
            Trigger.MakeKey((uint)Gdk.Constants.KEY_Home, 0),
        });
        m.Set(HotkeyAction.SeekEnd, new Trigger[]
        {
            Trigger.MakeKey((uint)Gdk.Constants.KEY_End, 0),
        });
        m.Set(HotkeyAction.ChapterPrev, new Trigger[]
        {
            Trigger.MakeKey((uint)Gdk.Constants.KEY_Left, Gdk.ModifierType.ControlMask),
        });
        m.Set(HotkeyAction.ChapterNext, new Trigger[]
        {
            Trigger.MakeKey((uint)Gdk.Constants.KEY_Right, Gdk.ModifierType.ControlMask),
        });
        m.Set(HotkeyAction.VolumeUp, new Trigger[]
        {
            Trigger.MakeKey((uint)Gdk.Constants.KEY_Up, 0),
        });
        m.Set(HotkeyAction.VolumeDown, new Trigger[]
        {
            Trigger.MakeKey((uint)Gdk.Constants.KEY_Down, 0),
        });
        m.Set(HotkeyAction.ToggleMute, new Trigger[]
        {
            Trigger.MakeKey((uint)Gdk.Constants.KEY_m, 0),
        });
        return m;
    }

    // Round-trip representation for TOML: action name (snake_case) → list of trigger strings. Unknown trigger strings are dropped during load (logged by UserConfig); unknown action names are ignored. Action keys not present in the file fall back to the default — so a partial config doesn't reset unrelated keys.
    public IReadOnlyDictionary<string, List<string>> ToTomlForm()
    {
        var dict = new Dictionary<string, List<string>>();
        foreach (var action in AllActions)
        {
            if (bindings.TryGetValue(action, out var list))
            {
                dict[ActionToTomlKey(action)] = list.Select(t => t.Format()).ToList();
            }
            else
            {
                dict[ActionToTomlKey(action)] = new List<string>();
            }
        }
        return dict;
    }

    public static HotkeyMap FromTomlForm(IReadOnlyDictionary<string, List<string>> raw, Action<string> warn)
    {
        var map = Default();
        foreach (var kv in raw)
        {
            if (!TryParseTomlKey(kv.Key, out var action))
            {
                warn($"hotkeys: unknown action '{kv.Key}', ignoring");
                continue;
            }
            var triggers = new List<Trigger>();
            foreach (var s in kv.Value)
            {
                var t = Trigger.TryParse(s);
                if (t == null)
                {
                    warn($"hotkeys: failed to parse trigger '{s}' for action '{kv.Key}', ignoring");
                    continue;
                }
                triggers.Add(t);
            }
            map.Set(action, triggers);
        }
        return map;
    }

    public static string ActionToTomlKey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.Open: return "open";
            case HotkeyAction.Quit: return "quit";
            case HotkeyAction.PlayPause: return "play_pause";
            case HotkeyAction.ToggleFullscreen: return "toggle_fullscreen";
            case HotkeyAction.ExitFullscreen: return "exit_fullscreen";
            case HotkeyAction.ToggleDiagnosticOverlay: return "toggle_diagnostic_overlay";
            case HotkeyAction.ShowPreferences: return "show_preferences";
            case HotkeyAction.SeekBack5: return "seek_back_5";
            case HotkeyAction.SeekForward5: return "seek_forward_5";
            case HotkeyAction.SeekBack10: return "seek_back_10";
            case HotkeyAction.SeekForward10: return "seek_forward_10";
            case HotkeyAction.FrameStepBack: return "frame_step_back";
            case HotkeyAction.FrameStepForward: return "frame_step_forward";
            case HotkeyAction.SeekStart: return "seek_start";
            case HotkeyAction.SeekEnd: return "seek_end";
            case HotkeyAction.ChapterPrev: return "chapter_prev";
            case HotkeyAction.ChapterNext: return "chapter_next";
            case HotkeyAction.VolumeUp: return "volume_up";
            case HotkeyAction.VolumeDown: return "volume_down";
            case HotkeyAction.ToggleMute: return "toggle_mute";
            default: throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    public static bool TryParseTomlKey(string key, out HotkeyAction action)
    {
        switch (key)
        {
            case "open": action = HotkeyAction.Open; return true;
            case "quit": action = HotkeyAction.Quit; return true;
            case "play_pause": action = HotkeyAction.PlayPause; return true;
            case "toggle_fullscreen": action = HotkeyAction.ToggleFullscreen; return true;
            case "exit_fullscreen": action = HotkeyAction.ExitFullscreen; return true;
            case "toggle_diagnostic_overlay": action = HotkeyAction.ToggleDiagnosticOverlay; return true;
            case "show_preferences": action = HotkeyAction.ShowPreferences; return true;
            case "seek_back_5": action = HotkeyAction.SeekBack5; return true;
            case "seek_forward_5": action = HotkeyAction.SeekForward5; return true;
            case "seek_back_10": action = HotkeyAction.SeekBack10; return true;
            case "seek_forward_10": action = HotkeyAction.SeekForward10; return true;
            case "frame_step_back": action = HotkeyAction.FrameStepBack; return true;
            case "frame_step_forward": action = HotkeyAction.FrameStepForward; return true;
            case "seek_start": action = HotkeyAction.SeekStart; return true;
            case "seek_end": action = HotkeyAction.SeekEnd; return true;
            case "chapter_prev": action = HotkeyAction.ChapterPrev; return true;
            case "chapter_next": action = HotkeyAction.ChapterNext; return true;
            case "volume_up": action = HotkeyAction.VolumeUp; return true;
            case "volume_down": action = HotkeyAction.VolumeDown; return true;
            case "toggle_mute": action = HotkeyAction.ToggleMute; return true;
            default: action = default; return false;
        }
    }

    // User-facing label for preferences dialog rows. Diverges from the TOML key only because the dialog wants Title Case, not snake_case.
    public static string ActionDisplayName(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.Open: return "Open File";
            case HotkeyAction.Quit: return "Quit";
            case HotkeyAction.PlayPause: return "Play / Pause";
            case HotkeyAction.ToggleFullscreen: return "Toggle Fullscreen";
            case HotkeyAction.ExitFullscreen: return "Exit Fullscreen";
            case HotkeyAction.ToggleDiagnosticOverlay: return "Diagnostic Overlay";
            case HotkeyAction.ShowPreferences: return "Preferences…";
            case HotkeyAction.SeekBack5: return "Seek Back 5s";
            case HotkeyAction.SeekForward5: return "Seek Forward 5s";
            case HotkeyAction.SeekBack10: return "Seek Back 10s";
            case HotkeyAction.SeekForward10: return "Seek Forward 10s";
            case HotkeyAction.FrameStepBack: return "Step Back One Frame";
            case HotkeyAction.FrameStepForward: return "Step Forward One Frame";
            case HotkeyAction.SeekStart: return "Jump to Start";
            case HotkeyAction.SeekEnd: return "Jump to End";
            case HotkeyAction.ChapterPrev: return "Previous Chapter";
            case HotkeyAction.ChapterNext: return "Next Chapter";
            case HotkeyAction.VolumeUp: return "Volume Up";
            case HotkeyAction.VolumeDown: return "Volume Down";
            case HotkeyAction.ToggleMute: return "Mute / Unmute";
            default: return action.ToString();
        }
    }
}
