using Windows.Services.Store;

namespace Portal.Core.Services;

public sealed class LicensingService
{
    public const string ProUnlockStoreId = ProEntitlementService.ProAddOnStoreId;
    public static readonly TimeSpan TrialDuration = ProEntitlementService.TrialDuration;
    private static readonly TimeSpan PriceQueryTimeout = TimeSpan.FromSeconds(4);

    private readonly ILicenseSettings _appSettingsService;
    private readonly StoreContext? _storeContext;
    private readonly ProEntitlementService _entitlement;
    private readonly Action<string, string>? _log;
    private readonly TaskCompletionSource _trialStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _recordingUse;
    private string? _formattedPrice;

    /// <summary>Raised once, on the thread that reported the use, when a first Pro use starts the trial.</summary>
    public event Action? TrialStarted;

    /// <param name="log">Receives licence-check failures as (context, reason) for the app's error log.</param>
    public LicensingService(ILicenseSettings appSettingsService, Action<string, string>? log = null)
    {
        _appSettingsService = appSettingsService;
        _log = log;
        try
        {
            _storeContext = StoreContext.GetDefault();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PortalBatteryCare: Store context unavailable: {ex}");
        }
        _entitlement = new ProEntitlementService(
            _storeContext is null ? null : new StoreLicenseProvider(_storeContext),
            () => EffectiveTrial,
            log);
    }

    /// <summary>True while the trial grants Pro: before its first use and while it runs.</summary>
    public bool IsTrialActive => _entitlement.IsTrialActive;

    /// <summary>False until a Pro feature is first used. Someone who owns Pro never starts it.</summary>
    public bool IsTrialStarted => _entitlement.IsTrialStarted;

    /// <summary>Time left in the trial; its full length while it has not started.</summary>
    public TimeSpan TrialRemaining => _entitlement.TrialRemaining;

    /// <summary>Length of this install's trial: 14 days.</summary>
    public TimeSpan TrialLength => EffectiveTrial.Duration;

    /// <summary>Whole days left in a running trial (rounded up), or null when no trial is running.</summary>
    public int? TrialDaysLeft => IsTrialStarted && IsTrialActive && !_ownsPermanentAccess ? Math.Max(1, (int)Math.Ceiling(TrialRemaining.TotalDays)) : null;

    /// <summary>True while the trial is still waiting for its first Pro use and Pro is not owned.</summary>
    public bool TrialWaitingForFirstUse => !IsTrialStarted && !_ownsPermanentAccess;

    /// <summary>Completes once the trial has started (at once when it already has).</summary>
    public Task WaitForTrialStartAsync() => IsTrialStarted ? Task.CompletedTask : _trialStarted.Task;

    /// <summary>
    /// A deliberate use of a Pro feature. The first one starts the trial (once; later calls do nothing), logs the feature and
    /// raises <see cref="TrialStarted"/>. Someone who owns Pro never starts a trial.
    /// </summary>
    public void NoteProFeatureUsed(string feature)
    {
        if (IsTrialStarted) return;
        if (Interlocked.Exchange(ref _recordingUse, 1) == 1) return;
        _ = RecordFirstUseAsync(feature);
    }

    private async Task RecordFirstUseAsync(string feature)
    {
        try
        {
            if (await RefreshOwnershipAsync().ConfigureAwait(false)) return;
            if (!_appSettingsService.TryStartProTrial(DateTimeOffset.UtcNow)) return;
            try { _log?.Invoke("Pro trial started", $"first use: {feature}"); } catch { }
            _trialStarted.TrySetResult();
            TrialStarted?.Invoke();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PortalBatteryCare: Pro trial start not recorded: {ex}");
        }
        finally
        {
            Volatile.Write(ref _recordingUse, 0);
        }
    }

    private volatile bool _ownsPermanentAccess;

    /// <summary>
    /// Asks the Store  whether Pro is owned for good and remembers the answer, so
    /// <see cref="TrialDaysLeft"/> stays empty for an owner. Call once the window or the app starts.
    /// </summary>
    public async Task<bool> RefreshOwnershipAsync(bool forceRefresh = false)
    {
        bool owns = await _entitlement.OwnsPermanentAccessAsync(forceRefresh).ConfigureAwait(false);
        _ownsPermanentAccess = owns;
        return owns;
    }

    private ProTrialTerms EffectiveTrial
    {
        get
        {
#if DEBUG
            try
            {
                string expiredMarker = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "pro-trial-expired.debug");
                if (File.Exists(expiredMarker)) return new ProTrialTerms(DateTimeOffset.UtcNow - TrialDuration - TimeSpan.FromMinutes(1), TrialDuration);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"PortalBatteryCare: trial test marker unavailable: {ex}");
            }
#endif
            return _appSettingsService.ProTrialTerms;
        }
    }

    /// <summary>The Pro decision for the Pro edition (see <see cref="ProEntitlementService"/>).</summary>
    public Task<bool> IsProUnlockedAsync(bool forceLicenseRefresh = false) =>
        _entitlement.HasProAccessAsync(forceLicenseRefresh);

    // The Store formats the price in the customer's market currency; null when the Store is unreachable.
    public async Task<string?> GetProUnlockFormattedPriceAsync()
    {
        if (_formattedPrice is not null || _storeContext is null) return _formattedPrice;
        try
        {
            Task<StoreProductQueryResult> query = _storeContext
                .GetStoreProductsAsync(new[] { "Durable" }, new[] { ProUnlockStoreId }).AsTask();
            if (await Task.WhenAny(query, Task.Delay(PriceQueryTimeout)) != query) return null;
            StoreProductQueryResult result = await query;
            if (result.Products.TryGetValue(ProUnlockStoreId, out StoreProduct? product))
            {
                string price = product.Price?.FormattedPrice ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(price)) _formattedPrice = price;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PortalBatteryCare: Store price unavailable: {ex}");
        }
        return _formattedPrice;
    }

    /// <summary>Store ID of the optional "Support development" add-on. Empty until the listing exists; the button stays hidden while empty. It unlocks nothing.</summary>
    public static readonly string TipAddOnStoreId = "";

    public static bool TipAvailable => !string.IsNullOrWhiteSpace(TipAddOnStoreId);

    private Task<bool>? _tipLookup;

    /// <summary>True only when the Store lists the tip add-on as purchasable. Looked up once; false on any failure or timeout.</summary>
    public Task<bool> IsTipPurchasableAsync() => _tipLookup ??= LookUpTipAsync();

    private async Task<bool> LookUpTipAsync()
    {
        try
        {
            if (!TipAvailable || _storeContext is null) return false;
            Task<StoreProductQueryResult> query = _storeContext
                .GetStoreProductsAsync(new[] { "Durable", "Consumable", "UnmanagedConsumable" }, new[] { TipAddOnStoreId }).AsTask();
            if (await Task.WhenAny(query, Task.Delay(PriceQueryTimeout)) != query) return false;
            StoreProductQueryResult result = await query;
            return result.Products.TryGetValue(TipAddOnStoreId, out StoreProduct? product) && product is not null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PortalBatteryCare: tip lookup unavailable: {ex}");
            return false;
        }
    }

    /// <summary>Buys the tip add-on. Unlocks nothing; returns the Store status, or NetworkError when it cannot be reached.</summary>
    public async Task<StorePurchaseStatus> PurchaseTipAsync()
    {
        try
        {
            if (!TipAvailable || _storeContext is null) return StorePurchaseStatus.NetworkError;
            StorePurchaseResult result = await _storeContext.RequestPurchaseAsync(TipAddOnStoreId);
            return result.Status;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PortalBatteryCare: tip purchase unavailable: {ex}");
            return StorePurchaseStatus.NetworkError;
        }
    }

    public async Task<StorePurchaseStatus> PurchaseProUnlockAsync()
    {
        try
        {
            if (_storeContext is null) return StorePurchaseStatus.NetworkError;
            StorePurchaseResult result = await _storeContext.RequestPurchaseAsync(ProUnlockStoreId);
            if (result.Status is StorePurchaseStatus.Succeeded or StorePurchaseStatus.AlreadyPurchased)
                await IsProUnlockedAsync(forceLicenseRefresh: true);
            return result.Status;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PortalBatteryCare: Store purchase unavailable: {ex}");
            return StorePurchaseStatus.NetworkError;
        }
    }
}

