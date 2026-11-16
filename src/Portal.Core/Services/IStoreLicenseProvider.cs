namespace Portal.Core.Services;

/// <summary>The Store query the Pro decision needs. Implementations throw when the Store can't answer.</summary>
public interface IStoreLicenseProvider
{
    /// <summary>True when the user owns the durable add-on with this Store ID.</summary>
    Task<bool> IsDurableAddOnOwnedAsync(string addOnStoreId);
}
