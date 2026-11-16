namespace Portal.Core.Battery;

/// <summary>
/// One point-in-time reading of the system battery. Every value the hardware or driver does not
/// report is null, so the UI can hide it instead of showing a misleading zero.
/// </summary>
public sealed record BatterySnapshot
{
    public static BatterySnapshot NotPresent { get; } = new() { Present = false };

    public DateTimeOffset Timestamp { get; init; }
    public bool Present { get; init; }
    public int? Percent { get; init; }
    public bool IsCharging { get; init; }
    public bool IsOnAc { get; init; }
    public int? ChargeRateMw { get; init; }
    public int? DischargeRateMw { get; init; }
    public int? VoltageMv { get; init; }
    public int? RemainingCapacityMwh { get; init; }
    public int? DesignCapacityMwh { get; init; }
    public int? FullChargeCapacityMwh { get; init; }
    public double? HealthPercent { get; init; }
    public int? CycleCount { get; init; }
    public double? TemperatureC { get; init; }
    /// <summary>Estimated time until empty while running on battery.</summary>
    public TimeSpan? TimeRemaining { get; init; }
    /// <summary>Estimated time until full while charging.</summary>
    public TimeSpan? TimeToFull { get; init; }
    public string? Manufacturer { get; init; }
    public string? Chemistry { get; init; }
    public string? DeviceName { get; init; }
    /// <summary>Full serial number of the first battery that reports one; show it through <see cref="BatteryMath.MaskSerial"/>.</summary>
    public string? SerialNumber { get; init; }
    public DateOnly? ManufactureDate { get; init; }
    /// <summary>Per-battery details. The top-level values are the combined totals of these.</summary>
    public BatteryUnitList Batteries { get; init; } = BatteryUnitList.Empty;
    /// <summary>True when the reading comes from the simulated battery (PBC_FAKE_BATTERY=1 or PPM_FAKE_BATTERY=1).</summary>
    public bool IsSimulated { get; init; }

    /// <summary>Capacity lost to ageing (100 minus health), or null when design or full capacity is unknown.</summary>
    public double? WearPercent => BatteryMath.WearPercent(DesignCapacityMwh, FullChargeCapacityMwh);
    /// <summary>Signed power in mW: charging positive, discharging negative.</summary>
    public int? PowerMw => BatteryMath.SignedPowerMw(ChargeRateMw, DischargeRateMw);
    public double? PowerW => BatteryMath.PowerWatts(PowerMw);
    /// <summary>Signed current in mA, derived from power and voltage.</summary>
    public int? CurrentMa => BatteryMath.CurrentMa(PowerMw, VoltageMv);
    public double? VoltageV => BatteryMath.MvToVolts(VoltageMv);
}

/// <summary>Raw per-battery values from root\wmi or the battery device interface, before sanity filtering.</summary>
public sealed record BatteryUnitRaw
{
    public string InstanceName { get; init; } = "";
    public string? Manufacturer { get; init; }
    public string? DeviceName { get; init; }
    public string? SerialNumber { get; init; }
    public DateOnly? ManufactureDate { get; init; }
    public uint? ChemistryCode { get; init; }
    public long? DesignMwh { get; init; }
    public long? FullMwh { get; init; }
    public long? RemainingMwh { get; init; }
    public long? VoltageMv { get; init; }
    public long? ChargeRateMw { get; init; }
    public long? DischargeRateMw { get; init; }
    public long? CycleCount { get; init; }
    public long? TemperatureDeciKelvin { get; init; }
}

/// <summary>
/// Raw values gathered from the individual Windows sources before they are merged.
/// Kept separate from <see cref="BatterySnapshot"/> so the merge rules can be unit-tested.
/// </summary>
public sealed record BatteryRawReadings
{
    // GetSystemPowerStatus
    public byte? AcLineStatus { get; init; }
    public byte? BatteryFlag { get; init; }
    public byte? LifePercent { get; init; }
    public int? LifeTimeSeconds { get; init; }

    // Windows.Devices.Power.Battery.AggregateBattery report
    public string? ReportStatus { get; init; }
    public int? ReportChargeRateMw { get; init; }
    public int? ReportDesignMwh { get; init; }
    public int? ReportFullMwh { get; init; }
    public int? ReportRemainingMwh { get; init; }

    // root\wmi battery classes
    public long? WmiDesignMwh { get; init; }
    public long? WmiFullMwh { get; init; }
    public long? WmiRemainingMwh { get; init; }
    public long? WmiChargeRateMw { get; init; }
    public long? WmiDischargeRateMw { get; init; }
    public long? WmiVoltageMv { get; init; }
    public bool? WmiPowerOnline { get; init; }
    public bool? WmiCharging { get; init; }
    public long? WmiCycleCount { get; init; }
    public long? WmiTemperatureDeciKelvin { get; init; }
    public string? Manufacturer { get; init; }
    public uint? ChemistryCode { get; init; }

    /// <summary>Per-battery readings (WMI rows, filled from the battery device interface). Null when unavailable.</summary>
    public IReadOnlyList<BatteryUnitRaw>? Units { get; init; }
}

public static class BatterySnapshotMapper
{
    private const byte NoSystemBattery = 128;
    private const byte UnknownByte = 255;
    private const byte ChargingFlag = 8;
    private const long WmiUnknown = 0xFFFFFFFF;
    private const long WmiUnknownRate = 0x80000000;

    public static BatterySnapshot Map(BatteryRawReadings raw, DateTimeOffset timestamp)
    {
        bool present = IsPresent(raw);
        if (!present) return BatterySnapshot.NotPresent with { Timestamp = timestamp };

        BatteryUnitList units = new((raw.Units ?? Array.Empty<BatteryUnitRaw>()).Select((u, i) => MapUnit(u, i)));
        BatteryTotals totals = BatteryMath.Aggregate(units);

        int? design = Positive(raw.ReportDesignMwh) ?? ValidWmi(raw.WmiDesignMwh) ?? totals.DesignCapacityMwh;
        int? full = Positive(raw.ReportFullMwh) ?? ValidWmi(raw.WmiFullMwh) ?? totals.FullChargeCapacityMwh;
        int? remaining = PositiveOrZero(raw.ReportRemainingMwh) ?? ValidWmiOrZero(raw.WmiRemainingMwh) ?? totals.RemainingCapacityMwh;

        int? percent = raw.LifePercent is byte p && p <= 100 ? p : null;
        if (percent is null && remaining is int r && full is int f && f > 0)
            percent = Math.Clamp((int)Math.Round(r * 100.0 / f), 0, 100);

        bool isOnAc = raw.AcLineStatus switch
        {
            1 => true,
            0 => false,
            _ => raw.WmiPowerOnline ?? false,
        };

        bool isCharging =
            string.Equals(raw.ReportStatus, "Charging", StringComparison.OrdinalIgnoreCase) ||
            (raw.BatteryFlag is byte flag && flag != UnknownByte && (flag & ChargingFlag) != 0) ||
            raw.WmiCharging == true;

        int? chargeRate = raw.ReportChargeRateMw is > 0 ? raw.ReportChargeRateMw : null;
        int? dischargeRate = raw.ReportChargeRateMw is < 0 ? -raw.ReportChargeRateMw : null;
        if (raw.ReportChargeRateMw == 0) { chargeRate = 0; dischargeRate = 0; } // reported idle, not unknown
        chargeRate ??= isCharging ? ValidWmi(raw.WmiChargeRateMw) ?? totals.ChargeRateMw : null;
        dischargeRate ??= !isCharging ? ValidWmi(raw.WmiDischargeRateMw) ?? totals.DischargeRateMw : null;

        double? health = BatteryMath.HealthPercent(design, full);

        TimeSpan? timeRemaining = null;
        if (!isOnAc)
        {
            if (raw.LifeTimeSeconds is int seconds && seconds > 0) timeRemaining = TimeSpan.FromSeconds(seconds);
            else timeRemaining = BatteryMath.TimeToEmpty(remaining, dischargeRate);
        }

        TimeSpan? timeToFull = isCharging ? BatteryMath.TimeToFull(remaining, full, chargeRate) : null;

        return new BatterySnapshot
        {
            Timestamp = timestamp,
            Present = true,
            Percent = percent,
            IsOnAc = isOnAc,
            IsCharging = isCharging,
            ChargeRateMw = chargeRate,
            DischargeRateMw = dischargeRate,
            VoltageMv = ValidWmi(raw.WmiVoltageMv) ?? totals.VoltageMv,
            RemainingCapacityMwh = remaining,
            DesignCapacityMwh = design,
            FullChargeCapacityMwh = full,
            HealthPercent = health,
            // Many firmwares report 0 when they do not track cycles; show "unknown" rather than 0.
            CycleCount = ValidWmi(raw.WmiCycleCount) ?? totals.CycleCount,
            TemperatureC = MapTemperature(raw.WmiTemperatureDeciKelvin) ?? totals.TemperatureC,
            TimeRemaining = timeRemaining,
            TimeToFull = timeToFull,
            Manufacturer = (string.IsNullOrWhiteSpace(raw.Manufacturer) ? null : raw.Manufacturer.Trim())
                           ?? units.Select(u => u.Manufacturer).FirstOrDefault(v => v is not null),
            Chemistry = DecodeChemistry(raw.ChemistryCode) ?? units.Select(u => u.Chemistry).FirstOrDefault(v => v is not null),
            DeviceName = units.Select(u => u.DeviceName).FirstOrDefault(v => v is not null),
            SerialNumber = units.Select(u => u.SerialNumber).FirstOrDefault(v => v is not null),
            ManufactureDate = units.Select(u => u.ManufactureDate).FirstOrDefault(v => v is not null),
            Batteries = units,
        };
    }

    private static BatteryUnit MapUnit(BatteryUnitRaw u, int index) => new()
    {
        Name = string.IsNullOrWhiteSpace(u.InstanceName) ? $"Battery {index + 1}" : u.InstanceName,
        Manufacturer = Clean(u.Manufacturer),
        DeviceName = Clean(u.DeviceName),
        SerialNumber = Clean(u.SerialNumber),
        ManufactureDate = u.ManufactureDate,
        Chemistry = DecodeChemistry(u.ChemistryCode),
        DesignCapacityMwh = ValidWmi(u.DesignMwh),
        FullChargeCapacityMwh = ValidWmi(u.FullMwh),
        RemainingCapacityMwh = ValidWmiOrZero(u.RemainingMwh),
        VoltageMv = ValidWmi(u.VoltageMv),
        CycleCount = ValidWmi(u.CycleCount),
        TemperatureC = MapTemperature(u.TemperatureDeciKelvin),
        ChargeRateMw = ValidWmiOrZero(u.ChargeRateMw),
        DischargeRateMw = ValidWmiOrZero(u.DischargeRateMw),
    };

    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string t = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return t.Length == 0 ? null : t;
    }

    public static string? DecodeChemistry(uint? code)
    {
        if (code is not uint value || value == 0) return null;
        Span<char> chars = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            byte b = (byte)(value >> (8 * i));
            if (b == 0) { chars[i] = ' '; continue; }
            if (b < 0x20 || b > 0x7E) return null;
            chars[i] = (char)b;
        }
        string text = new string(chars).Trim();
        return text.Length == 0 ? null : text;
    }

    private static bool IsPresent(BatteryRawReadings raw)
    {
        if (raw.BatteryFlag == NoSystemBattery) return false;
        if (raw.ReportStatus is not null)
            return !string.Equals(raw.ReportStatus, "NotPresent", StringComparison.OrdinalIgnoreCase);
        if (raw.BatteryFlag is byte flag && flag != UnknownByte) return (flag & NoSystemBattery) == 0;
        return ValidWmi(raw.WmiFullMwh) is not null || ValidWmi(raw.WmiDesignMwh) is not null;
    }

    private static double? MapTemperature(long? deciKelvin)
    {
        if (deciKelvin is not long value || value <= 0 || value >= WmiUnknownRate) return null;
        double celsius = Math.Round(value / 10.0 - 273.15, 1);
        return celsius is > -30 and < 110 ? celsius : null;
    }

    private static int? Positive(int? value) => value is > 0 ? value : null;
    private static int? PositiveOrZero(int? value) => value is >= 0 ? value : null;

    private static int? ValidWmi(long? value) =>
        value is long v && v > 0 && v != WmiUnknown && v != WmiUnknownRate && v <= int.MaxValue ? (int)v : null;

    private static int? ValidWmiOrZero(long? value) =>
        value == 0 ? 0 : ValidWmi(value);
}
