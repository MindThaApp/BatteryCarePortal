using System.Globalization;

namespace Portal.Core.Battery;

/// <summary>
/// Pure battery arithmetic: wear and health, signed power and current, time estimates, unit conversions
/// and multi-battery totals. Every method returns null when an input is missing, never a made-up value.
/// </summary>
public static class BatteryMath
{
    /// <summary>Full-charge capacity as a share of design capacity, one decimal. Capped at 150.</summary>
    public static double? HealthPercent(int? designMwh, int? fullMwh) =>
        designMwh is int d && d > 0 && fullMwh is int f && f > 0
            ? Math.Round(Math.Clamp(f * 100.0 / d, 0, 150), 1)
            : null;

    /// <summary>Capacity lost to ageing: 100 minus health, never below zero.</summary>
    public static double? WearPercent(int? designMwh, int? fullMwh) =>
        HealthPercent(designMwh, fullMwh) is double h ? Math.Round(Math.Max(0, 100 - h), 1) : null;

    /// <summary>Signed power in mW: charging is positive, discharging is negative, idle is zero.</summary>
    public static int? SignedPowerMw(int? chargeRateMw, int? dischargeRateMw)
    {
        if (chargeRateMw is int c && c > 0) return c;
        if (dischargeRateMw is int d && d > 0) return -d;
        if (chargeRateMw == 0 || dischargeRateMw == 0) return 0;
        return null;
    }

    public static double? PowerWatts(int? powerMw) => powerMw is int p ? p / 1000.0 : null;

    /// <summary>Signed current in mA from power and voltage (mW / V = mA).</summary>
    public static int? CurrentMa(int? powerMw, int? voltageMv) =>
        powerMw is int p && voltageMv is int v && v > 0 ? (int)Math.Round(p * 1000.0 / v) : null;

    public static double? MvToVolts(int? mv) => mv is int v ? v / 1000.0 : null;

    public static double? MwhToWh(int? mwh) => mwh is int v ? v / 1000.0 : null;

    /// <summary>Time until empty at the current discharge rate.</summary>
    public static TimeSpan? TimeToEmpty(int? remainingMwh, int? dischargeRateMw) =>
        remainingMwh is int r && r >= 0 && dischargeRateMw is int d && d > 0 ? TimeSpan.FromHours(r / (double)d) : null;

    /// <summary>Time until the battery reaches <paramref name="targetMwh"/> (normally the full-charge capacity).</summary>
    public static TimeSpan? TimeToFull(int? remainingMwh, int? targetMwh, int? chargeRateMw) =>
        remainingMwh is int r && targetMwh is int t && chargeRateMw is int c && c > 0 && t > r
            ? TimeSpan.FromHours((t - r) / (double)c)
            : null;

    /// <summary>Shows only the last four characters of a serial number, for example "••••3F9A". Null when empty.</summary>
    public static string? MaskSerial(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial)) return null;
        string s = serial.Trim();
        return s.Length <= 4 ? s : new string('•', Math.Min(4, s.Length - 4)) + s[^4..];
    }

    /// <summary>
    /// Parses the manufacture date strings drivers use: "20210315", "2021-03-15" or the WMI date-time form
    /// "20210315000000.000000+000". Anything else, or an implausible year, gives null.
    /// </summary>
    public static DateOnly? ParseManufactureDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string t = text.Trim();
        string[] formats = { "yyyyMMdd", "yyyy-MM-dd", "yyyy/MM/dd" };
        string head = t.Length >= 8 && t.Take(8).All(char.IsDigit) ? t[..8] : t;
        if (DateOnly.TryParseExact(head, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
            return IsPlausible(date) ? date : null;
        return null;
    }

    public static DateOnly? FromDayMonthYear(int day, int month, int year)
    {
        if (year < 1995 || year > 2100 || month is < 1 or > 12 || day < 1) return null;
        if (day > DateTime.DaysInMonth(year, month)) return null;
        return new DateOnly(year, month, day);
    }

    private static bool IsPlausible(DateOnly d) => d.Year is >= 1995 and <= 2100;

    /// <summary>
    /// Combined totals for several batteries. Capacities are only totalled when every battery supplied
    /// that value, because summing a subset would look complete while being wrong.
    /// </summary>
    public static BatteryTotals Aggregate(IReadOnlyList<BatteryUnit> units)
    {
        if (units.Count == 0) return new BatteryTotals();
        var volts = units.Where(u => u.VoltageMv > 0).Select(u => u.VoltageMv!.Value).ToList();
        var temps = units.Where(u => u.TemperatureC is not null).Select(u => u.TemperatureC!.Value).ToList();
        var cycles = units.Where(u => u.CycleCount > 0).Select(u => u.CycleCount!.Value).ToList();
        return new BatteryTotals
        {
            DesignCapacityMwh = SumAll(units, u => u.DesignCapacityMwh),
            FullChargeCapacityMwh = SumAll(units, u => u.FullChargeCapacityMwh),
            RemainingCapacityMwh = SumAll(units, u => u.RemainingCapacityMwh),
            ChargeRateMw = units.Any(u => u.ChargeRateMw > 0) ? units.Sum(u => u.ChargeRateMw ?? 0) : null,
            DischargeRateMw = units.Any(u => u.DischargeRateMw > 0) ? units.Sum(u => u.DischargeRateMw ?? 0) : null,
            VoltageMv = volts.Count > 0 ? (int)Math.Round(volts.Average()) : null,
            TemperatureC = temps.Count > 0 ? temps.Max() : null,
            CycleCount = cycles.Count > 0 ? cycles.Max() : null,
        };
    }

    private static int? SumAll(IReadOnlyList<BatteryUnit> units, Func<BatteryUnit, int?> pick)
    {
        long total = 0;
        foreach (BatteryUnit u in units)
        {
            if (pick(u) is not int v || v < 0) return null;
            total += v;
        }
        return total <= int.MaxValue ? (int)total : null;
    }
}

/// <summary>Combined values across all batteries. Voltage is the mean, temperature the hottest, cycles the highest.</summary>
public sealed record BatteryTotals
{
    public int? DesignCapacityMwh { get; init; }
    public int? FullChargeCapacityMwh { get; init; }
    public int? RemainingCapacityMwh { get; init; }
    public int? ChargeRateMw { get; init; }
    public int? DischargeRateMw { get; init; }
    public int? VoltageMv { get; init; }
    public double? TemperatureC { get; init; }
    public int? CycleCount { get; init; }
}

/// <summary>One physical battery. Unreported values are null.</summary>
public sealed record BatteryUnit
{
    public string Name { get; init; } = "";
    public string? Manufacturer { get; init; }
    public string? DeviceName { get; init; }
    /// <summary>Full serial number; display it through <see cref="BatteryMath.MaskSerial"/>.</summary>
    public string? SerialNumber { get; init; }
    public DateOnly? ManufactureDate { get; init; }
    public string? Chemistry { get; init; }
    public int? DesignCapacityMwh { get; init; }
    public int? FullChargeCapacityMwh { get; init; }
    public int? RemainingCapacityMwh { get; init; }
    public int? VoltageMv { get; init; }
    public int? CycleCount { get; init; }
    public double? TemperatureC { get; init; }
    public int? ChargeRateMw { get; init; }
    public int? DischargeRateMw { get; init; }

    public double? HealthPercent => BatteryMath.HealthPercent(DesignCapacityMwh, FullChargeCapacityMwh);
    public double? WearPercent => BatteryMath.WearPercent(DesignCapacityMwh, FullChargeCapacityMwh);
    public int? PowerMw => BatteryMath.SignedPowerMw(ChargeRateMw, DischargeRateMw);
}

/// <summary>Value-equal list of batteries, so snapshot change detection keeps working.</summary>
public sealed class BatteryUnitList : IReadOnlyList<BatteryUnit>, IEquatable<BatteryUnitList>
{
    public static BatteryUnitList Empty { get; } = new(Array.Empty<BatteryUnit>());
    private readonly BatteryUnit[] _items;

    public BatteryUnitList(IEnumerable<BatteryUnit> items) => _items = items.ToArray();

    public int Count => _items.Length;
    public BatteryUnit this[int index] => _items[index];
    public IEnumerator<BatteryUnit> GetEnumerator() => ((IEnumerable<BatteryUnit>)_items).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _items.GetEnumerator();

    public bool Equals(BatteryUnitList? other) => other is not null && _items.AsSpan().SequenceEqual(other._items);
    public override bool Equals(object? obj) => Equals(obj as BatteryUnitList);
    public override int GetHashCode() => _items.Length;
}
