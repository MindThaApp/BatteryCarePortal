using System.Globalization;
using Portal.Core.Battery;
using Xunit;

namespace Portal.Core.Tests;

public class BatteryFormatTests
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    [Fact]
    public void Missing_values_show_a_dash()
    {
        Assert.Equal("—", BatteryFormat.Capacity(null, Us));
        Assert.Equal("—", BatteryFormat.CapacityWh(null, Us));
        Assert.Equal("—", BatteryFormat.Volts(null, Us));
        Assert.Equal("—", BatteryFormat.Milliamps(null, Us));
        Assert.Equal("—", BatteryFormat.Watts(null, Us));
        Assert.Equal("—", BatteryFormat.Temperature(null, Us));
        Assert.Equal("—", BatteryFormat.Percent(null, Us));
        Assert.Equal("—", BatteryFormat.Count(null, Us));
        Assert.Equal("—", BatteryFormat.Date(null, Us));
        Assert.Equal("—", BatteryFormat.Text("  "));
    }

    [Fact]
    public void Values_are_formatted_with_units_and_signs()
    {
        Assert.Equal("57,000 mWh (57.0 Wh)", BatteryFormat.Capacity(57000, Us));
        Assert.Equal("51.9 Wh", BatteryFormat.CapacityWh(51870, Us));
        Assert.Equal("12.40 V", BatteryFormat.Volts(12400, Us));
        Assert.Equal("+1,250 mA", BatteryFormat.Milliamps(1250, Us));
        Assert.Equal("−750 mA", BatteryFormat.Milliamps(-750, Us));
        Assert.Equal("+15.0 W", BatteryFormat.Watts(15, Us));
        Assert.Equal("−6.0 W", BatteryFormat.Watts(-6, Us));
        Assert.Equal("0.0 W", BatteryFormat.Watts(0, Us));
        Assert.Equal("33.5 °C", BatteryFormat.Temperature(33.5, Us));
        Assert.Equal("92 %", BatteryFormat.Percent(92, Us));
    }

    [Fact]
    public void Fraction_is_clamped_and_null_when_unknown()
    {
        Assert.Equal(0.5, BatteryFormat.Fraction(500, 1000));
        Assert.Equal(1.0, BatteryFormat.Fraction(1500, 1000));
        Assert.Null(BatteryFormat.Fraction(500, null));
        Assert.Null(BatteryFormat.Fraction(500, 0));
    }

    [Fact]
    public void Simulated_battery_reports_details_and_wear()
    {
        BatterySnapshot s = new FakeBattery().SnapshotSource.Read();
        Assert.Equal(9.0, s.WearPercent);
        Assert.Single(s.Batteries);
        Assert.Equal("SIM00012345", s.SerialNumber);
        Assert.Equal("SIM00012345", s.Batteries[0].SerialNumber);
    }

    [Fact]
    public void Tray_battery_mode_defaults_to_simple_value_order()
    {
        Assert.Equal(0, (int)TrayBatteryMode.Off);
        Assert.Equal(1, (int)TrayBatteryMode.Simple);
        Assert.Equal(2, (int)TrayBatteryMode.Expanded);
    }
}
