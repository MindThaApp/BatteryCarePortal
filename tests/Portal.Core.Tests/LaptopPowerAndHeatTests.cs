using Portal.Core.Battery;
using Xunit;

namespace Portal.Core.Tests;

public class LaptopPowerAndHeatTests
{
    [Fact]
    public void Charging_on_mains_reports_battery_watts_and_marks_system_not_reported()
    {
        LaptopPowerModel m = LaptopPowerModelBuilder.Build(Snap.At(45, onAc: true, charging: true));
        Assert.Equal(LaptopPowerState.Charging, m.State);
        Assert.Equal(25.0, m.Flow.BatteryW.Watts);
        Assert.True(m.SystemNotReported);
        Assert.False(m.BatteryNotReported);
        Assert.Equal(PowerDataQuality.Unavailable, m.Flow.SystemW.Quality);
    }

    [Fact]
    public void Charging_with_an_energy_meter_has_a_measured_system_draw()
    {
        var meter = new EnergyMeterSample(Array.Empty<EmiChannelPower>(), 14.5);
        LaptopPowerModel m = LaptopPowerModelBuilder.Build(Snap.At(45, onAc: true, charging: true), meter);
        Assert.False(m.SystemNotReported);
        Assert.Equal(14.5, m.Flow.SystemW.Watts);
    }

    [Fact]
    public void On_battery_uses_the_discharge_rate_as_system_draw()
    {
        BatterySnapshot s = Snap.At(71, onAc: false) with { DischargeRateMw = 9600, TimeRemaining = TimeSpan.FromHours(6) };
        LaptopPowerModel m = LaptopPowerModelBuilder.Build(s);
        Assert.Equal(LaptopPowerState.OnBattery, m.State);
        Assert.Equal(9.6, m.Flow.SystemW.Watts);
        Assert.Equal(-9.6, m.Flow.BatteryW.Watts);
        Assert.False(m.SystemNotReported);
        Assert.Equal(TimeSpan.FromHours(6), m.TimeRemaining);
    }

    [Fact]
    public void Laptop_without_any_rate_reports_nothing_instead_of_inventing_numbers()
    {
        LaptopPowerModel m = LaptopPowerModelBuilder.Build(Snap.At(50, onAc: false));
        Assert.True(m.SystemNotReported);
        Assert.True(m.BatteryNotReported);
        Assert.Null(m.Flow.SystemW.Watts);
        Assert.Null(m.Flow.BatteryW.Watts);
    }

    [Fact]
    public void Plugged_in_and_holding_is_its_own_state()
    {
        LaptopPowerModel m = LaptopPowerModelBuilder.Build(Snap.At(80, onAc: true));
        Assert.Equal(LaptopPowerState.PluggedIn, m.State);
        Assert.Null(m.TimeRemaining);
    }

    [Fact]
    public void No_battery_is_a_model_without_numbers()
    {
        LaptopPowerModel m = LaptopPowerModelBuilder.Build(BatterySnapshot.NotPresent);
        Assert.Equal(LaptopPowerState.NoBattery, m.State);
        Assert.Null(m.Flow.AdapterConnected);
    }

    private static readonly ChargeLimitCapabilities OnOff = new() { Kind = ChargeLimitKind.OnOffConservation, ConservationPercent = 60 };
    private static readonly ChargeLimitCapabilities Pausing = new() { Kind = ChargeLimitKind.ContinuousRange, MinLimit = 50, MaxLimit = 100, CanPauseCharging = true };

    [Fact]
    public void No_temperature_reading_means_no_switch()
    {
        var m = HeatProtectionModel.Build(Snap.At(50, true), new HeatProtectionSettings(), Pausing);
        Assert.Equal(HeatBehaviour.NoReading, m.Behaviour);
        Assert.False(m.SwitchOffered);
        Assert.Equal(HeatZone.Unknown, m.Zone);
        Assert.Null(m.TemperatureFraction);
    }

    [Fact]
    public void Behaviour_follows_what_the_backend_can_do()
    {
        BatterySnapshot warm = Snap.At(50, true, tempC: 34);
        var heat = new HeatProtectionSettings();
        Assert.Equal(HeatBehaviour.PausesCharging, HeatProtectionModel.Build(warm, heat, Pausing).Behaviour);
        Assert.Equal(HeatBehaviour.EndsTopUp, HeatProtectionModel.Build(warm, heat, OnOff).Behaviour);
        Assert.Equal(HeatBehaviour.MonitorOnly, HeatProtectionModel.Build(warm, heat, null).Behaviour);
        Assert.Equal(HeatBehaviour.MonitorOnly, HeatProtectionModel.Build(warm, heat, ChargeLimitCapabilities.Unsupported).Behaviour);
    }

    [Fact]
    public void Scale_runs_from_20_to_60_and_shows_the_resume_gap()
    {
        var m = HeatProtectionModel.Build(Snap.At(50, true, tempC: 34), new HeatProtectionSettings { MaxTempC = 40 }, OnOff);
        Assert.Equal(0.35, m.TemperatureFraction!.Value, 3);
        Assert.Equal(0.5, m.ThresholdFraction, 3);
        Assert.Equal(37, m.ResumeC);
        Assert.Equal(HeatZone.Normal, m.Zone);
        Assert.Equal(0, HeatProtectionModel.ScaleFraction(10));
        Assert.Equal(1, HeatProtectionModel.ScaleFraction(75));
    }

    [Fact]
    public void Hot_zone_starts_at_the_threshold()
    {
        var heat = new HeatProtectionSettings { MaxTempC = 40 };
        Assert.Equal(HeatZone.Hot, HeatProtectionModel.Build(Snap.At(50, true, tempC: 40), heat, OnOff).Zone);
        Assert.Equal(HeatZone.Normal, HeatProtectionModel.Build(Snap.At(50, true, tempC: 39.9), heat, OnOff).Zone);
    }

    [Fact]
    public void Threshold_steps_stay_inside_30_to_60()
    {
        Assert.Equal(41, HeatProtectionModel.StepThreshold(40, 1));
        Assert.Equal(30, HeatProtectionModel.StepThreshold(30, -1));
        Assert.Equal(60, HeatProtectionModel.StepThreshold(60, 1));
    }
}
