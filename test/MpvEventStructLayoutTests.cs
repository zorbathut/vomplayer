using System;
using System.Runtime.InteropServices;
using Vomplayer.Mpv;

namespace Vomplayer.Tests;

// Pins the layout of the event structs PtrToStructure'd from live mpv memory on every event dispatch — same contract-test pattern as MpvRenderStructLayoutTests. mpv_event: mpv_event_id (int enum) + int error + uint64 reply_userdata + void* data.
[TestFixture]
public class MpvEventStructLayoutTests
{
    [Test]
    public void EventLayout()
    {
        Assert.That((int)Marshal.OffsetOf<LibMpv.Event>(nameof(LibMpv.Event.EventId)), Is.EqualTo(0));
        Assert.That((int)Marshal.OffsetOf<LibMpv.Event>(nameof(LibMpv.Event.Error)), Is.EqualTo(4));
        Assert.That((int)Marshal.OffsetOf<LibMpv.Event>(nameof(LibMpv.Event.ReplyUserData)), Is.EqualTo(8));
        Assert.That((int)Marshal.OffsetOf<LibMpv.Event>(nameof(LibMpv.Event.Data)), Is.EqualTo(16));
        Assert.That(Marshal.SizeOf<LibMpv.Event>(), Is.EqualTo(16 + IntPtr.Size));
    }

    [Test]
    public void EventPropertyLayout()
    {
        // mpv_event_property: const char* name, mpv_format (int enum), void* data. On 64-bit the int is padded to the pointer boundary.
        Assert.That((int)Marshal.OffsetOf<LibMpv.EventProperty>(nameof(LibMpv.EventProperty.Name)), Is.EqualTo(0));
        Assert.That((int)Marshal.OffsetOf<LibMpv.EventProperty>(nameof(LibMpv.EventProperty.Format)), Is.EqualTo(IntPtr.Size));
        Assert.That((int)Marshal.OffsetOf<LibMpv.EventProperty>(nameof(LibMpv.EventProperty.Data)), Is.EqualTo(2 * IntPtr.Size));
        Assert.That(Marshal.SizeOf<LibMpv.EventProperty>(), Is.EqualTo(3 * IntPtr.Size));
    }

    [Test]
    public void EventLogMessageLayout()
    {
        // mpv_event_log_message: const char* prefix, level, text, then mpv_log_level (int).
        Assert.That((int)Marshal.OffsetOf<LibMpv.EventLogMessage>(nameof(LibMpv.EventLogMessage.Prefix)), Is.EqualTo(0));
        Assert.That((int)Marshal.OffsetOf<LibMpv.EventLogMessage>(nameof(LibMpv.EventLogMessage.Level)), Is.EqualTo(IntPtr.Size));
        Assert.That((int)Marshal.OffsetOf<LibMpv.EventLogMessage>(nameof(LibMpv.EventLogMessage.Text)), Is.EqualTo(2 * IntPtr.Size));
        Assert.That((int)Marshal.OffsetOf<LibMpv.EventLogMessage>(nameof(LibMpv.EventLogMessage.LogLevel)), Is.EqualTo(3 * IntPtr.Size));
        Assert.That(Marshal.SizeOf<LibMpv.EventLogMessage>(), Is.EqualTo(3 * IntPtr.Size + IntPtr.Size));
    }
}
