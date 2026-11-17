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

    /// <summary>While true every feature is free and available; the map below is the intended future split.</summary>
    public static bool EverythingFree = true;

    /// <summary>The tier the map assigns, ignoring <see cref="EverythingFree"/>.</summary>
    public static FeatureTier TierOf(PortalFeature feature) => Map[feature];

    /// <summary>History window in days: the intended free and Pro values; everything is free (and Pro-length) for now.</summary>
    public const int FreeHistoryDays = 7;
    public const int ProHistoryDays = 30;

    public static int HistoryRetentionDays(bool hasPro) => EverythingFree || hasPro ? ProHistoryDays : FreeHistoryDays;

    public static bool IsAvailable(PortalFeature feature, bool hasPro) => EverythingFree || hasPro || TierOf(feature) == FeatureTier.Free;
}
