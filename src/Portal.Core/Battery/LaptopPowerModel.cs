namespace Portal.Core.Battery;

public enum LaptopPowerState
{
    NoBattery,
    Charging,
    /// <summary>On mains, battery not taking charge (holding at the limit or full).</summary>
    PluggedIn,
    OnBattery,
}

/// <summary>What the Power flow page shows for a laptop: the flow numbers plus which of them the laptop does not report.</summary>
public sealed record LaptopPowerModel(
    LaptopPowerState State,
    PowerFlowSnapshot Flow,
    bool SystemNotReported,
    bool BatteryNotReported,
    int? Percent,
    TimeSpan? TimeRemaining);

public static class LaptopPowerModelBuilder
{
    /// <summary>
    /// Laptop only: mains in, battery charge or discharge, system draw. A value the laptop does not report stays
    /// unavailable (never estimated), and the flags say so for the page text.
    /// </summary>
    public static LaptopPowerModel Build(BatterySnapshot battery, EnergyMeterSample? meter = null)
    {
        PowerFlowSnapshot flow = PowerFlowCalculator.Compute(battery, adapterRatedW: null, meter, cpuPercent: null);
        if (!battery.Present)
            return new LaptopPowerModel(LaptopPowerState.NoBattery, flow, true, true, null, null);

        LaptopPowerState state = !battery.IsOnAc ? LaptopPowerState.OnBattery
            : battery.IsCharging ? LaptopPowerState.Charging
            : LaptopPowerState.PluggedIn;
        return new LaptopPowerModel(state, flow, !flow.SystemW.HasValue, !flow.BatteryW.HasValue,
            battery.Percent, battery.IsOnAc ? null : battery.TimeRemaining);
    }
}
