using System;

namespace Vomplayer.Util;

// Wrapper for GLib.Functions.IdleAdd that catches managed exceptions before they unwind into native GLib code. Bare IdleAdd registers a C# lambda as a GSourceFunc — if the lambda throws, the unwind crosses the managed→native boundary, which is undefined behavior at the C ABI (typically: process abort, sometimes: corrupted main loop). Routing every IdleAdd through here surfaces the failure to stderr (per the never-swallow policy) without risking the loop. Used by every IdleAdd call site in the codebase; do not add new bare IdleAdd usages.
//
// Why not just install a TaskScheduler.UnobservedTaskException / GLib.UnhandledException handler instead? Those don't see this surface. Idle callbacks aren't Tasks (no observation channel) and aren't routed through MainLoopSynchronizationContext (they're scheduled directly via the GLib API), so the SyncContext's catch-and-Raise path doesn't cover them. The catch has to live at the trampoline itself.
public static class IdleSafe
{
    public static void Add(int priority, System.Action action)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }
        GLib.Functions.IdleAdd(priority, () =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[vomplayer] idle callback threw: {ex}");
            }
            return false;
        });
    }
}
