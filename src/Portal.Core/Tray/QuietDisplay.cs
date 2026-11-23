namespace Portal.Core.Tray;

/// <summary>Pure display choice for the test-only quiet launch: never the primary monitor.</summary>
public static class QuietDisplay
{
    /// <summary>Index of the first display whose bounds do not contain the origin (the primary display), or -1 when there is none.</summary>
    public static int PickNonPrimary(IReadOnlyList<PxRect> outerBounds)
    {
        for (int i = 0; i < outerBounds.Count; i++)
        {
            PxRect r = outerBounds[i];
            bool containsOrigin = r.Left <= 0 && 0 < r.Right && r.Top <= 0 && 0 < r.Bottom;
            if (!containsOrigin) return i;
        }
        return -1;
    }

    /// <summary>Top-left of a window of the given size centred in the work area, kept inside it.</summary>
    public static (int X, int Y) CenterIn(PxRect work, int width, int height) =>
        (work.Left + Math.Max(0, (work.Width - width) / 2), work.Top + Math.Max(0, (work.Height - height) / 2));
}
