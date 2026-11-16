using System.Globalization;
using System.Text.Json;

namespace Portal.Core.Services;

public enum ProAccessSource
{
    None,
    LegacyPurchase,
    AddOn,
    Trial,
}

public enum LegacyPurchaseResult
{
    Paid,
    NotPaid,
    Unknown,
}

/// <summary>
/// Whether the Pro edition's Pro features are available: a purchase of the app made while the listing was paid (permanent,
/// remembered in settings once seen), the durable Pro add-on, or the in-app trial (open until a Pro feature is first used,
/// then 14 days from that use; 30 days from the first launch for installs from before that rule).
/// When the Store can't answer, the result falls back to the add-on and trial gate, is logged and is not cached, so the next
/// check asks again instead of locking anyone out for longer.
/// </summary>
/// <remarks>
/// Still to do in Partner Center before the Pro listing becomes free: switch its price to free, confirm
/// <see cref="ProAddOnStoreId"/> is the durable add-on new customers buy, and set <see cref="PriceFlipCutoffUtc"/> to the
/// moment the price changes.
/// </remarks>
public sealed class ProEntitlementService
{
    // Durable Pro add-on of the Pro listing. If the add-on for the free listing gets a new Store ID, keep honouring this one
    // as well so the people who bought it keep Pro.
    public const string ProAddOnStoreId = "PBC-PRO-ADDON-STORE-ID-PLACEHOLDER";

    // The moment (UTC) the Pro listing switches to free in Partner Center. A purchase of the app acquired before this counts
    // as paid. A value earlier than the real switch only sends the latest buyers to the trial and add-on gate; a later one
    // would hand Pro to free installs made in between.
    public static readonly DateTimeOffset PriceFlipCutoffUtc = new(2026, 11, 9, 18, 47, 58, TimeSpan.Zero);

    /// <summary>Length of the trial for new installs; installs from before the on-use trial keep 30 days (see <see cref="ProTrialRecord"/>).</summary>
    public static readonly TimeSpan TrialDuration = ProTrialRecord.Duration;

    // Unset Store dates (DateTimeOffset.MinValue and similar) would otherwise sit before any cutoff and read as paid.
    private static readonly DateTimeOffset EarliestPlausibleAcquisitionUtc = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan StoreResultCacheDuration = TimeSpan.FromMinutes(5);

    private readonly IStoreLicenseProvider? _store;
    private readonly Func<ProTrialTerms> _trial;
    private readonly Func<bool> _legacyConfirmed;
    private readonly Action _confirmLegacy;
    private readonly Action<string, string>? _log;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly DateTimeOffset _cutoffUtc;
    private readonly SemaphoreSlim _storeLock = new(1, 1);
    private readonly HashSet<string> _logged = [];
    private CachedAccess? _cache;

    private sealed record CachedAccess(ProAccessSource Source, DateTimeOffset AtUtc);

    /// <param name="store">Null when the Store isn't reachable at all (no Store context).</param>
    /// <param name="trial">The in-app trial: its start (null until a Pro feature is first used) and its length.</param>
    /// <param name="legacyConfirmed">Reads the remembered "bought while the listing was paid" flag.</param>
    /// <param name="confirmLegacy">Remembers that flag.</param>
    /// <param name="log">Receives failures as (context, reason) for the error log.</param>
    public ProEntitlementService(
        IStoreLicenseProvider? store,
        Func<ProTrialTerms> trial,
        Func<bool> legacyConfirmed,
        Action confirmLegacy,
        Action<string, string>? log = null,
        Func<DateTimeOffset>? utcNow = null,
        DateTimeOffset? priceFlipCutoffUtc = null)
    {
        _store = store;
        _trial = trial;
        _legacyConfirmed = legacyConfirmed;
        _confirmLegacy = confirmLegacy;
        _log = log;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _cutoffUtc = priceFlipCutoffUtc ?? PriceFlipCutoffUtc;
    }

    /// <summary>True while the trial grants Pro: before it has started, and while it runs.</summary>
    public bool IsTrialActive => IsTrialActiveAt(_trial(), _utcNow());

    /// <summary>False until a Pro feature is first used.</summary>
    public bool IsTrialStarted => _trial().IsStarted;

    /// <summary>Time left in the trial; its full length while it has not started.</summary>
    public TimeSpan TrialRemaining => _trial().RemainingAt(_utcNow());

    public static bool IsTrialActiveAt(ProTrialTerms trial, DateTimeOffset nowUtc) => trial.GrantsAccessAt(nowUtc);

    public async Task<bool> HasProAccessAsync(bool forceRefresh = false) =>
        await GetAccessSourceAsync(forceRefresh).ConfigureAwait(false) != ProAccessSource.None;

    public async Task<ProAccessSource> GetAccessSourceAsync(bool forceRefresh = false)
    {
        if (ReadLegacyConfirmed()) return ProAccessSource.LegacyPurchase;
        if (IsTrialActive) return ProAccessSource.Trial;
        if (!forceRefresh && FreshCache() is { } cached) return cached.Source;

        await _storeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!forceRefresh && FreshCache() is { } again) return again.Source;
            (ProAccessSource source, bool conclusive) = await QueryStoreAsync().ConfigureAwait(false);
            _cache = conclusive ? new CachedAccess(source, _utcNow()) : null;
            return source;
        }
        finally
        {
            _storeLock.Release();
        }
    }

    /// <summary>
    /// True when Pro is owned for good (remembered paid purchase or the durable add-on), even while the trial is still running.
    /// Lets the UI hide trial wording from someone who does not need the trial. Store failures read as "not owned" and are not cached.
    /// </summary>
    public async Task<bool> OwnsPermanentAccessAsync(bool forceRefresh = false)
    {
        if (ReadLegacyConfirmed()) return true;
        if (!forceRefresh && FreshCache() is { } cached) return cached.Source != ProAccessSource.None;

        await _storeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!forceRefresh && FreshCache() is { } again) return again.Source != ProAccessSource.None;
            (ProAccessSource source, bool conclusive) = await QueryStoreAsync().ConfigureAwait(false);
            _cache = conclusive ? new CachedAccess(source, _utcNow()) : null;
            return source != ProAccessSource.None;
        }
        finally
        {
            _storeLock.Release();
        }
    }

    private CachedAccess? FreshCache() =>
        _cache is { } cache && _utcNow() - cache.AtUtc < StoreResultCacheDuration ? cache : null;

    private bool ReadLegacyConfirmed()
    {
        try
        {
            return _legacyConfirmed();
        }
        catch (Exception ex)
        {
            Log("Pro licence: remembered purchase unreadable", Describe(ex));
            return false;
        }
    }

    private async Task<(ProAccessSource Source, bool Conclusive)> QueryStoreAsync()
    {
        if (_store is null)
        {
            Log("Pro licence check", "Store unavailable; using the trial and add-on gate.");
            return (ProAccessSource.None, false);
        }

        bool conclusive = true;
        try
        {
            if (await _store.IsDurableAddOnOwnedAsync(ProAddOnStoreId).ConfigureAwait(false)) return (ProAccessSource.AddOn, true);
        }
        catch (Exception ex)
        {
            conclusive = false;
            Log("Pro add-on check failed", Describe(ex));
        }

        try
        {
            IReadOnlyList<StoreSkuInfo> skus = await _store.GetCurrentAppSkusAsync().ConfigureAwait(false);
            switch (EvaluateLegacyPurchase(skus, _cutoffUtc, out string detail))
            {
                case LegacyPurchaseResult.Paid:
                    RememberLegacy();
                    return (ProAccessSource.LegacyPurchase, true);
                case LegacyPurchaseResult.Unknown:
                    conclusive = false;
                    Log("Paid Pro purchase check inconclusive", detail);
                    break;
            }
        }
        catch (Exception ex)
        {
            conclusive = false;
            Log("Paid Pro purchase check failed", Describe(ex));
        }

        return (ProAccessSource.None, conclusive);
    }

    private void RememberLegacy()
    {
        try
        {
            _confirmLegacy();
        }
        catch (Exception ex)
        {
            Log("Pro licence: could not remember the paid purchase", Describe(ex));
        }
    }

    /// <summary>
    /// Paid when a non-trial SKU of the app was acquired before <paramref name="cutoffUtc"/>. Unknown when the Store data
    /// can't settle it (no acquisition data, unreadable JSON or an implausible date), so the caller can fall back and log.
    /// </summary>
    public static LegacyPurchaseResult EvaluateLegacyPurchase(IReadOnlyList<StoreSkuInfo> skus, DateTimeOffset cutoffUtc, out string detail)
    {
        bool sawOwned = false;
        var problems = new List<string>();
        for (int i = 0; i < skus.Count; i++)
        {
            StoreSkuInfo sku = skus[i];
            SkuOwnership? json = ReadJson(sku.ExtendedJsonData, out string? jsonProblem);
            if (jsonProblem is not null) problems.Add($"sku {i}: {jsonProblem}");
            SkuOwnership? typed = ReadTyped(sku);
            SkuOwnership? owned = json is null ? typed
                : typed is null ? json
                : new SkuOwnership(json.AcquiredUtc ?? typed.AcquiredUtc, json.IsTrial || typed.IsTrial);
            if (owned is null) continue;

            sawOwned = true;
            if (owned.IsTrial) continue;
            if (owned.AcquiredUtc is not { } acquired || acquired < EarliestPlausibleAcquisitionUtc)
            {
                problems.Add($"sku {i}: owned without a usable acquired date");
                continue;
            }
            if (acquired < cutoffUtc)
            {
                detail = $"sku {i} acquired {acquired:O}";
                return LegacyPurchaseResult.Paid;
            }
        }

        if (problems.Count > 0)
        {
            detail = string.Join("; ", problems);
            return LegacyPurchaseResult.Unknown;
        }
        if (!sawOwned)
        {
            detail = $"none of {skus.Count} sku(s) carried acquisition data";
            return LegacyPurchaseResult.Unknown;
        }
        detail = "acquired on or after the price change";
        return LegacyPurchaseResult.NotPaid;
    }

    private sealed record SkuOwnership(DateTimeOffset? AcquiredUtc, bool IsTrial);

    // Store SKU JSON: collectionData { acquiredDate, isTrial } is present for the SKU the user owns. Property casing differs
    // between the catalog and collection parts, so names are matched case-insensitively.
    private static SkuOwnership? ReadJson(string? json, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { problem = "sku JSON is not an object"; return null; }
            if (Property(root, "collectionData") is not { ValueKind: JsonValueKind.Object } collection) return null;

            bool trial = IsTrue(Property(collection, "isTrial")) || IsTrue(Property(root, "isTrial")) ||
                (Property(root, "properties") is { ValueKind: JsonValueKind.Object } properties && IsTrue(Property(properties, "isTrial")));
            DateTimeOffset? acquired = null;
            if (Property(collection, "acquiredDate") is { ValueKind: JsonValueKind.String } date &&
                DateTimeOffset.TryParse(date.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
            {
                acquired = parsed;
            }
            return new SkuOwnership(acquired, trial);
        }
        catch (JsonException ex)
        {
            problem = $"unreadable sku JSON ({ex.Message})";
            return null;
        }
    }

    private static SkuOwnership? ReadTyped(StoreSkuInfo sku) =>
        sku.IsInUserCollection && sku.CollectionAcquiredUtc is { } acquired
            ? new SkuOwnership(acquired, sku.IsTrialSku || sku.CollectionIsTrial == true)
            : null;

    private static JsonElement? Property(JsonElement element, string name)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        }
        return null;
    }

    private static bool IsTrue(JsonElement? element) => element is { ValueKind: JsonValueKind.True };

    private static string Describe(Exception ex) => $"{ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}";

    private void Log(string context, string reason)
    {
        lock (_logged)
        {
            if (!_logged.Add(context + "\n" + reason)) return;
        }
        System.Diagnostics.Debug.WriteLine($"PortalBatteryCare: {context}: {reason}");
        try
        {
            _log?.Invoke(context, reason);
        }
        catch
        {
        }
    }
}
