using Windows.Services.Store;

namespace Portal.Core.Services;

/// <summary><see cref="IStoreLicenseProvider"/> over the Windows Store APIs.</summary>
public sealed class StoreLicenseProvider(StoreContext storeContext) : IStoreLicenseProvider
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(8);

    public async Task<bool> IsDurableAddOnOwnedAsync(string addOnStoreId)
    {
        // AddOnLicenses is keyed by the SKU Store ID ("<product>/<sku>") on some systems and by the product ID on others.
        StoreAppLicense license = await storeContext.GetAppLicenseAsync().AsTask().WaitAsync(QueryTimeout);
        foreach ((string key, StoreLicense addOn) in license.AddOnLicenses)
        {
            if (addOn.IsActive && (MatchesAddOn(key, addOnStoreId) || MatchesAddOn(addOn.SkuStoreId, addOnStoreId))) return true;
        }

        StoreProductQueryResult query = await storeContext.GetAssociatedStoreProductsAsync(["Durable"]).AsTask().WaitAsync(QueryTimeout);
        if (query.Products.TryGetValue(addOnStoreId, out StoreProduct? product)) return product.IsInUserCollection;
        if (query.ExtendedError is not null)
            throw new InvalidOperationException($"Store add-on query failed (0x{query.ExtendedError.HResult:X8}).", query.ExtendedError);
        return false;
    }

    private static bool MatchesAddOn(string? id, string addOnStoreId) =>
        id is not null && (id.Equals(addOnStoreId, StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith(addOnStoreId + "/", StringComparison.OrdinalIgnoreCase));
}

