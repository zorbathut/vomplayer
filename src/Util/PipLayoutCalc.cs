using System;

namespace Vomplayer.Util;

// Pure layout math for the Picture-in-Picture overlay. Resolves an effective (width, height, marginStart, marginTop) given the parent video region's video display rect, the source's display aspect, and the user's optional manual overrides for width/position. Overrides are PERCENTAGE-BASED — fractions of the primary's *video display rect* (not the surrounding widget allocation) — so a window resize naturally keeps the PiP at the same proportional size and position the user dragged it to AND, when the widget aspect doesn't match the video aspect (letterbox/pillarbox), the PiP stays glued to the visible video region rather than drifting into the black bars. When the user hasn't dragged or resized, defaults to a bottom-left placement at 1/4 the video rect's width — matching the original fixed-corner layout. Once the user takes over any one of {widthFraction, marginStartFraction, marginTopFraction}, the corresponding override flows through and is clamped here for rendering. The user's stored fraction itself is NOT mutated by clamping — the caller is expected to leave the fraction untouched between drags so a transient window-shrink-induced clamp doesn't erase the original placement (the PiP springs back when the window grows again). Per-call rounding from the fraction→pixel conversion stays bounded because the fraction is the source of truth on every call.
public static class PipLayoutCalc
{
    public readonly record struct Result(int Width, int Height, int MarginStart, int MarginTop);
    public readonly record struct VideoRect(int X, int Y, int Width, int Height);

    // Compute the rendered video's display rect inside the primary widget's allocation. With a known primary video aspect and a widget whose aspect doesn't match, mpv letterboxes (top/bottom black bars) or pillarboxes (left/right) — this helper returns the inner rect those black bars surround. When the primary has no aspect (no file loaded, or pre-decode), the entire widget is treated as the video region: the user-visible content fills the widget either way (just black instead of video), and a "PiP relative to whatever's there" semantic is the sensible default. Returns (0,0,0,0)-shaped rects when the widget itself isn't allocated.
    public static VideoRect ComputeVideoRect(int widgetWidth, int widgetHeight, double? primaryAspect)
    {
        if (widgetWidth <= 0 || widgetHeight <= 0)
        {
            int w = widgetWidth > 0 ? widgetWidth : 0;
            int h = widgetHeight > 0 ? widgetHeight : 0;
            return new VideoRect(0, 0, w, h);
        }
        if (!primaryAspect.HasValue || primaryAspect.Value <= 0 || double.IsNaN(primaryAspect.Value) || double.IsInfinity(primaryAspect.Value))
        {
            return new VideoRect(0, 0, widgetWidth, widgetHeight);
        }
        double a = primaryAspect.Value;
        double widgetA = (double)widgetWidth / widgetHeight;
        if (widgetA > a)
        {
            // Widget wider than video → pillarbox left/right.
            int videoW = (int)Math.Round(widgetHeight * a);
            if (videoW > widgetWidth)
            {
                videoW = widgetWidth;
            }
            int videoX = (widgetWidth - videoW) / 2;
            return new VideoRect(videoX, 0, videoW, widgetHeight);
        }
        else
        {
            // Widget taller than (or equal aspect to) video → letterbox top/bottom.
            int videoH = (int)Math.Round(widgetWidth / a);
            if (videoH > widgetHeight)
            {
                videoH = widgetHeight;
            }
            int videoY = (widgetHeight - videoH) / 2;
            return new VideoRect(0, videoY, widgetWidth, videoH);
        }
    }

    public static Result Compute(
        VideoRect videoRect,
        double aspect,
        int defaultMargin,
        double? userWidthFraction,
        double? userMarginStartFraction,
        double? userMarginTopFraction,
        int minWidth,
        int minHeight)
    {
        if (aspect <= 0 || double.IsNaN(aspect) || double.IsInfinity(aspect))
        {
            aspect = 16.0 / 9.0;
        }

        // Convert percentage overrides to pixel values against the video rect's dimensions. When the rect's W or H is 0 (pre-allocation), drop the override and fall through to the default placement; the next allocation-change tick will re-apply the user's fraction at the new rect size.
        int? userWidth = (userWidthFraction.HasValue && videoRect.Width > 0)
            ? (int)Math.Round(userWidthFraction.Value * videoRect.Width)
            : (int?)null;
        int? userMarginStartInRect = (userMarginStartFraction.HasValue && videoRect.Width > 0)
            ? (int)Math.Round(userMarginStartFraction.Value * videoRect.Width)
            : (int?)null;
        int? userMarginTopInRect = (userMarginTopFraction.HasValue && videoRect.Height > 0)
            ? (int)Math.Round(userMarginTopFraction.Value * videoRect.Height)
            : (int?)null;

        // Width: default = 1/4 video-rect width with a min floor; user override wins. Pre-allocation case (videoRect.Width <= 0) falls back to a 2× min so the corner thumbnail is visible until the first geometry change.
        int defaultW;
        if (videoRect.Width > 0)
        {
            defaultW = Math.Max(minWidth, videoRect.Width / 4);
        }
        else
        {
            defaultW = minWidth * 2;
        }
        int width = userWidth ?? defaultW;
        if (width < minWidth)
        {
            width = minWidth;
        }
        if (videoRect.Width > 0 && width > videoRect.Width)
        {
            width = videoRect.Width;
        }

        int height = (int)Math.Round(width / aspect);
        if (height < minHeight)
        {
            height = minHeight;
            width = (int)Math.Round(height * aspect);
            if (videoRect.Width > 0 && width > videoRect.Width)
            {
                width = videoRect.Width;
            }
        }
        if (videoRect.Height > 0 && height > videoRect.Height)
        {
            height = videoRect.Height;
            width = (int)Math.Round(height * aspect);
            if (videoRect.Width > 0 && width > videoRect.Width)
            {
                width = videoRect.Width;
                height = (int)Math.Round(width / aspect);
            }
        }

        // Margins are first computed *inside* the video rect (i.e. relative to its top-left corner), then translated to widget-space at the end by adding videoRect.{X,Y}. Default placement = bottom-left of the video rect with `defaultMargin` from each edge.
        int marginStartInRect = userMarginStartInRect ?? defaultMargin;
        int marginTopInRect;
        if (userMarginTopInRect.HasValue)
        {
            marginTopInRect = userMarginTopInRect.Value;
        }
        else if (videoRect.Height > 0)
        {
            marginTopInRect = videoRect.Height - height - defaultMargin;
            if (marginTopInRect < 0)
            {
                marginTopInRect = 0;
            }
        }
        else
        {
            marginTopInRect = 0;
        }

        // Clamp into video-rect bounds. The order matters: clamp the upper-right edge first so a too-large user value gets pulled back, then floor at zero so we never go negative.
        if (videoRect.Width > 0)
        {
            int maxStart = videoRect.Width - width;
            if (marginStartInRect > maxStart)
            {
                marginStartInRect = maxStart;
            }
            if (marginStartInRect < 0)
            {
                marginStartInRect = 0;
            }
        }
        else if (marginStartInRect < 0)
        {
            marginStartInRect = 0;
        }
        if (videoRect.Height > 0)
        {
            int maxTop = videoRect.Height - height;
            if (marginTopInRect > maxTop)
            {
                marginTopInRect = maxTop;
            }
            if (marginTopInRect < 0)
            {
                marginTopInRect = 0;
            }
        }
        else if (marginTopInRect < 0)
        {
            marginTopInRect = 0;
        }

        // Translate in-rect margins to widget-space output (the consumer's wrapper sits in the parent overlay's coordinate frame, so add the rect's offset within that frame).
        return new Result(width, height, videoRect.X + marginStartInRect, videoRect.Y + marginTopInRect);
    }

    // Aspect-locked corner-resize: project the pointer offset (dx, dy) from the grab point onto the line through the origin in direction (1, 1/aspect). Returns the width-delta along the aspect line. This is the standard "snap to aspect" behavior — pure-X drag yields a sub-dx width change because the corner can only travel along the aspect line; pure-diagonal-on-aspect drag yields a 1:1 width change.
    public static double ProjectAspectResize(double dx, double dy, double aspect)
    {
        if (aspect <= 0 || double.IsNaN(aspect) || double.IsInfinity(aspect))
        {
            aspect = 16.0 / 9.0;
        }
        double aspectInv = 1.0 / aspect;
        return (dx + dy * aspectInv) / (1.0 + aspectInv * aspectInv);
    }
}
