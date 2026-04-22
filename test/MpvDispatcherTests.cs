using System;
using System.Collections.Generic;
using System.Threading;
using Vomplayer.Mpv;

namespace Vomplayer.Tests;

// Integration tests for MpvDispatcher against a real libmpv. Each test stands up its own dispatcher so the worker thread is fresh, and disposes it at end so mpv terminates cleanly.
[TestFixture]
public class MpvDispatcherTests
{
    private static MpvDispatcher NewHeadless()
    {
        var d = new MpvDispatcher();
        d.Post(h =>
        {
            h.SetOption("vo", "null");
            h.SetOption("ao", "null");
            h.SetOption("terminal", "no");
            h.Initialize();
        });
        return d;
    }

    private static void RunAndWait(MpvDispatcher d, Action<MpvHandle> action, TimeSpan timeout)
    {
        var done = new ManualResetEventSlim(false);
        d.Post(h =>
        {
            action(h);
            done.Set();
        });
        if (!done.Wait(timeout))
        {
            throw new TimeoutException("dispatcher action did not complete in time");
        }
    }

    [Test]
    public void ActionsRunOnWorkerThreadNotCaller()
    {
        using var d = NewHeadless();
        int callerThread = Thread.CurrentThread.ManagedThreadId;
        int workerThread = 0;
        RunAndWait(d, _ => workerThread = Thread.CurrentThread.ManagedThreadId, TimeSpan.FromSeconds(2));
        Assert.That(workerThread, Is.Not.Zero);
        Assert.That(workerThread, Is.Not.EqualTo(callerThread));
    }

    [Test]
    public void ActionsRunInSubmissionOrder()
    {
        using var d = NewHeadless();
        var log = new List<int>();
        for (int i = 0; i < 20; i++)
        {
            int captured = i;
            d.Post(_ =>
            {
                lock (log)
                {
                    log.Add(captured);
                }
            });
        }
        RunAndWait(d, _ => { }, TimeSpan.FromSeconds(2));
        // Deterministic ordering: single worker consumes the queue FIFO.
        Assert.That(log, Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19 }));
    }

    [Test]
    public void HandleCanInvokeMpvApi()
    {
        using var d = NewHeadless();
        ulong observeId = 0;
        RunAndWait(d, h => observeId = h.ObserveProperty("pause", MpvFormat.Flag), TimeSpan.FromSeconds(2));
        Assert.That(observeId, Is.GreaterThan((ulong)0));
    }

    [Test]
    public void ExceptionInActionDoesNotKillWorker()
    {
        using var d = NewHeadless();
        // Swallow the stderr log so it doesn't clutter test output.
        var origErr = Console.Error;
        Console.SetError(TextWriter.Null);
        try
        {
            d.Post(_ => throw new InvalidOperationException("deliberate"));
            // The worker should still run subsequent actions after swallowing the throw.
            bool after = false;
            RunAndWait(d, _ => after = true, TimeSpan.FromSeconds(2));
            Assert.That(after, Is.True);
        }
        finally
        {
            Console.SetError(origErr);
        }
    }

    [Test]
    public void PostAfterDisposeThrows()
    {
        var d = NewHeadless();
        d.Dispose();
        Assert.Throws<ObjectDisposedException>(() => d.Post(_ => { }));
    }

    [Test]
    public void EventsForwardFromUnderlyingClient()
    {
        using var d = NewHeadless();
        var propertyEvents = new List<PropertyChange>();
        d.PropertyChanged += c =>
        {
            lock (propertyEvents)
            {
                propertyEvents.Add(c);
            }
        };

        // Observe a property to trigger mpv's synthesized initial change event. ObserveProperty must run on the dispatcher thread.
        RunAndWait(d, h => h.ObserveProperty("pause", MpvFormat.Flag), TimeSpan.FromSeconds(2));

        // Drain pump: the wakeup callback auto-posts DrainEvents. Yield briefly so the dispatcher can run it.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            lock (propertyEvents)
            {
                if (propertyEvents.Count > 0)
                {
                    break;
                }
            }
            Thread.Sleep(20);
        }

        lock (propertyEvents)
        {
            // Don't assume ordering — other events may interleave. Just verify the observe fired its synthesized initial change through the forwarding chain.
            Assert.That(propertyEvents, Has.Some.Matches<PropertyChange>(c => c.Name == "pause"));
        }
    }
}
