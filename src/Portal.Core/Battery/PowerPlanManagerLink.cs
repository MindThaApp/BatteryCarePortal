namespace Portal.Core.Battery;

/// <summary>How to open Power Plan Manager, or its Store page when it is not installed. Pure; nothing here launches.</summary>
/// <param name="ProtocolUri">The protocol the installed edition declares, or null when none is known.</param>
/// <param name="AppsFolderTarget">The shell:AppsFolder target (package family and application id) used when the protocol fails.</param>
/// <param name="StoreUri">The Store page to open when it is not installed.</param>
public sealed record PowerPlanManagerLaunch(string? ProtocolUri, string? AppsFolderTarget, string StoreUri);

public static class PowerPlanManagerLink
{
    /// <summary>Public Store ID of Power Plan Manager (the simple, free edition).</summary>
    public const string StoreProductId = "9NXJG6SXVVF2";

    public const string ApplicationId = "PowerPlanTray";
    public const string LiteProtocol = "powerplanmanager-lite:";
    public const string FullProtocol = "powerplanmanager:";
    private const string LitePackageName = PowerPlanManagerProbe.PackageNamePrefix + "Lite";

    public static string StoreUri => "ms-windows-store://pdp/?ProductId=" + StoreProductId;

    /// <summary>Installed package family names (folder names under Packages); the Lite edition is preferred when both exist.</summary>
    public static PowerPlanManagerLaunch Resolve(IReadOnlyList<string> installedFamilies)
    {
        string? family = installedFamilies.FirstOrDefault(f => f.StartsWith(LitePackageName, StringComparison.OrdinalIgnoreCase))
            ?? installedFamilies.FirstOrDefault();
        if (family is null) return new PowerPlanManagerLaunch(null, null, StoreUri);
        bool lite = family.StartsWith(LitePackageName, StringComparison.OrdinalIgnoreCase);
        return new PowerPlanManagerLaunch(lite ? LiteProtocol : FullProtocol, family + "!" + ApplicationId, StoreUri);
    }
}
