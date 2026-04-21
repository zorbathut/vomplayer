using System;
using Vomplayer.Playback;

namespace Vomplayer.Tests;

[TestFixture]
public class PlaybackTests
{
    private static Playback.Playback NewHeadlessInitialized()
    {
        // Synchronous post — ProcessEvents runs on the test thread when mpv signals.
        var pb = new Playback.Playback(a => a());
        pb.Initialize();
        return pb;
    }

    [Test]
    public void NullPostThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new Playback.Playback(null!));
    }

    [Test]
    public void DisposeWithoutInitializeIsSafe()
    {
        var pb = new Playback.Playback(a => a());
        Assert.DoesNotThrow(() => pb.Dispose());
    }

    [Test]
    public void IsPausedDefaultsTrue()
    {
        using var pb = NewHeadlessInitialized();
        Assert.That(pb.IsPaused, Is.True);
    }

    [Test]
    public void LoadFileNullPathThrows()
    {
        using var pb = NewHeadlessInitialized();
        Assert.Throws<ArgumentNullException>(() => pb.LoadFile(null!));
    }

}
