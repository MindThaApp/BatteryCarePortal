namespace Portal.Core.Services;

/// <summary>One SKU of the running app as the Store reports it. The typed collection fields are null when the Store gave none.</summary>
public sealed record StoreSkuInfo(
    string? ExtendedJsonData,
    bool IsInUserCollection = false,
    bool IsTrialSku = false,
    DateTimeOffset? CollectionAcquiredUtc = null,
    bool? CollectionIsTrial = null);

/// <summary>The Store queries the Pro decision needs. Implementations throw when the Store can't answer.</summary>
public interface IStoreLicenseProvider
{
    /// <summary>The SKUs of the running app's own Store product.</summary>
    Task<IReadOnlyList<StoreSkuInfo>> GetCurrentAppSkusAsync();

    /// <summary>True when the user owns the durable add-on with this Store ID.</summary>
    Task<bool> IsDurableAddOnOwnedAsync(string addOnStoreId);
}
