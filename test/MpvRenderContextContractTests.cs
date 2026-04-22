using System;
using Vomplayer.Mpv;

namespace Vomplayer.Tests;

// Contract tests that don't require a live GL context. Anything touching the native
// render context itself needs real GL and is covered by the manual smoke test.
[TestFixture]
public class MpvRenderContextContractTests
{
    private static MpvClient NewHeadlessInitialized()
    {
        var mpv = new MpvClient();
        mpv.SetOption("vo", "null");
        mpv.SetOption("ao", "null");
        mpv.SetOption("terminal", "no");
        mpv.Initialize();
        return mpv;
    }

    [Test]
    public void NullClientThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new MpvRenderContext(null!, _ => IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
    }

    [Test]
    public void NullGetProcAddressThrows()
    {
        using var mpv = NewHeadlessInitialized();
        Assert.Throws<ArgumentNullException>(() => new MpvRenderContext(mpv, null!, IntPtr.Zero, IntPtr.Zero));
    }
}
