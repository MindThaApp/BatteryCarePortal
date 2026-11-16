namespace Portal.Core.Services;

public enum FeatureTier
{
    Free,
    Pro,
}

public enum PortalFeature
{
    Monitoring,
    BasicChargeLimit,
    HeatProtection,
    PowerFlow,
    History,
    ScheduledCare,
}

/// <summary>
/// Which features are free and which need Pro. The split is not confirmed yet; this map is the only place to change it.
/// </summary>
public static class FeatureTiers
{
    private static readonly IReadOnlyDictionary<PortalFeature, FeatureTier> Map = new Dictionary<PortalFeature, FeatureTier>
    {
        [PortalFeature.Monitoring] = FeatureTier.Free,
        [PortalFeature.BasicChargeLimit] = FeatureTier.Free,
        [PortalFeature.HeatProtection] = FeatureTier.Pro,
        [PortalFeature.PowerFlow] = FeatureTier.Pro,
        [PortalFeature.History] = FeatureTier.Pro,
        [PortalFeature.ScheduledCare] = FeatureTier.Pro,
    };

    public static FeatureTier TierOf(PortalFeature feature) => Map[feature];

    public static bool IsAvailable(PortalFeature feature, bool hasPro) => hasPro || TierOf(feature) == FeatureTier.Free;
}
