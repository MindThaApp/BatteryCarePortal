namespace Portal.Core.Battery;

/// <summary>Geometry for the level ring: 0% at the top, clockwise, a full circle at 100%.</summary>
public static class BatteryRingMath
{
    public static double Degrees(double percent) => Math.Clamp(percent, 0, 100) * 3.6;

    /// <summary>Point on a circle of <paramref name="radius"/> around (cx, cy) at the angle for <paramref name="percent"/>. Y grows downward.</summary>
    public static (double X, double Y) PointAt(double cx, double cy, double radius, double percent)
    {
        double rad = Degrees(percent) * Math.PI / 180.0;
        return (cx + radius * Math.Sin(rad), cy - radius * Math.Cos(rad));
    }

    /// <summary>A 100% arc cannot be drawn from one point to the same point, so it stops a hair short.</summary>
    public static double ArcPercent(double percent) => Math.Min(Math.Clamp(percent, 0, 100), 99.99);
}
