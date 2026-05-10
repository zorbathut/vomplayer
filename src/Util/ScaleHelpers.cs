namespace Vomplayer.Util;

public static class ScaleHelpers
{
    // Gtk.Range adds a GtkGestureLongPress in gtk_range_init that activates zoom/fine-tune mode after ~1s of holding the slider. Zoom makes the trough thicker and rescales cursor motion 1:1 — useful for a precision slider, useless for a seek bar or a volume bar. Worse, the activation path recomputes the slider origin and any post-activation motion (even sub-pixel hand jitter) emits a value-changed at ~the held position, which the seek path would re-SeekTo, causing a visible video jump back to the held position mid-hold. Removing the controller kills the long-press trigger entirely. Shift+click fine-tune still works (different code path in gtk_range_click_gesture_pressed) — deliberate modifier, leave alone.
    public static void RemoveLongPressGesture(Gtk.Scale scale)
    {
        var controllers = scale.ObserveControllers();
        uint n = controllers.GetNItems();
        for (uint i = 0; i < n; i++)
        {
            var item = controllers.GetObject(i);
            if (item is Gtk.GestureLongPress lp)
            {
                scale.RemoveController(lp);
                return;
            }
        }
    }
}
