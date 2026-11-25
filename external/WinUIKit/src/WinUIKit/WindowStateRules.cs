using System.Globalization;

namespace WinUIKit;

/// <summary>
/// How the UI state remembered by a modal navigation window is applied once read back (window size, start page). The
/// values come from the app's settings file and may have been corrupted by hand edits, written by an older version, or
/// refer to pages that no longer exist; this class handles all of that in one place. Pure functions that never touch a
/// window, unit-tested directly (WindowStateRulesTests).
/// </summary>
internal static class WindowStateRules
{
    /// <summary>Distance kept between the window and the work area edge (logical pixels).</summary>
    internal const int DisplayEdgeMarginDip = 16;

    /// <summary>
    /// Upper bound for a remembered window size (logical pixels). Any real screen is far smaller; absurd values from hand
    /// edits are clamped here first and then clipped to the work area by <see cref="FitSize"/>.
    /// (Since .NET 9, out-of-range float-to-int conversions saturate rather than overflow to negative values; this only
    /// keeps the numbers in a sensible range, it isn't an overflow fix.)
    /// </summary>
    internal const double MaxRememberedDip = 32768;

    /// <summary>Lower bound for available width/height (physical pixels): however small the work area, the window is never squeezed below this.</summary>
    internal const int MinAvailablePixels = 320;

    /// <summary>
    /// Stored window size → usable value: missing, not a number, not finite, or not greater than 0 → the default; below
    /// the minimum → raised to the minimum; above <see cref="MaxRememberedDip"/> → clamped to it (then clipped to the work
    /// area by <see cref="FitSize"/>). Only the invariant culture format (decimal point) is accepted, matching how it is saved.
    /// </summary>
    internal static double ParseDimension(string? saved, double fallback, double minimum)
    {
        if (!double.TryParse(saved, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || !double.IsFinite(value) || value <= 0)
            return fallback;
        return Math.Min(Math.Max(minimum, value), MaxRememberedDip);
    }

    /// <summary>
    /// Determines the window's physical-pixel size and minimum size from the work area: subtract the edge margins first;
    /// when the work area can't fit the design minimum (low resolution or high scaling), lower the minimum to the available
    /// size; the window size is clamped between the minimum and the available size.
    /// </summary>
    internal static (int Width, int Height, int MinWidth, int MinHeight) FitSize(
        double widthDip, double heightDip, double minWidthDip, double minHeightDip, double scale, int workWidth, int workHeight)
    {
        if (!(scale > 0) || !double.IsFinite(scale)) scale = 1;
        int edgeMargin = Math.Max(1, ToPixels(DisplayEdgeMarginDip, scale));
        int availableWidth = Math.Max(MinAvailablePixels, workWidth - edgeMargin * 2);
        int availableHeight = Math.Max(MinAvailablePixels, workHeight - edgeMargin * 2);
        int minWidth = Math.Min(ToPixels(minWidthDip, scale), availableWidth);
        int minHeight = Math.Min(ToPixels(minHeightDip, scale), availableHeight);
        return (Math.Clamp(ToPixels(widthDip, scale), minWidth, availableWidth),
                Math.Clamp(ToPixels(heightDip, scale), minHeight, availableHeight),
                minWidth, minHeight);
    }

    /// <summary>Logical → physical pixels: clamp to the int range in floating point before rounding (not relying on how the runtime handles out-of-range conversions); NaN counts as 0.</summary>
    internal static int ToPixels(double dip, double scale)
    {
        double pixels = dip * scale;
        if (!double.IsFinite(pixels)) pixels = double.IsPositiveInfinity(pixels) ? int.MaxValue : 0;
        return (int)Math.Round(Math.Clamp(pixels, 0, int.MaxValue));
    }

    /// <summary>
    /// Which page to open on: the remembered page if it still exists and is usable; otherwise the first usable page; if
    /// none is usable, the first page; null if there are no pages.
    /// </summary>
    internal static string? ChooseStartPage(string? saved, IReadOnlyList<string> order, Func<string, bool> usable)
    {
        if (!string.IsNullOrEmpty(saved) && order.Contains(saved) && usable(saved))
            return saved;
        foreach (string tag in order)
        {
            if (usable(tag))
                return tag;
        }
        return order.Count > 0 ? order[0] : null;
    }
}
