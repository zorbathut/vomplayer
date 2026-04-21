using System;
using System.Runtime.InteropServices;
using Vomplayer.Mpv;

namespace Vomplayer.Tests;

[TestFixture]
public class MpvRenderStructLayoutTests
{
    [Test]
    public void MpvOpenGlFboLayout()
    {
        Assert.That(Marshal.SizeOf<MpvOpenGlFbo>(), Is.EqualTo(16));
        Assert.That((int)Marshal.OffsetOf<MpvOpenGlFbo>(nameof(MpvOpenGlFbo.Fbo)), Is.EqualTo(0));
        Assert.That((int)Marshal.OffsetOf<MpvOpenGlFbo>(nameof(MpvOpenGlFbo.Width)), Is.EqualTo(4));
        Assert.That((int)Marshal.OffsetOf<MpvOpenGlFbo>(nameof(MpvOpenGlFbo.Height)), Is.EqualTo(8));
        Assert.That((int)Marshal.OffsetOf<MpvOpenGlFbo>(nameof(MpvOpenGlFbo.InternalFormat)), Is.EqualTo(12));
    }

    [Test]
    public void MpvRenderParamLayout()
    {
        // On 64-bit: int (4) + padding (4) + IntPtr (8) = 16 bytes, Data at offset 8.
        // On 32-bit: int (4) + IntPtr (4) = 8 bytes, Data at offset 4.
        var size = Marshal.SizeOf<MpvRenderParam>();
        var dataOffset = (int)Marshal.OffsetOf<MpvRenderParam>(nameof(MpvRenderParam.Data));
        if (IntPtr.Size == 8)
        {
            Assert.That(size, Is.EqualTo(16));
            Assert.That(dataOffset, Is.EqualTo(8));
        }
        else
        {
            Assert.That(size, Is.EqualTo(8));
            Assert.That(dataOffset, Is.EqualTo(4));
        }
        Assert.That((int)Marshal.OffsetOf<MpvRenderParam>(nameof(MpvRenderParam.Type)), Is.EqualTo(0));
    }

    [Test]
    public void MpvOpenGlInitParamsLayout()
    {
        Assert.That(Marshal.SizeOf<MpvOpenGlInitParams>(), Is.EqualTo(2 * IntPtr.Size));
        Assert.That((int)Marshal.OffsetOf<MpvOpenGlInitParams>(nameof(MpvOpenGlInitParams.GetProcAddress)), Is.EqualTo(0));
        Assert.That((int)Marshal.OffsetOf<MpvOpenGlInitParams>(nameof(MpvOpenGlInitParams.GetProcAddressCtx)), Is.EqualTo(IntPtr.Size));
    }
}
