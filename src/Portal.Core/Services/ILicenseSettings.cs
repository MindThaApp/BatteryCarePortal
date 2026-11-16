namespace Portal.Core.Services;

/// <summary>The settings the licensing layer needs from the host app's settings store.</summary>
public interface ILicenseSettings
{
    bool LegacyProPurchaseConfirmed { get; set; }
    ProTrialTerms ProTrialTerms { get; }
    bool TryStartProTrial(DateTimeOffset nowUtc);
}
