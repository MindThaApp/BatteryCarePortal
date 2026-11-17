namespace Portal.Core.Battery;

/// <summary>What heat protection can do on this laptop; decides the "on this laptop" text.</summary>
public enum HeatBehaviour
{
    /// <summary>The laptop reports no battery temperature: the switch is not offered.</summary>
    NoReading,
    /// <summary>The charge limit backend can pause charging.</summary>
    PausesCharging,
    /// <summary>On/off backends: ends a top-up early and puts the limit back; cannot pause.</summary>
    EndsTopUp,
    /// <summary>No usable charge limit backend: reading and reminder only.</summary>
    MonitorOnly,
}

public enum HeatZone { Unknown, Normal, Hot }

/// <summary>Pure view model for the Heat protection page. Reads only; it never changes a charge limit.</summary>
public sealed record HeatProtectionModel(
    HeatBehaviour Behaviour,
    double? TemperatureC,
    double ThresholdC,
    double ResumeC,
    HeatZone Zone)
{
    public const double ScaleMinC = 20;
    public const double ScaleMaxC = 60;
    public const double MinThresholdC = 30;
    public const double MaxThresholdC = 60;

    /// <summary>The switch is offered only when the laptop reports a temperature.</summary>
    public bool SwitchOffered => Behaviour != HeatBehaviour.NoReading;

    public double? TemperatureFraction => TemperatureC is double t ? ScaleFraction(t) : null;
    public double ThresholdFraction => ScaleFraction(ThresholdC);

    public static double ScaleFraction(double celsius) =>
        Math.Clamp((celsius - ScaleMinC) / (ScaleMaxC - ScaleMinC), 0, 1);

    /// <summary>Moves the threshold by whole degrees inside the allowed 30 to 60 range.</summary>
    public static double StepThreshold(double current, int stepC) =>
        Math.Clamp(Math.Round(current) + stepC, MinThresholdC, MaxThresholdC);

    /// <param name="capabilities">Null when no backend can write on this laptop.</param>
    /// <param name="hot">Whether the battery is in the hot band now (above the threshold, or still above the resume level).</param>
    public static HeatProtectionModel Build(BatterySnapshot battery, HeatProtectionSettings heat, ChargeLimitCapabilities? capabilities)
    {
        double threshold = Math.Clamp(heat.MaxTempC, MinThresholdC, MaxThresholdC);
        double resume = threshold - ChargeCareEngine.HeatHysteresisC;
        double? t = battery.Present ? battery.TemperatureC : null;

        HeatBehaviour behaviour = t is null ? HeatBehaviour.NoReading
            : capabilities is null || (!capabilities.SupportsLimit && !capabilities.CanPauseCharging) ? HeatBehaviour.MonitorOnly
            : capabilities.CanPauseCharging ? HeatBehaviour.PausesCharging
            : HeatBehaviour.EndsTopUp;

        HeatZone zone = t is not double now ? HeatZone.Unknown : now >= threshold ? HeatZone.Hot : HeatZone.Normal;
        return new HeatProtectionModel(behaviour, t, threshold, resume, zone);
    }
}
