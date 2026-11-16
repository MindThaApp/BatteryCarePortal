namespace Portal.Core.Power;

/// <summary>Power the PSU reports about itself (some digital PSUs expose this through the sensor helper).</summary>
public sealed record PsuMeasurement(double? InputW, double? OutputW, string? Name)
{
    public static PsuMeasurement None { get; } = new(null, null, null);
}
