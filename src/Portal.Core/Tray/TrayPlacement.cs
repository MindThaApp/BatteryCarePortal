namespace Portal.Core.Tray;

/// <summary>A rectangle in physical pixels (left/top inclusive, right/bottom exclusive).</summary>
public readonly record struct PxRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public int CenterX => (Left + Right) / 2;
    public int CenterY => (Top + Bottom) / 2;
}

public enum TaskbarEdge { None, Bottom, Top, Left, Right }

/// <summary>Where the tray pop-up goes. Pure maths on pixels of one monitor; the caller supplies that monitor's work area.</summary>
public static class TrayPlacement
{
    public const double DefaultWidthDip = 380;
    public const double MaxWidthDip = 560;
    public const double MinWidthDip = 300;

    /// <summary>The taskbar edge the icon sits on: outside the work area on that side. None when the icon is inside the work area (hidden-icons flyout, auto-hide, cursor fallback).</summary>
    public static TaskbarEdge EdgeOf(PxRect anchor, PxRect work)
    {
        if (anchor.CenterY >= work.Bottom) return TaskbarEdge.Bottom;
        if (anchor.CenterY < work.Top) return TaskbarEdge.Top;
        if (anchor.CenterX < work.Left) return TaskbarEdge.Left;
        if (anchor.CenterX >= work.Right) return TaskbarEdge.Right;
        return TaskbarEdge.None;
    }

    /// <summary>Opens from the icon toward the screen interior, <paramref name="gap"/> pixels from the taskbar and the work-area edges.</summary>
    public static PxRect Place(PxRect anchor, PxRect work, int width, int height, int gap)
    {
        width = Math.Min(width, Math.Max(1, work.Width - 2 * gap));
        height = Math.Min(height, Math.Max(1, work.Height - 2 * gap));
        int x, y;
        switch (EdgeOf(anchor, work))
        {
            case TaskbarEdge.Bottom: x = anchor.CenterX - width / 2; y = Math.Min(anchor.Top, work.Bottom) - gap - height; break;
            case TaskbarEdge.Top: x = anchor.CenterX - width / 2; y = Math.Max(anchor.Bottom, work.Top) + gap; break;
            case TaskbarEdge.Left: x = Math.Max(anchor.Right, work.Left) + gap; y = anchor.CenterY - height / 2; break;
            case TaskbarEdge.Right: x = Math.Min(anchor.Left, work.Right) - gap - width; y = anchor.CenterY - height / 2; break;
            default:
                x = anchor.CenterX - width / 2;
                y = anchor.Top - gap - height;
                if (y < work.Top + gap) y = anchor.Bottom + gap;
                break;
        }
        x = Math.Clamp(x, work.Left + gap, Math.Max(work.Left + gap, work.Right - width - gap));
        y = Math.Clamp(y, work.Top + gap, Math.Max(work.Top + gap, work.Bottom - height - gap));
        return new PxRect(x, y, x + width, y + height);
    }

    /// <summary>Width in DIP: the content's natural width within 300..560, never wider than the work area less both gaps.</summary>
    public static double WidthDip(double naturalDip, double roomDip) =>
        Math.Min(Math.Clamp(naturalDip, MinWidthDip, MaxWidthDip), Math.Max(1, roomDip));

    /// <summary>Height in DIP: the content's natural height, capped to the room the work area leaves.</summary>
    public static double HeightDip(double naturalDip, double roomDip) => Math.Max(1, Math.Min(naturalDip, roomDip));

    /// <summary>
    /// Height in DIP for the scrolling tile area when it does not all fit: ends after the last whole tile row and one row gap,
    /// so the cut falls between tiles. All rows fit gives the full content height. When not even the first row fits the
    /// available height is used as is.
    /// </summary>
    public static double TileViewportDip(double availableDip, IReadOnlyList<double> rowHeightsDip, double rowGapDip, double padTopDip, double padBottomDip)
    {
        double full = padTopDip + padBottomDip + rowHeightsDip.Sum() + Math.Max(0, rowHeightsDip.Count - 1) * rowGapDip;
        if (rowHeightsDip.Count == 0 || full <= availableDip) return Math.Min(full, Math.Max(1, availableDip));
        double used = padTopDip;
        double best = 0;
        foreach (double row in rowHeightsDip)
        {
            used += row + rowGapDip;
            if (used > availableDip) break;
            best = used;
        }
        return best > 0 ? best : Math.Max(1, availableDip);
    }
}
