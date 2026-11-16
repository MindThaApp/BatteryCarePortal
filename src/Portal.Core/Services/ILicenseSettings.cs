namespace Portal.Core.Services;

/// <summary>The settings the licensing layer needs from the host app's settings store.</summary>
public interface ILicenseSettings
{
    ProTrialTerms ProTrialTerms { get; }
    bool TryStartProTrial(DateTimeOffset nowUtc);
}

