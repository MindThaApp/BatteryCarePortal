using Portal.Core.Battery;
using Xunit;

namespace Portal.Core.Tests;

public class BatteryMathTests
{
    [Theory]
    [InlineData(57000, 51300, 90.0, 10.0)]
    [InlineData(50000, 50000, 100.0, 0.0)]
    [InlineData(40000, 42000, 105.0, 0.0)]
    public void Health_and_wear_are_complementary(int design, int full, double health, double wear)
    {
        Assert.Equal(health, BatteryMath.HealthPercent(design, full));
        Assert.Equal(wear, BatteryMath.WearPercent(design, full));
    }

    [Theory]
    [InlineData(null, 5000)]
    [InlineData(5000, null)]
    [InlineData(0, 5000)]
    [InlineData(5000, 0)]
    public void Health_and_wear_are_null_when_an_input_is_missing(int? design, int? full)
    {
        Assert.Null(BatteryMath.HealthPercent(design, full));
        Assert.Null(BatteryMath.WearPercent(design, full));
    }

    [Fact]
    public void Power_is_signed_charge_positive_discharge_negative()
    {
        Assert.Equal(15000, BatteryMath.SignedPowerMw(15000, null));
        Assert.Equal(-9000, BatteryMath.SignedPowerMw(null, 9000));
        Assert.Equal(0, BatteryMath.SignedPowerMw(0, 0));
        Assert.Null(BatteryMath.SignedPowerMw(null, null));
        Assert.Equal(15.0, BatteryMath.PowerWatts(15000));
        Assert.Null(BatteryMath.PowerWatts(null));
    }

    [Fact]
    public void Current_comes_from_power_and_voltage()
    {
        Assert.Equal(1250, BatteryMath.CurrentMa(15000, 12000));
        Assert.Equal(-750, BatteryMath.CurrentMa(-9000, 12000));
        Assert.Null(BatteryMath.CurrentMa(15000, null));
        Assert.Null(BatteryMath.CurrentMa(15000, 0));
        Assert.Null(BatteryMath.CurrentMa(null, 12000));
    }

    [Fact]
    public void Unit_conversions()
    {
        Assert.Equal(12.4, BatteryMath.MvToVolts(12400));
        Assert.Equal(51.87, BatteryMath.MwhToWh(51870));
        Assert.Null(BatteryMath.MvToVolts(null));
        Assert.Null(BatteryMath.MwhToWh(null));
    }

    [Fact]
    public void Time_estimates()
    {
        Assert.Equal(TimeSpan.FromHours(2), BatteryMath.TimeToEmpty(18000, 9000));
        Assert.Null(BatteryMath.TimeToEmpty(18000, 0));
        Assert.Null(BatteryMath.TimeToEmpty(null, 9000));
        Assert.Equal(TimeSpan.FromHours(1), BatteryMath.TimeToFull(30000, 45000, 15000));
        Assert.Null(BatteryMath.TimeToFull(45000, 45000, 15000));
        Assert.Null(BatteryMath.TimeToFull(30000, 45000, null));
        Assert.Null(BatteryMath.TimeToFull(30000, 45000, 0));
    }

    [Theory]
    [InlineData("ABC123456789", "••••6789")]
    [InlineData("AB12", "AB12")]
    [InlineData("12345", "•" + "2345")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void Serial_is_masked(string? input, string? expected) => Assert.Equal(expected, BatteryMath.MaskSerial(input));

    [Fact]
    public void Manufacture_dates_parse_or_stay_null()
    {
        Assert.Equal(new DateOnly(2021, 3, 15), BatteryMath.ParseManufactureDate("20210315"));
        Assert.Equal(new DateOnly(2021, 3, 15), BatteryMath.ParseManufactureDate("20210315000000.000000+000"));
        Assert.Equal(new DateOnly(2021, 3, 15), BatteryMath.ParseManufactureDate("2021-03-15"));
        Assert.Null(BatteryMath.ParseManufactureDate("00000000"));
        Assert.Null(BatteryMath.ParseManufactureDate("garbage"));
        Assert.Null(BatteryMath.ParseManufactureDate(null));
        Assert.Equal(new DateOnly(2023, 9, 4), BatteryMath.FromDayMonthYear(4, 9, 2023));
        Assert.Null(BatteryMath.FromDayMonthYear(31, 2, 2023));
        Assert.Null(BatteryMath.FromDayMonthYear(0, 0, 0));
    }
}

public class BatteryAggregationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Totals_sum_capacities_and_pick_hottest_and_highest_cycles()
    {
        var totals = BatteryMath.Aggregate(new[]
        {
            new BatteryUnit { DesignCapacityMwh = 30000, FullChargeCapacityMwh = 27000, RemainingCapacityMwh = 13500, VoltageMv = 11000, CycleCount = 100, TemperatureC = 30, DischargeRateMw = 4000 },
            new BatteryUnit { DesignCapacityMwh = 20000, FullChargeCapacityMwh = 19000, RemainingCapacityMwh = 9500, VoltageMv = 12000, CycleCount = 150, TemperatureC = 34, DischargeRateMw = 2000 },
        });
        Assert.Equal(50000, totals.DesignCapacityMwh);
        Assert.Equal(46000, totals.FullChargeCapacityMwh);
        Assert.Equal(23000, totals.RemainingCapacityMwh);
        Assert.Equal(11500, totals.VoltageMv);
        Assert.Equal(150, totals.CycleCount);
        Assert.Equal(34, totals.TemperatureC);
        Assert.Equal(6000, totals.DischargeRateMw);
        Assert.Null(totals.ChargeRateMw);
    }

    [Fact]
    public void A_capacity_total_is_null_when_one_battery_does_not_report_it()
    {
        var totals = BatteryMath.Aggregate(new[]
        {
            new BatteryUnit { DesignCapacityMwh = 30000, FullChargeCapacityMwh = 27000 },
            new BatteryUnit { DesignCapacityMwh = 20000, FullChargeCapacityMwh = null },
        });
        Assert.Equal(50000, totals.DesignCapacityMwh);
        Assert.Null(totals.FullChargeCapacityMwh);
        Assert.Null(totals.VoltageMv);
        Assert.Null(totals.TemperatureC);
        Assert.Null(totals.CycleCount);
    }

    [Fact]
    public void No_batteries_gives_empty_totals()
    {
        var totals = BatteryMath.Aggregate(Array.Empty<BatteryUnit>());
        Assert.Null(totals.DesignCapacityMwh);
        Assert.Null(totals.CycleCount);
    }

    [Fact]
    public void Mapper_uses_unit_totals_when_the_aggregate_sources_are_silent()
    {
        BatterySnapshot s = BatterySnapshotMapper.Map(new BatteryRawReadings
        {
            AcLineStatus = 0, BatteryFlag = 0, LifePercent = 50,
            Units = new[]
            {
                new BatteryUnitRaw { InstanceName = "A", DesignMwh = 30000, FullMwh = 27000, RemainingMwh = 13500, VoltageMv = 11000, DischargeRateMw = 4000, CycleCount = 120, TemperatureDeciKelvin = 3032, SerialNumber = "ABCDEF123456" },
                new BatteryUnitRaw { InstanceName = "B", DesignMwh = 20000, FullMwh = 19000, RemainingMwh = 9500, VoltageMv = 12000, DischargeRateMw = 2000, CycleCount = 80, DeviceName = "PACK-B" },
            },
        }, Now);

        Assert.Equal(50000, s.DesignCapacityMwh);
        Assert.Equal(46000, s.FullChargeCapacityMwh);
        Assert.Equal(23000, s.RemainingCapacityMwh);
        Assert.Equal(92.0, s.HealthPercent);
        Assert.Equal(8.0, s.WearPercent);
        Assert.Equal(6000, s.DischargeRateMw);
        Assert.Equal(-6000, s.PowerMw);
        Assert.Equal(-6.0, s.PowerW);
        Assert.Equal(TimeSpan.FromHours(23000 / 6000.0), s.TimeRemaining);
        Assert.Equal(120, s.CycleCount);
        Assert.InRange(s.TemperatureC!.Value, 30.0, 30.1);
        Assert.Equal(2, s.Batteries.Count);
        Assert.Equal("ABCDEF123456", s.SerialNumber);
        Assert.Equal("PACK-B", s.DeviceName);
        Assert.Equal(90.0, s.Batteries[0].HealthPercent);
        Assert.Equal(95.0, s.Batteries[1].HealthPercent);
    }

    [Fact]
    public void Mapper_keeps_unreported_values_null_and_does_not_invent_them()
    {
        BatterySnapshot s = BatterySnapshotMapper.Map(new BatteryRawReadings
        {
            AcLineStatus = 1, BatteryFlag = 0, LifePercent = 80,
            Units = new[]
            {
                new BatteryUnitRaw { InstanceName = "A", DesignMwh = 0xFFFFFFFF, VoltageMv = 0xFFFFFFFF, CycleCount = 0, TemperatureDeciKelvin = 0, ChargeRateMw = 0x80000000 },
            },
        }, Now);

        Assert.True(s.Present);
        Assert.Null(s.DesignCapacityMwh);
        Assert.Null(s.VoltageMv);
        Assert.Null(s.CycleCount);
        Assert.Null(s.TemperatureC);
        Assert.Null(s.WearPercent);
        Assert.Null(s.PowerMw);
        Assert.Null(s.CurrentMa);
        Assert.Null(s.VoltageV);
        Assert.Null(s.SerialNumber);
        Assert.Null(s.ManufactureDate);
        Assert.Single(s.Batteries);
    }

    [Fact]
    public void Snapshot_equality_ignores_list_identity()
    {
        BatteryUnit unit = new() { Name = "A", DesignCapacityMwh = 1000 };
        var a = new BatteryUnitList(new[] { unit });
        var b = new BatteryUnitList(new[] { unit with { } });
        Assert.Equal(a, b);
        Assert.NotEqual(a, new BatteryUnitList(new[] { unit with { DesignCapacityMwh = 2000 } }));
    }

    [Fact]
    public void Current_is_derived_from_power_and_voltage()
    {
        BatterySnapshot s = new() { Present = true, ChargeRateMw = 15000, VoltageMv = 12000 };
        Assert.Equal(1250, s.CurrentMa);
        Assert.Equal(15.0, s.PowerW);
        Assert.Equal(12.0, s.VoltageV);
    }
}

public class BatteryUnitMergerTests
{
    private static IReadOnlyDictionary<string, object?> Row(params (string Key, object? Value)[] values) =>
        new Dictionary<string, object?>(values.ToDictionary(v => v.Key, v => v.Value), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Static_and_dynamic_rows_are_joined_by_instance_name()
    {
        var units = BatteryUnitMerger.BuildStaticUnits(
            new[]
            {
                Row(("InstanceName", "A"), ("ManufactureName", "SMP"), ("DeviceName", "L19"), ("SerialNumber", "123"), ("DesignedCapacity", 30000u), ("ManufactureDate", "20220102")),
                Row(("InstanceName", "B"), ("DesignedCapacity", 20000u)),
            },
            new[] { Row(("InstanceName", "B"), ("CycleCount", 7u)), Row(("InstanceName", "A"), ("CycleCount", 9u)) },
            new[] { Row(("InstanceName", "A"), ("FullChargedCapacity", 28000u)), Row(("InstanceName", "B"), ("FullChargedCapacity", 18000u)) });
        var live = BatteryUnitMerger.AddDynamic(units,
            new[] { Row(("InstanceName", "B"), ("RemainingCapacity", 9000u), ("Voltage", 12000u)), Row(("InstanceName", "A"), ("RemainingCapacity", 14000u), ("Voltage", 11000u)) },
            new[] { Row(("InstanceName", "A"), ("Temperature", 3032u)) });

        Assert.Equal(2, live.Count);
        Assert.Equal(9, live[0].CycleCount);
        Assert.Equal(28000, live[0].FullMwh);
        Assert.Equal(14000, live[0].RemainingMwh);
        Assert.Equal(new DateOnly(2022, 1, 2), live[0].ManufactureDate);
        Assert.Equal(3032, live[0].TemperatureDeciKelvin);
        Assert.Equal(7, live[1].CycleCount);
        Assert.Equal(9000, live[1].RemainingMwh);
        Assert.Null(live[1].TemperatureDeciKelvin);
    }

    [Fact]
    public void Device_values_fill_gaps_only_when_counts_match()
    {
        var wmi = new[] { new BatteryUnitRaw { InstanceName = "A", DesignMwh = 30000 } };
        var device = new[] { new BatteryUnitRaw { InstanceName = "Battery 1", DesignMwh = 29000, TemperatureDeciKelvin = 3032, SerialNumber = "XYZ" } };

        var merged = BatteryUnitMerger.Merge(wmi, device)!;
        Assert.Equal(30000, merged[0].DesignMwh);
        Assert.Equal(3032, merged[0].TemperatureDeciKelvin);
        Assert.Equal("XYZ", merged[0].SerialNumber);

        var two = new[] { device[0], device[0] };
        Assert.Same(wmi, BatteryUnitMerger.Merge(wmi, two));
        Assert.Same(device, BatteryUnitMerger.Merge(Array.Empty<BatteryUnitRaw>(), device));
        Assert.Null(BatteryUnitMerger.Merge(Array.Empty<BatteryUnitRaw>(), Array.Empty<BatteryUnitRaw>()));
    }
}
