using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Vomplayer.Mpv;

// Owns the single MpvClient and serializes every mpv client-API call onto one worker thread.
//
// Background: calling mpv_set_property_string (or any synchronously-blocking client call) from the main thread deadlocks against mpv's render pipeline. mpv core waits for mpv_render_context_render to acknowledge the change; our render callback runs on the main thread via GLib.IdleAdd; main thread is blocked in the set — circular wait. Fix: a dedicated thread owns all mpv calls.
//
// Enforcement: callers never touch MpvClient directly. They submit an action to Post(), and the dispatcher hands them an MpvHandle — a readonly ref struct whose stack-only nature makes it impossible to capture, stash in a field, await across, or otherwise leak out of the action scope. Code that needs mpv access at any other thread boundary fails to compile.
//
// One exception: MpvRenderContext construction must run on the GL-owning thread (main), not the dispatcher worker. CreateRenderContext() provides a controlled escape hatch that builds the render context synchronously on the caller's thread — render-context calls don't deadlock the way client-API calls do, so they're allowed to touch the internal client directly.
//
// Events: libmpv's wakeup callback fires on its own event thread. We re-queue a DrainEvents action onto the worker so consumers observe FileLoaded/FileEnded/PropertyChanged/Shutdown *from* the dispatcher thread. Consumers that need to marshal to a UI thread do so themselves.
internal sealed class MpvDispatcher : IDisposable
{
    // Cached static delegate so the wakeup path doesn't allocate per fire (one allocation per presented frame at mpv's event cadence otherwise).
    private static readonly MpvAction drainAction = h => h.DrainEvents();

    private readonly MpvClient client;
    private readonly BlockingCollection<MpvAction> queue = new();
    private readonly Thread worker;
    private int disposed;

    public event Action? FileLoaded;
    public event Action? FileEnded;
    public event Action<PropertyChange>? PropertyChanged;
    public event Action<LogMessage>? LogMessageReceived;
    public event Action? Shutdown;

    public MpvDispatcher()
    {
        client = new MpvClient();
        client.FileLoaded += OnClientFileLoaded;
        client.FileEnded += OnClientFileEnded;
        client.PropertyChanged += OnClientPropertyChanged;
        client.LogMessageReceived += OnClientLogMessage;
        client.Shutdown += OnClientShutdown;
        // Fires on libmpv's event thread — we only queue work, we don't touch the client from there.
        client.EventAvailable += OnClientEventAvailable;

        worker = new Thread(Run) { IsBackground = true, Name = "vompl-mpv" };
        worker.Start();
    }

    // Queue an action to run on the dispatcher worker. The MpvHandle passed into the action is the only way to reach mpv client-API methods; it cannot escape the lambda.
    public void Post(MpvAction action)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }
        if (Volatile.Read(ref disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(MpvDispatcher));
        }
        try
        {
            queue.Add(action);
        }
        catch (InvalidOperationException ex)
        {
            // Narrow race: Dispose ran between the disposed-check and Add, calling CompleteAdding. BlockingCollection.Add throws InvalidOperationException in that case; translate to the type callers expect.
            throw new ObjectDisposedException(nameof(MpvDispatcher), ex);
        }
    }

    // Runs on the caller's thread — expected to be the GL-owning main thread. Does NOT go through the worker queue because mpv_render_context_create requires a current GL context and the get_proc_address callback it invokes must resolve against that context. Render-context calls don't deadlock the way client-API calls do, so bypassing the dispatcher for construction is safe.
    //
    // wlDisplay / x11Display: native display handles for hwdec interop (vaapi, vdpau). Pass IntPtr.Zero when unavailable. See MpvRenderContext ctor doc for lifetime and fallback details.
    public MpvRenderContext CreateRenderContext(Func<string, IntPtr> getProcAddress, IntPtr wlDisplay, IntPtr x11Display)
    {
        if (getProcAddress == null)
        {
            throw new ArgumentNullException(nameof(getProcAddress));
        }
        return new MpvRenderContext(client, getProcAddress, wlDisplay, x11Display);
    }

    private void OnClientEventAvailable()
    {
        // Fires on libmpv's event thread. Don't touch the client here — queue a drain.
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }
        try
        {
            queue.Add(drainAction);
        }
        catch (InvalidOperationException ex)
        {
            // Narrow window: Dispose set disposed=1 then called CompleteAdding; our disposed-check above raced it. Drain is no longer relevant because Dispose unsubscribes and drops events — but log so a recurring wakeup here during normal operation doesn't hide silently.
            Console.Error.WriteLine($"[vomplayer] mpv wakeup arrived during dispatcher shutdown: {ex.Message}");
        }
    }

    private void OnClientFileLoaded()
    {
        FileLoaded?.Invoke();
    }

    private void OnClientFileEnded()
    {
        FileEnded?.Invoke();
    }

    private void OnClientPropertyChanged(PropertyChange change)
    {
        PropertyChanged?.Invoke(change);
    }

    private void OnClientLogMessage(LogMessage message)
    {
        LogMessageReceived?.Invoke(message);
    }

    private void OnClientShutdown()
    {
        Shutdown?.Invoke();
    }

    private void Run()
    {
        foreach (var action in queue.GetConsumingEnumerable())
        {
            try
            {
                action(new MpvHandle(client));
            }
            catch (Exception ex)
            {
                // Trampolines must not throw past the worker loop — one bad action would tear down the whole mpv thread and silently freeze all subsequent commands. Log and continue.
                Console.Error.WriteLine($"[vomplayer] mpv dispatcher action threw: {ex}");
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        client.EventAvailable -= OnClientEventAvailable;
        client.FileLoaded -= OnClientFileLoaded;
        client.FileEnded -= OnClientFileEnded;
        client.PropertyChanged -= OnClientPropertyChanged;
        client.LogMessageReceived -= OnClientLogMessage;
        client.Shutdown -= OnClientShutdown;
        FileLoaded = null;
        FileEnded = null;
        PropertyChanged = null;
        LogMessageReceived = null;
        Shutdown = null;
        queue.CompleteAdding();
        // Bounded join: if the worker is stuck inside a blocking mpv_set_property_string waiting for a render-context ACK, and the render context has already been destroyed (normal Dispose ordering — render surface disposes before Playback), mpv unblocks quickly. But if Dispose is called while the render context is still alive AND a blocking set is in flight, we'd wait forever — main thread is us, and mpv's render ACK needs main. The timeout makes that failure mode loud instead of a silent process hang. Callers should dispose the render surface before the dispatcher to avoid this path.
        if (!worker.Join(TimeSpan.FromSeconds(3)))
        {
            // Deliberately LEAK the client and queue rather than destroy them under the worker's feet: mpv_terminate_destroy while another thread is inside an mpv_* call on the same handle is use-after-free, and the wedged worker means exactly that call is still in flight (likewise queue.Dispose under an active GetConsumingEnumerable). Every current Dispose path runs at process exit, so the leak is reclaimed by the OS moments later.
            Console.Error.WriteLine("[vomplayer] MpvDispatcher worker did not exit within 3s — probably blocked on mpv waiting for a render ACK that will never come. Check Dispose ordering: the render surface must be disposed before the dispatcher. Leaking the mpv client to avoid use-after-free.");
            return;
        }
        client.Dispose();
        queue.Dispose();
    }
}

// Delegate shape for Post actions. Custom delegate rather than Action<T> because MpvHandle is a ref struct and can't be used as a generic type argument. Passed by value — the struct is pointer-sized so copy cost is trivial, and by-value avoids forcing `in` syntax on every lambda.
internal delegate void MpvAction(MpvHandle handle);

// Stack-only reference to the process's MpvClient. Handed out exclusively by MpvDispatcher inside Post callbacks; its ref-struct nature blocks the compiler from letting callers store, box, capture across await, or otherwise leak it out of the callback scope — which means client-API access is statically pinned to the dispatcher thread.
internal readonly ref struct MpvHandle
{
    private readonly MpvClient client;

    internal MpvHandle(MpvClient client)
    {
        this.client = client;
    }

    public void SetOption(string name, string value)
    {
        client.SetOption(name, value);
    }

    public void SetProperty(string name, string value)
    {
        client.SetProperty(name, value);
    }

    public void Initialize()
    {
        client.Initialize();
    }

    public void RequestLogMessages(string minLevel)
    {
        client.RequestLogMessages(minLevel);
    }

    public void Command(params string[] args)
    {
        client.Command(args);
    }

    public string? GetPropertyString(string name)
    {
        return client.GetPropertyString(name);
    }

    public double? GetPropertyDouble(string name)
    {
        return client.GetPropertyDouble(name);
    }

    public bool? GetPropertyFlag(string name)
    {
        return client.GetPropertyFlag(name);
    }

    public ulong ObserveProperty(string name, MpvFormat format)
    {
        return client.ObserveProperty(name, format);
    }

    public void DrainEvents()
    {
        client.DrainEvents();
    }
}
