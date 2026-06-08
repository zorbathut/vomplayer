using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Vomplayer.Playback;
using Vomplayer.Util;

namespace Vomplayer.Controls;

// Owns the seek Gtk.Scale and its press/release/settle state machine. Emits SeekRequested for user-driven value changes; the host calls SetVmSeekValue whenever the VM-side mirror moves, and the controller applies holding/settling gating before pushing the new value back into the widget. State-machine semantics live on the field comments below.
public sealed partial class SeekScaleController : IDisposable
{
    // Raw-signal P/Invoke. GirCore 0.7.0 cannot marshal Gtk.EventControllerLegacy's `event` signal payload: GdkEvent is its own fundamental GType (id 196), and GirCore's Value.Extract only handles GObject/Boxed/Enum/Flags/Param/Variant — it throws NotSupportedException for anything else. We connect to the signal via raw g_signal_connect_data and read the event type directly, skipping GirCore's extraction entirely.
    // Explicit SONAMEs: `libgtk-4.so.1` / `libgobject-2.0.so.0` are the runtime-installed libraries. The bare `libgtk-4.so` / `libgobject-2.0.so` names only exist as dev-package symlinks and would fail NativeLibrary resolution on runtime-only hosts.
    private const string GObjectLib = "libgobject-2.0.so.0";
    private const string GtkLib = "libgtk-4.so.1";

    [LibraryImport(GObjectLib, EntryPoint = "g_signal_connect_data", StringMarshalling = StringMarshalling.Utf8)]
    private static partial ulong SignalConnectData(IntPtr instance, string detailedSignal, IntPtr cHandler, IntPtr data, IntPtr destroyData, int connectFlags);

    [LibraryImport(GObjectLib, EntryPoint = "g_signal_handler_disconnect")]
    private static partial void SignalHandlerDisconnect(IntPtr instance, ulong handlerId);

    [LibraryImport(GtkLib, EntryPoint = "gdk_event_get_event_type")]
    private static partial int GdkEventGetEventType(IntPtr evt);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SeekLegacyEventCallback(IntPtr sender, IntPtr evt, IntPtr userData);

    private readonly IPlayback playback;
    private readonly Gtk.Scale scale;

    // Seek-scale state machine. idle = not pressing and !awaitingSeekSettle; holding = the user is pressing the scale (queried live via IsUserPressingScale, not stored — see that method); settling = awaitingSeekSettle (post-release, waiting for mpv's in-flight seek to land). `seekValueAtPress` is captured at press as the pre-seek position; the settle gate waits for the reported position to differ from it (i.e. to actually become the seek destination). Gating on "the position moved off the pre-seek value" rather than on `seeking=false` avoids a race where mpv fires `seeking=false` before its `time-pos` update, which would otherwise let a stale SeekValue push flicker the scale. Capturing at press (not release) matters: mpv's echo of a click-to-seek can arrive during the brief click-hold, so a release-time capture would record the *destination* and the gate could never fire while paused.
    private bool awaitingSeekSettle;
    private double seekValueAtPress;
    // Dedupe OnValueChanged against the last value we sent to SeekTo, reset to NaN on each press. NaN comparison is always false so the first post-press value-change always seeks; subsequent emissions at the same value (from any source) are skipped. Cheap defense against spurious re-emissions.
    private double lastUserSeek = double.NaN;
    private bool updatingFromVm;
    // Latest VM-side SeekValue, cached so the press-time baseline capture and the seek-settle handler can read it without a Func<double> dependency on the host. This is an equality-gated mirror of mpv's position (SeekValue is an [ObservableProperty] that only fires on a distinct value), so the press baseline is "the last distinct echoed normalized position", not a live mpv read — close enough in practice. Assumes the host's `viewModel.PropertyChanged → SetVmSeekValue` forward is synchronous on the main thread (true today via CommunityToolkit.Mvvm + GTK main-loop dispatch); a future refactor that batched or debounced VM forwards would let this cache lag mpv's actual position and would need to switch back to a live Func<double> read.
    private double currentVmSeekValue;
    // Delegate is retained as an instance field so it stays rooted while the signal connection lives. Handler id + controller pointer let us disconnect synchronously in Dispose, before the delegate field is nulled and before GTK tears the widget down — closing the narrow window where a late event could dispatch into a collectable delegate.
    private SeekLegacyEventCallback? seekLegacyCallback;
    private ulong seekLegacyHandlerId;
    private IntPtr seekLegacyControllerHandle;

    public Gtk.Scale Scale
    {
        get
        {
            return scale;
        }
    }

    // Fires for user-driven value changes (drag, scroll, arrow keys). The host wires this to viewModel.SeekTo. Suppressed during the VM→widget push so the round-trip doesn't loop.
    public event Action<double>? SeekRequested;

    public SeekScaleController(IPlayback playback)
    {
        if (playback == null)
        {
            throw new ArgumentNullException(nameof(playback));
        }
        this.playback = playback;

        scale = Gtk.Scale.NewWithRange(Gtk.Orientation.Horizontal, 0.0, 1.0, 0.001);
        scale.SetHexpand(true);
        scale.SetDrawValue(false);
        scale.SetSensitive(false);
        ScaleHelpers.RemoveLongPressGesture(scale);

        scale.OnValueChanged += OnSeekScaleValueChanged;

        // Gtk.Scale's internal gesture CLAIMS the pointer sequence on press, which denies any sibling gesture — their `released` signal never fires, `end`/`cancel` fire at claim-time instead. EventControllerLegacy is NOT a gesture (per gtk_widget_run_controllers), so it's unaffected by the claim protocol and sees raw ButtonPress/ButtonRelease at capture phase. We bypass GirCore's broken GdkEvent marshalling by using raw g_signal_connect_data; the callback returns 0 (gboolean FALSE) so the scale's gestures still receive the event — returning TRUE would short-circuit them and the slider would stop responding to mouse entirely.
        var legacy = Gtk.EventControllerLegacy.New();
        legacy.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        scale.AddController(legacy);

        seekLegacyCallback = OnSeekScaleRawEvent;
        seekLegacyControllerHandle = legacy.Handle.DangerousGetHandle();
        seekLegacyHandlerId = SignalConnectData(
            seekLegacyControllerHandle,
            "event",
            Marshal.GetFunctionPointerForDelegate(seekLegacyCallback),
            IntPtr.Zero, IntPtr.Zero, 0);

        playback.PropertyChanged += OnPlaybackPropertyChanged;
    }

    // "Is the user actively pressing the scrubber?" — sourced from GTK's own gesture state rather than a hand-maintained bool. The scale node carries GTK_STATE_FLAG_ACTIVE for the whole press across every region (thumb, trough, trough-click, the chapter-marker claim path, the padding strip above the bar) and GTK clears it on release / grab-broken. Two consequences: a lost release can't latch a stale "holding" (GTK's gesture clears Active even when our legacy controller misses the raw release), and it's scoped to this scale so a button-held drag elsewhere — e.g. the volume slider — doesn't suppress scrubber tracking. (The pointer Button1Mask and the slider sub-node's :active were both unreliable on this Wayland setup; the scale's :active was set in every observed press path.)
    private bool IsUserPressingScale()
    {
        return (scale.GetStateFlags() & Gtk.StateFlags.Active) != 0;
    }

    // Push a VM-side SeekValue update through to the widget, applying the holding/settling gates. Cache the value first so the press-time and seek-settle paths can read it without a separate accessor on the host.
    public void SetVmSeekValue(double value)
    {
        currentVmSeekValue = value;
        if (IsUserPressingScale())
        {
            return;
        }
        // While settling, only resume tracking once mpv's time-pos has actually moved off the pre-seek (press-time) value — the reliable "the seek landed" signal. Checking `!IsSeeking` alone would race with the `seeking` property arriving before the matching time-pos.
        if (awaitingSeekSettle)
        {
            if (value == seekValueAtPress)
            {
                return;
            }
            awaitingSeekSettle = false;
        }
        PushToScale();
    }

    public void SetSensitive(bool sensitive)
    {
        scale.SetSensitive(sensitive);
    }

    // Seek on every user-driven change; `updatingFromVm` breaks the VM→scale→VM loop; `lastUserSeek` dedupes repeated emissions at the same value (see field comment). Scroll-wheel and keyboard Arrow keys take this path too — no press, so `scale=Active` stays false and the standard VM-push flow resumes after each seek.
    private void OnSeekScaleValueChanged(Gtk.Range sender, EventArgs e)
    {
        if (updatingFromVm)
        {
            return;
        }
        double v = scale.GetValue();
        if (v == lastUserSeek)
        {
            return;
        }
        lastUserSeek = v;
        SeekRequested?.Invoke(v);
    }

    // Callback marshalled into GTK via raw g_signal_connect_data. Runs on the main thread (GTK signal delivery). Reads the event type via P/Invoke (not GirCore) and drives the state machine. Always returns 0 (gboolean FALSE) so the scale's internal gestures still process the event — returning nonzero would short-circuit them.
    private int OnSeekScaleRawEvent(IntPtr sender, IntPtr evt, IntPtr userData)
    {
        Gdk.EventType type = (Gdk.EventType)GdkEventGetEventType(evt);
        switch (type)
        {
            case Gdk.EventType.ButtonPress:
            case Gdk.EventType.TouchBegin:
                // Capture the pre-seek position as the settle baseline now, before any seek echo lands. See seekValueAtPress comment for why press-time (not release-time).
                awaitingSeekSettle = false;
                lastUserSeek = double.NaN;
                seekValueAtPress = currentVmSeekValue;
                break;
            case Gdk.EventType.ButtonRelease:
            case Gdk.EventType.TouchEnd:
            case Gdk.EventType.TouchCancel:
            case Gdk.EventType.GrabBroken:
                if (playback.IsSeeking)
                {
                    awaitingSeekSettle = true;
                }
                else
                {
                    PushToScale();
                }
                break;
        }
        return 0;
    }

    // mpv fires `seeking=false` and the new `time-pos` from the same playback-loop step, but the events may arrive in either order via the property-change queue. If `seeking` arrives first and we push VM.SeekValue right then, we'd push the pre-seek stale position and flicker old→new on the next tick. Instead, keep this handler as a pure safety net: clear `awaitingSeekSettle` and push only once the VM has moved off the press-time baseline — which covers the paused case, where no further time-pos tick will arrive to drive SetVmSeekValue.
    private void OnPlaybackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IPlayback.IsSeeking)
            && awaitingSeekSettle && !playback.IsSeeking && !IsUserPressingScale()
            && currentVmSeekValue != seekValueAtPress)
        {
            awaitingSeekSettle = false;
            PushToScale();
        }
    }

    private void PushToScale()
    {
        updatingFromVm = true;
        scale.SetValue(currentVmSeekValue);
        updatingFromVm = false;
    }

    public void Dispose()
    {
        // Disconnect the raw signal BEFORE releasing our delegate reference. GTK flushes pending events during widget destruction, which can happen after this method returns; if we dropped the delegate root first, a late dispatch would land in freed memory. Disconnect is synchronous — once it returns, the function pointer is unwired.
        if (seekLegacyHandlerId != 0 && seekLegacyControllerHandle != IntPtr.Zero)
        {
            SignalHandlerDisconnect(seekLegacyControllerHandle, seekLegacyHandlerId);
            seekLegacyHandlerId = 0;
            seekLegacyControllerHandle = IntPtr.Zero;
        }
        seekLegacyCallback = null;
        playback.PropertyChanged -= OnPlaybackPropertyChanged;
    }
}
