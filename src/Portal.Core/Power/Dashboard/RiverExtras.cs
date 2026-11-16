using Portal.Core.Theming;

namespace Portal.Core.Power.Dashboard;

public enum LiveChipKind
{
    Total,
    Cpu,
    Gpu,
    Battery,
    Other,
}

/// <summary>One value of the row above the river. <see cref="Watts"/> is signed for the battery: positive charging, negative discharging.</summary>
public sealed record LiveChip(LiveChipKind Kind, double Watts, PowerQuality Quality);

/// <summary>The values that stay visible when the river card is folded: total, CPU, graphics, battery and the rest.</summary>
public static class LiveChips
{
    private static readonly PowerCategory[] OtherCategories =
        [PowerCategory.Memory, PowerCategory.Storage, PowerCategory.Fans, PowerCategory.Motherboard, PowerCategory.Peripherals];

    /// <summary>Chips in a fixed order. A chip with no value on this PC is left out; the total is left out only before the first reading.</summary>
    public static IReadOnlyList<LiveChip> Build(PowerTreeSnapshot s)
    {
        var chips = new List<LiveChip>();
        if (s.Root.HasValue && s.Root.Watts is double total && double.IsFinite(total))
            chips.Add(new LiveChip(LiveChipKind.Total, total, s.Root.Quality));

        PowerNode[] top = [.. s.Root.Children.Where(c => c.HasValue && c.Watts is double w && double.IsFinite(w))];
        PowerNode[] cpu = [.. top.Where(c => c.Category == PowerCategory.Cpu)];
        if (cpu.Length > 0) chips.Add(Sum(LiveChipKind.Cpu, cpu));
        PowerNode[] gpu = [.. top.Where(c => c.Category == PowerCategory.Gpu)];
        if (gpu.Length > 0) chips.Add(Sum(LiveChipKind.Gpu, gpu));
        if (s.BatteryWatts is double b && double.IsFinite(b)) chips.Add(new LiveChip(LiveChipKind.Battery, b, PowerQuality.Measured));
        PowerNode[] other = [.. top.Where(c => OtherCategories.Contains(c.Category))];
        if (other.Length > 0 && other.Sum(c => c.Watts!.Value) > 0) chips.Add(Sum(LiveChipKind.Other, other));
        return chips;
    }

    private static LiveChip Sum(LiveChipKind kind, PowerNode[] nodes) =>
        new(kind, Math.Round(nodes.Sum(n => n.Watts!.Value), 2), PowerNodes.Combine(nodes));
}

/// <summary>
/// The gentle colour shift along the river: the accent at the source drifts a little cooler and lighter towards the sinks.
/// Pure colour maths on the accent so every accent colour gets the same, small change.
/// </summary>
public static class RiverTint
{
    /// <summary>Largest hue change, in degrees, between the source and the sinks.</summary>
    public const double MaxHueShift = 16;
    /// <summary>Hue the water drifts towards (a cool blue).</summary>
    public const double CoolHue = 205;
    /// <summary>Share of the remaining lightness added at the sinks.</summary>
    public const double MaxLighten = 0.12;

    /// <summary><paramref name="t"/> is 0 at the start of the source and 1 at the mouths of the sinks.</summary>
    public static Rgb At(Rgb accent, double t)
    {
        t = double.IsFinite(t) ? Math.Clamp(t, 0, 1) : 0;
        if (t <= 0) return accent;
        (double h, double s, double l) = ColorMath.ToHsl(accent);
        double delta = ((CoolHue - h) % 360 + 540) % 360 - 180;
        double hue = h + Math.Clamp(delta, -MaxHueShift, MaxHueShift) * t;
        double light = l + (1 - l) * MaxLighten * t;
        double sat = s * (1 - 0.08 * t);
        return ColorMath.FromHsl(hue, sat, light);
    }
}
