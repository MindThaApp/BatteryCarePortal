namespace Portal.Core.Battery;

/// <summary>Builds and merges per-battery readings from the root\wmi rows and the battery device interface.</summary>
public static class BatteryUnitMerger
{
    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows(IReadOnlyList<IReadOnlyDictionary<string, object?>>? rows) =>
        rows ?? Array.Empty<IReadOnlyDictionary<string, object?>>();

    /// <summary>Static data (names, capacities, cycles) per battery, keyed on the WMI InstanceName.</summary>
    public static List<BatteryUnitRaw> BuildStaticUnits(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> detailRows,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> cycleRows,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> fullRows)
    {
        var units = new List<BatteryUnitRaw>();
        foreach (var row in Rows(detailRows))
        {
            string name = row.AsString("InstanceName") ?? "";
            units.Add(new BatteryUnitRaw
            {
                InstanceName = name,
                Manufacturer = row.AsString("ManufactureName"),
                DeviceName = row.AsString("DeviceName"),
                SerialNumber = row.AsString("SerialNumber"),
                ManufactureDate = BatteryMath.ParseManufactureDate(row.AsString("ManufactureDate")),
                ChemistryCode = ChemistryCodeOf(row),
                DesignMwh = row.AsLong("DesignedCapacity"),
                FullMwh = FindLong(fullRows, name, "FullChargedCapacity"),
                CycleCount = FindLong(cycleRows, name, "CycleCount") ?? row.AsLong("CycleCount"),
            });
        }
        return units;
    }

    /// <summary>Adds the live values (remaining capacity, voltage, rates, temperature) to the static units.</summary>
    public static IReadOnlyList<BatteryUnitRaw> AddDynamic(
        IReadOnlyList<BatteryUnitRaw> staticUnits,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> statusRows,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> temperatureRows)
    {
        var units = staticUnits.ToList();
        if (units.Count == 0)
        {
            // Static data unavailable: still list one entry per status row.
            foreach (var row in Rows(statusRows)) units.Add(new BatteryUnitRaw { InstanceName = row.AsString("InstanceName") ?? "" });
        }

        for (int i = 0; i < units.Count; i++)
        {
            BatteryUnitRaw unit = units[i];
            var status = Pick(statusRows, unit.InstanceName, i, units.Count);
            var temperature = Pick(temperatureRows, unit.InstanceName, i, units.Count);
            units[i] = unit with
            {
                RemainingMwh = status?.AsLong("RemainingCapacity"),
                VoltageMv = status?.AsLong("Voltage"),
                ChargeRateMw = status?.AsLong("ChargeRate"),
                DischargeRateMw = status?.AsLong("DischargeRate"),
                TemperatureDeciKelvin = temperature?.AsLong("Temperature"),
            };
        }
        return units;
    }

    /// <summary>
    /// WMI wins; the device interface fills the gaps. The two lists are only paired up when they hold the same
    /// number of batteries, because there is no reliable key that links them.
    /// </summary>
    public static IReadOnlyList<BatteryUnitRaw>? Merge(IReadOnlyList<BatteryUnitRaw> wmi, IReadOnlyList<BatteryUnitRaw> device)
    {
        if (wmi.Count == 0) return device.Count == 0 ? null : device;
        if (device.Count != wmi.Count) return wmi;
        var merged = new List<BatteryUnitRaw>(wmi.Count);
        for (int i = 0; i < wmi.Count; i++)
        {
            BatteryUnitRaw w = wmi[i], d = device[i];
            merged.Add(w with
            {
                Manufacturer = Blank(w.Manufacturer) ?? d.Manufacturer,
                DeviceName = Blank(w.DeviceName) ?? d.DeviceName,
                SerialNumber = Blank(w.SerialNumber) ?? d.SerialNumber,
                ManufactureDate = w.ManufactureDate ?? d.ManufactureDate,
                ChemistryCode = w.ChemistryCode ?? d.ChemistryCode,
                DesignMwh = Known(w.DesignMwh) ?? d.DesignMwh,
                FullMwh = Known(w.FullMwh) ?? d.FullMwh,
                RemainingMwh = w.RemainingMwh is > 0 ? w.RemainingMwh : d.RemainingMwh ?? w.RemainingMwh,
                VoltageMv = Known(w.VoltageMv) ?? d.VoltageMv,
                CycleCount = Known(w.CycleCount) ?? d.CycleCount,
                TemperatureDeciKelvin = Known(w.TemperatureDeciKelvin) ?? d.TemperatureDeciKelvin,
                ChargeRateMw = Known(w.ChargeRateMw) ?? d.ChargeRateMw ?? w.ChargeRateMw,
                DischargeRateMw = Known(w.DischargeRateMw) ?? d.DischargeRateMw ?? w.DischargeRateMw,
            });
        }
        return merged;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static long? Known(long? v) => v is > 0 and < 0x80000000 ? v : null;

    private static long? FindLong(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string instance, string property)
    {
        foreach (var row in Rows(rows))
            if (string.Equals(row.AsString("InstanceName"), instance, StringComparison.OrdinalIgnoreCase)) return row.AsLong(property);
        return Rows(rows).Count == 1 ? Rows(rows)[0].AsLong(property) : null;
    }

    private static IReadOnlyDictionary<string, object?>? Pick(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string instance, int index, int unitCount)
    {
        var list = Rows(rows);
        foreach (var row in list)
            if (!string.IsNullOrEmpty(instance) && string.Equals(row.AsString("InstanceName"), instance, StringComparison.OrdinalIgnoreCase)) return row;
        // No matching key: pair by position, but only when the counts agree.
        return list.Count == unitCount && index < list.Count ? list[index] : null;
    }

    private static uint? ChemistryCodeOf(IReadOnlyDictionary<string, object?> row)
    {
        if (!row.TryGetValue("Chemistry", out object? value) || value is null) return null;
        if (value is byte[] bytes && bytes.Length >= 4) return BitConverter.ToUInt32(bytes, 0);
        if (value is ushort[] words && words.Length >= 2) return (uint)(words[0] | (words[1] << 16));
        long? number = row.AsLong("Chemistry");
        return number is > 0 ? (uint)number.Value : null;
    }
}
