using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Vomplayer.Tests;

// Exercises LibC.WriteAllTo against real pipes: the partial-write loop (payload larger than pipe capacity) and the EAGAIN retry on an O_NONBLOCK fd. These are the failure modes of the crash-path stderr writer, so they get real-syscall coverage rather than trust.
[TestFixture]
public partial class LibCTests
{
    private const int F_SETFL = 4;
    private const int O_NONBLOCK = 0x800;

    [LibraryImport("libc.so.6", EntryPoint = "pipe", SetLastError = true)]
    private static partial int Pipe(Span<int> fds);

    [LibraryImport("libc.so.6", EntryPoint = "fcntl")]
    private static partial int Fcntl(int fd, int cmd, int arg);

    [LibraryImport("libc.so.6", EntryPoint = "read", SetLastError = true)]
    private static partial nint Read(int fd, byte[] buf, nuint count);

    [LibraryImport("libc.so.6", EntryPoint = "close")]
    private static partial int Close(int fd);

    private static byte[] MakePayload(int size)
    {
        var payload = new byte[size];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }
        return payload;
    }

    private static void RunPipeRoundTrip(bool nonBlockingWriter, int payloadSize)
    {
        Span<int> fds = stackalloc int[2];
        Assert.That(Pipe(fds), Is.Zero, "pipe() failed");
        int readFd = fds[0];
        int writeFd = fds[1];
        if (nonBlockingWriter)
        {
            Assert.That(Fcntl(writeFd, F_SETFL, O_NONBLOCK), Is.Not.EqualTo(-1), "fcntl(F_SETFL, O_NONBLOCK) failed");
        }

        byte[] payload = MakePayload(payloadSize);
        var received = new byte[payload.Length];
        int receivedCount = 0;
        var reader = new Thread(() =>
        {
            var buf = new byte[4096];
            while (receivedCount < received.Length)
            {
                nint n = Read(readFd, buf, (nuint)buf.Length);
                if (n <= 0)
                {
                    return;
                }
                Array.Copy(buf, 0, received, receivedCount, (int)n);
                receivedCount += (int)n;
                // Slow drain so the writer actually hits a full pipe (partial write on a blocking fd, EAGAIN on a non-blocking one).
                Thread.Sleep(1);
            }
        });
        reader.Start();
        try
        {
            LibC.WriteAllTo(writeFd, payload);
        }
        finally
        {
            Close(writeFd);
            reader.Join(TimeSpan.FromSeconds(30));
            Close(readFd);
        }

        Assert.That(receivedCount, Is.EqualTo(payload.Length), "payload was truncated");
        Assert.That(received, Is.EqualTo(payload));
    }

    [Test]
    public void WriteAllToDeliversPayloadLargerThanPipeCapacity()
    {
        RunPipeRoundTrip(nonBlockingWriter: false, payloadSize: 256 * 1024);
    }

    [Test]
    public void WriteAllToRetriesEagainOnNonBlockingFd()
    {
        RunPipeRoundTrip(nonBlockingWriter: true, payloadSize: 256 * 1024);
    }

    [Test]
    public void WriteAllToHandlesEmptyPayload()
    {
        Span<int> fds = stackalloc int[2];
        Assert.That(Pipe(fds), Is.Zero, "pipe() failed");
        LibC.WriteAllTo(fds[1], Array.Empty<byte>());
        Close(fds[1]);
        Close(fds[0]);
    }
}
