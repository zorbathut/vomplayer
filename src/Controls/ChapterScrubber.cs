using System;
using System.Collections.Generic;
using Vomplayer.Playback;
using Vomplayer.Util;

namespace Vomplayer.Controls;

// Adds chapter-marker behavior to a horizontal Gtk.Scale. Ticks are drawn entirely by Cairo onto a non-targetable Gtk.DrawingArea overlay layered above the scale.
//
// Why not Gtk.Scale.AddMark: AddMark reserves `> marks` subnodes above and below the trough, which grows the controls bar by the indicator height on each side. Even with CSS min-height 0 / 2 px, you pay 4 px of bar growth and the indicator is too short to read as a clear chapter tick. Cairo painting uses space the scale ALREADY owns (the vertical padding inside the scale's natural height — the slider thumb is taller than the trough, so there's a few px of slack on each side of the trough). Painting into that slack gives a noticeably taller tick at zero bar-height cost.
//
// Composition over inheritance: GirCore's GObject subclassing story is fragile (see DiagnosticOverlay.cs). We own a Gtk.Overlay (scale + transparent hover layer) and expose it via Widget.
//
// Click routing: a Capture-phase Gtk.GestureClick on the scale watches every press. If the press lands within the trough's Y range (Gtk.Range.GetRangeRect), we don't claim — the scale's own internal click gesture sees the press and runs normal click-to-seek. If the press lands above or below the trough (i.e. inside the tick-mark region) AND is within ClickToleranceTpx of a chapter's X, we emit ChapterClicked and SetState(Claimed) to deny the scale's own gesture; trough-Y clicks are never claimed, so "click on the bar itself even right over a marker" cannot snap to a chapter. Off-marker clicks in the tick region don't claim either, so they fall through to the scale's normal seek-to-X — keeps full-width clickability of the scale intact.
//
// Hover affordance: a motion controller on the scale tracks the hovered chapter (the chapter under the cursor's X within tolerance, when the cursor's Y is in the tick region). While a chapter is hovered, the scale's cursor switches to "pointer" and a tooltip shows the chapter's title (or "Chapter N" when the container carries no title). The tooltip is delivered via GTK's query-tooltip signal (SetHasTooltip + OnQueryTooltip) rather than the motion handler, so GTK owns the popup's show/hide/refresh as the pointer moves between markers and across the trough dead-zone — pushing text into SetTooltipText from motion leaves stale tooltips that don't refresh while the pointer stays inside the widget. The query handler reuses the same HitTestTickArea as the cursor. We deliberately don't change the tick's rendering on hover (earlier iterations brightened it but that read as "the tick vanishes" on light themes where the chosen highlight color blended into the background). The overlay is SetCanTarget(false) so clicks pass straight through to the scale.
//
// X math: chapter ticks are positioned at `effectiveLeft + V * effectiveWidth`, where the effective range is the trough's allocation rect (from Gtk.Widget.ComputeBounds) shrunk on each side by `trough.padding + trough.border + slider.padding + slider.border` (all from Gtk.StyleContext queries). Empirically (from a regression on 30+ logged sliderValue→slider.center pairs across the V range), GTK constrains the slider thumb's allocation such that its border-box fits inside the trough's content area — so the slider's center can never reach within `(trough.border+padding) + (slider.border+padding)` px of the trough's allocation edge. With Adwaita's `tBdr=(1,1) sBdr=(1,1)` that's 2 px on each side; with f3-formula residuals < 1px across V, vs the gtk_range_compute_slider_position-style documented formula which gave residuals up to ±7px, vs trough-padding-only which gave ±1.7px drifting linearly with V. The slider's CSS margin (typically negative on Adwaita: -9px each side) does NOT enter the position formula — it controls visual overhang of the rendered thumb beyond its allocation, not where the allocation sits. We deliberately don't use Gtk.Range.GetRangeRect / GetSliderRange: GetRangeRect coords are in a different coord space than the hoverLayer's Cairo coords (scale's CSS margin shifts them), and GetSliderRange has been observed to return zero/stale values during first paint. ComputeBounds gives the rendered rect directly in any target's coord space, so we ask for it in hoverLayer coords for paint and in scale coords for hit-test against motion-event args.X.
public sealed class ChapterScrubber
{
    // Click hit-test tolerance in pixels.
    private const double ClickToleranceTpx = 6.0;

    private readonly Gtk.Overlay overlay;
    private readonly Gtk.Scale scale;
    private readonly Gtk.DrawingArea hoverLayer;

    private IReadOnlyList<MediaChapter> chapters = Array.Empty<MediaChapter>();
    private double durationSeconds;
    // Cached trough/slider subwidget references. Looked up lazily on first RefreshGeometry — the scale is realized by then. CSS names "trough" / "slider" are stable across GTK4 themes since they're set by GtkRange itself, not by the theme.
    private Gtk.Widget? troughWidget;
    private Gtk.Widget? sliderWidget;
    // Trough rect in hoverLayer (= Cairo paint) coords.
    private double troughLeftInHoverPx;
    private double troughTopInHoverPx;
    private double troughWidthInHoverPx;
    private double troughHeightInHoverPx;
    // Trough rect in scale-local coords (= motion-event args.X coords).
    private double troughLeftInScalePx;
    private double troughTopInScalePx;
    private double troughWidthInScalePx;
    private double troughHeightInScalePx;
    // Per-side inset from the trough's allocation edge to where the slider thumb's center can reach. Equals trough's CSS (padding+border) + slider's CSS (padding+border) — empirically derived from logged slider positions; see the class-level comment.
    private double troughInsetLeftPx;
    private double troughInsetRightPx;
    // Time of the chapter currently under the cursor, null when no marker is hovered.
    private double? hoveredChapterTime;

    public event Action<double>? ChapterClicked;

    public Gtk.Widget Widget
    {
        get
        {
            return overlay;
        }
    }

    public ChapterScrubber(Gtk.Scale scale)
    {
        if (scale == null)
        {
            throw new ArgumentNullException(nameof(scale));
        }
        this.scale = scale;

        // Z-order: hoverLayer is the overlay's MAIN child (rendered first, behind), scale is the overlay child (rendered on top). This way the trough fill and slider thumb visually clip the chapter ticks where they intersect, instead of the ticks painting over the slider.
        hoverLayer = Gtk.DrawingArea.New();
        // CanTarget=false makes the layer invisible to pointer hit-testing. With the layer behind the scale this is mostly redundant (scale is on top, catches input first), but defensive — if scale's halign/valign ever leave gaps, clicks in those gaps shouldn't be caught by the layer either.
        hoverLayer.SetCanTarget(false);
        // Deliberately NO Hexpand/Vexpand on hoverLayer: as the overlay's main child it gets the full overlay content area regardless. Setting Vexpand=true here would propagate upward (Gtk.Overlay → controlsBox → rootBox), making the controls bar consume all available vertical space.
        hoverLayer.SetDrawFunc(DrawHover);

        overlay = Gtk.Overlay.New();
        overlay.SetHexpand(true);
        overlay.SetChild(hoverLayer);
        overlay.AddOverlay(scale);

        // Capture phase: runs before the scale's internal click-to-seek gesture so we can claim the sequence and deny it. The legacy event controller already attached to the scale in MainWindow runs at Capture too but never claims (returns FALSE), so the two coexist.
        var click = Gtk.GestureClick.New();
        click.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        click.OnPressed += OnScalePressed;
        scale.AddController(click);

        var motion = Gtk.EventControllerMotion.New();
        motion.OnMotion += OnScaleMotion;
        motion.OnLeave += OnScaleLeave;
        scale.AddController(motion);

        // Position-dependent tooltip: GTK queries us per-pointer-position once has-tooltip is on. We hit-test the query coords (scale-local, same space as motion args) and supply the hovered chapter's label, or return false so no tooltip shows off-marker.
        scale.SetHasTooltip(true);
        scale.OnQueryTooltip += OnScaleQueryTooltip;
    }

    private void RefreshGeometry()
    {
        if (troughWidget == null)
        {
            troughWidget = FindByCssName(scale, "trough");
        }
        if (sliderWidget == null)
        {
            sliderWidget = FindByCssName(scale, "slider");
        }
        if (troughWidget == null || sliderWidget == null)
        {
            return;
        }
        if (troughWidget.ComputeBounds(hoverLayer, out var inHover))
        {
            troughLeftInHoverPx = inHover.GetX();
            troughTopInHoverPx = inHover.GetY();
            troughWidthInHoverPx = inHover.GetWidth();
            troughHeightInHoverPx = inHover.GetHeight();
        }
        if (troughWidget.ComputeBounds(scale, out var inScale))
        {
            troughLeftInScalePx = inScale.GetX();
            troughTopInScalePx = inScale.GetY();
            troughWidthInScalePx = inScale.GetWidth();
            troughHeightInScalePx = inScale.GetHeight();
        }
        var troughStyle = troughWidget.GetStyleContext();
        troughStyle.GetPadding(out var troughPadding);
        troughStyle.GetBorder(out var troughBorder);
        var sliderStyle = sliderWidget.GetStyleContext();
        sliderStyle.GetPadding(out var sliderPadding);
        sliderStyle.GetBorder(out var sliderBorder);
        troughInsetLeftPx = troughPadding.Left + troughBorder.Left + sliderPadding.Left + sliderBorder.Left;
        troughInsetRightPx = troughPadding.Right + troughBorder.Right + sliderPadding.Right + sliderBorder.Right;
    }

    private static Gtk.Widget? FindByCssName(Gtk.Widget root, string cssName)
    {
        var child = root.GetFirstChild();
        while (child != null)
        {
            if (child.GetCssName() == cssName)
            {
                return child;
            }
            var found = FindByCssName(child, cssName);
            if (found != null)
            {
                return found;
            }
            child = child.GetNextSibling();
        }
        return null;
    }

    // The chapter under (x, y) when (x, y) is in the tick region (outside the trough's Y range) AND aligned with a chapter's X within tolerance; null otherwise. Trough-Y clicks deliberately return null even if X aligns with a marker — that's the "clicking the bar itself doesn't snap to chapter" requirement.
    private MediaChapter? HitTestTickArea(double x, double y)
    {
        if (durationSeconds <= 0)
        {
            return null;
        }
        double effectiveLeft = troughLeftInScalePx + troughInsetLeftPx;
        double effectiveWidth = troughWidthInScalePx - troughInsetLeftPx - troughInsetRightPx;
        if (effectiveWidth <= 0)
        {
            return null;
        }
        if (y >= troughTopInScalePx && y < troughTopInScalePx + troughHeightInScalePx)
        {
            return null;
        }
        return ChapterHitTest.NearestChapter(x, effectiveLeft, effectiveWidth, durationSeconds, chapters, ClickToleranceTpx);
    }

    private void OnScalePressed(Gtk.GestureClick gesture, Gtk.GestureClick.PressedSignalArgs args)
    {
        RefreshGeometry();
        var chapter = HitTestTickArea(args.X, args.Y);
        if (chapter == null)
        {
            return;
        }
        ChapterClicked?.Invoke(chapter.TimeSeconds / durationSeconds);
        // Claim denies the scale's own click gesture. Without this, the scale would also seek-to-X for the same press and the chapter seek would be visibly overridden by the trough-X seek.
        gesture.SetState(Gtk.EventSequenceState.Claimed);
    }

    private void OnScaleMotion(Gtk.EventControllerMotion sender, Gtk.EventControllerMotion.MotionSignalArgs args)
    {
        RefreshGeometry();
        double? time = HitTestTickArea(args.X, args.Y)?.TimeSeconds;
        if (time == hoveredChapterTime)
        {
            return;
        }
        hoveredChapterTime = time;
        scale.SetCursorFromName(time.HasValue ? "pointer" : null);
    }

    private bool OnScaleQueryTooltip(Gtk.Widget sender, Gtk.Widget.QueryTooltipSignalArgs args)
    {
        // Keyboard-triggered tooltips carry no meaningful pointer position; there's no "focused chapter" concept on the scrubber, so suppress.
        if (args.KeyboardMode)
        {
            return false;
        }
        RefreshGeometry();
        var chapter = HitTestTickArea(args.X, args.Y);
        if (chapter == null)
        {
            return false;
        }
        args.Tooltip.SetText(ChapterLabel.For(chapter));
        return true;
    }

    private void OnScaleLeave(Gtk.EventControllerMotion sender, EventArgs args)
    {
        if (!hoveredChapterTime.HasValue)
        {
            return;
        }
        hoveredChapterTime = null;
        scale.SetCursorFromName(null);
    }

    // Update the chapter set and the duration ticks are computed against. The actual tick rendering happens in DrawHover; this just stashes state and queues a redraw.
    public void SetChapters(IReadOnlyList<MediaChapter> newChapters, double newDurationSeconds)
    {
        if (newChapters == null)
        {
            throw new ArgumentNullException(nameof(newChapters));
        }
        chapters = newChapters;
        durationSeconds = newDurationSeconds;
        // Hover identity may no longer correspond to where the cursor sits (chapter X positions just changed). Clear; next OnMotion will re-establish.
        if (hoveredChapterTime.HasValue)
        {
            hoveredChapterTime = null;
            scale.SetCursorFromName(null);
        }
        hoverLayer.QueueDraw();
    }

    // Paints every chapter as TWO 2-px-wide vertical Cairo line segments at the trough-relative X for that chapter's time: one above the trough (from troughTop-10 to troughTop-4) and one below (from troughBottom+4 to troughBottom+10). The 4-px inner gap on each side gives the trough room to breathe so the marker reads as bracketing the trough rather than crossing it. Both segments land inside the scale's natural slider-thumb-height padding, so the bar's allocated height is unchanged. Ticks use the scale's theme foreground color (which by definition contrasts with the bar background) at partial alpha — TickAlpha is the single contrast lever and works symmetrically in light and dark themes, since fg-over-bg blends toward the text color either way.
    private void DrawHover(Gtk.DrawingArea area, Cairo.Context cr, int width, int height)
    {
        if (durationSeconds <= 0 || chapters.Count == 0)
        {
            return;
        }
        // Refresh geometry — the layer paints lazily and the cached values may be stale on first paint after layout.
        RefreshGeometry();
        double effectiveLeft = troughLeftInHoverPx + troughInsetLeftPx;
        double effectiveWidth = troughWidthInHoverPx - troughInsetLeftPx - troughInsetRightPx;
        if (effectiveWidth <= 0)
        {
            return;
        }
        // Two segments per chapter: upper [troughTop-10, troughTop-4] and lower [troughBottom+4, troughBottom+10]. Clamp to [0, height] in case the scale is unusually tight; if a segment ends up zero-length the Cairo stroke is a harmless no-op.
        const double TickInnerGapPx = 4.0;
        const double TickOuterPx = 10.0;
        // Blend fraction from bar background toward the theme foreground (text) color. 0.2 read too faint in both light and dark; 0.3 lifts the contrast while staying subtle enough not to dominate the bar.
        const double TickAlpha = 0.3;
        double troughBottomInHover = troughTopInHoverPx + troughHeightInHoverPx;
        double upperTop = Math.Max(0, troughTopInHoverPx - TickOuterPx);
        double upperBottom = Math.Max(0, troughTopInHoverPx - TickInnerGapPx);
        double lowerTop = Math.Min(height, troughBottomInHover + TickInnerGapPx);
        double lowerBottom = Math.Min(height, troughBottomInHover + TickOuterPx);
        scale.GetStyleContext().GetColor(out var fg);
        double fgR = fg.Red;
        double fgG = fg.Green;
        double fgB = fg.Blue;
        cr.LineWidth = 2.0;
        for (int i = 0; i < chapters.Count; i++)
        {
            double t = chapters[i].TimeSeconds;
            if (t <= 0 || t >= durationSeconds)
            {
                continue;
            }
            double x = effectiveLeft + (t / durationSeconds) * effectiveWidth;
            // Snap to integer X for a sharp 2-px stroke: at integer X the stroke covers [X-1, X+1] — two pixels at full coverage. At half-integer X it would smear over three pixels at partial coverage. The sub-pixel rounding shifts the tick by ≤0.5 px from its mathematical position — invisible against the slider thumb, which itself rounds to integer pixels.
            double sharpX = Math.Round(x);
            cr.SetSourceRgba(fgR, fgG, fgB, TickAlpha);
            cr.MoveTo(sharpX, upperTop);
            cr.LineTo(sharpX, upperBottom);
            cr.Stroke();
            cr.MoveTo(sharpX, lowerTop);
            cr.LineTo(sharpX, lowerBottom);
            cr.Stroke();
        }
    }
}
