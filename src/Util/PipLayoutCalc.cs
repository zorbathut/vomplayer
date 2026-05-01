using System;

namespace Vomplayer.Util;

// Pure layout math for the Picture-in-Picture overlay. Resolves an effective (width, height, marginStart, marginTop) given the parent video region's allocation, the source's display aspect, and the user's optional manual overrides for width/position. When the user hasn't dragged or resized, defaults to a bottom-left placement at 1/4 the parent width — matching the original fixed-corner layout. Once the user takes over any one of {width, marginStart, marginTop}, the corresponding override flows through and is clamped here. MainWindow.Pip.cs writes back the clamped values so subsequent drags resume from a valid position even after a window-resize-induced clamp.
public static class PipLayoutCalc
{
    public readonly record struct Result(int Width, int Height, int MarginStart, int MarginTop);

    public static Result Compute(
        int primaryWidth,
        int primaryHeight,
        double aspect,
        int defaultMargin,
        int? userWidth,
        int? userMarginStart,
        int? userMarginTop,
        int minWidth,
        int minHeight)
    {
        if (aspect <= 0 || double.IsNaN(aspect) || double.IsInfinity(aspect))
        {
            aspect = 16.0 / 9.0;
        }

        // Width: default = 1/4 parent width with a min floor; user override wins. Pre-allocation case (primaryWidth <= 0) falls back to a 2× min so the corner thumbnail is visible until the first geometry change.
        int defaultW;
        if (primaryWidth > 0)
        {
            defaultW = Math.Max(minWidth, primaryWidth / 4);
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
        if (primaryWidth > 0 && width > primaryWidth)
        {
            width = primaryWidth;
        }

        int height = (int)Math.Round(width / aspect);
        if (height < minHeight)
        {
            height = minHeight;
            width = (int)Math.Round(height * aspect);
            if (primaryWidth > 0 && width > primaryWidth)
            {
                width = primaryWidth;
            }
        }
        if (primaryHeight > 0 && height > primaryHeight)
        {
            height = primaryHeight;
            width = (int)Math.Round(height * aspect);
            if (primaryWidth > 0 && width > primaryWidth)
            {
                width = primaryWidth;
                height = (int)Math.Round(width / aspect);
            }
        }

        // Margins. Default placement = bottom-left with `defaultMargin` from each edge.
        int marginStart = userMarginStart ?? defaultMargin;
        int marginTop;
        if (userMarginTop.HasValue)
        {
            marginTop = userMarginTop.Value;
        }
        else if (primaryHeight > 0)
        {
            marginTop = primaryHeight - height - defaultMargin;
            if (marginTop < 0)
            {
                marginTop = 0;
            }
        }
        else
        {
            marginTop = 0;
        }

        // Clamp into parent bounds. The order matters: clamp the upper-right edge first so a too-large user value gets pulled back, then floor at zero so we never go negative.
        if (primaryWidth > 0)
        {
            int maxStart = primaryWidth - width;
            if (marginStart > maxStart)
            {
                marginStart = maxStart;
            }
            if (marginStart < 0)
            {
                marginStart = 0;
            }
        }
        else if (marginStart < 0)
        {
            marginStart = 0;
        }
        if (primaryHeight > 0)
        {
            int maxTop = primaryHeight - height;
            if (marginTop > maxTop)
            {
                marginTop = maxTop;
            }
            if (marginTop < 0)
            {
                marginTop = 0;
            }
        }
        else if (marginTop < 0)
        {
            marginTop = 0;
        }

        return new Result(width, height, marginStart, marginTop);
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
