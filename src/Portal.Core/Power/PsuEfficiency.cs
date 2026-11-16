namespace Portal.Core.Power;

/// <summary>PSU efficiency by 80 Plus level and load fraction (115 V internal figures, optional high-line bonus).</summary>
public static class PsuEfficiency
{
    // Load fraction points and efficiency per level. 20/50/100 % are the certification points;
    // the low-load points follow typical review measurements (efficiency falls quickly below 10 %).
    private static readonly double[] Loads = [0.02, 0.05, 0.10, 0.20, 0.50, 1.00];

    private static double[] Curve(EightyPlusLevel level) => level switch
    {
        EightyPlusLevel.None => [0.50, 0.62, 0.70, 0.75, 0.78, 0.75],
        EightyPlusLevel.White => [0.55, 0.68, 0.76, 0.80, 0.80, 0.80],
        EightyPlusLevel.Bronze => [0.58, 0.70, 0.78, 0.82, 0.85, 0.82],
        EightyPlusLevel.Silver => [0.60, 0.73, 0.81, 0.85, 0.88, 0.85],
        EightyPlusLevel.Platinum => [0.66, 0.79, 0.87, 0.90, 0.92, 0.89],
        EightyPlusLevel.Titanium => [0.70, 0.83, 0.90, 0.92, 0.94, 0.90],
        _ => [0.63, 0.76, 0.84, 0.87, 0.90, 0.87], // Gold
    };

    /// <summary>Efficiency (0–1) at <paramref name="dcWatts"/> output for a PSU rated <paramref name="ratedWatts"/>.</summary>
    public static double At(double dcWatts, double ratedWatts, EightyPlusLevel level, bool highLine = false)
    {
        double[] curve = Curve(level);
        double load = ratedWatts > 0 && double.IsFinite(dcWatts) ? Math.Max(0, dcWatts) / ratedWatts : Loads[0];
        double eff;
        if (load <= Loads[0]) eff = curve[0];
        else if (load >= Loads[^1]) eff = curve[^1];
        else
        {
            int i = 1;
            while (Loads[i] < load) i++;
            double t = (load - Loads[i - 1]) / (Loads[i] - Loads[i - 1]);
            eff = curve[i - 1] + t * (curve[i] - curve[i - 1]);
        }
        if (highLine) eff += 0.015;
        return Math.Clamp(eff, 0.3, 0.97);
    }

    /// <summary>Wall-side watts for a DC load.</summary>
    public static double WallFromDc(double dcWatts, double ratedWatts, EightyPlusLevel level, bool highLine = false) =>
        dcWatts <= 0 ? 0 : dcWatts / At(dcWatts, ratedWatts, level, highLine);

    /// <summary>DC watts that would draw <paramref name="wallWatts"/> at the wall (fixed-point on the curve).</summary>
    public static double DcFromWall(double wallWatts, double ratedWatts, EightyPlusLevel level, bool highLine = false)
    {
        if (wallWatts <= 0 || !double.IsFinite(wallWatts)) return 0;
        double dc = wallWatts * 0.85;
        for (int i = 0; i < 12; i++) dc = wallWatts * At(dc, ratedWatts, level, highLine);
        return dc;
    }
}
