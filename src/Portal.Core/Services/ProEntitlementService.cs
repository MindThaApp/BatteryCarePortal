namespace Portal.Core.Services;

public enum ProAccessSource
{
    None,
    AddOn,
    Trial,
}

/// <summary>
/// Whether Pro features are available: the durable Pro add-on, or the in-app trial (open until a Pro feature is first used,
/// then 14 days from that use).
/// When the Store can't answer, the result falls back to the trial gate, is logged and is not cached, so the next
/// check asks again instead of locking anyone out for longer.
/// </summary>
public sealed class ProEntitlementService
{
    // Durable Pro add-on. Placeholder until the Store listing exists.
    public const string ProAddOnStoreId = "BCP-PRO-ADDON-STORE-ID-PLACEHOLDER";

    public static readonly TimeSpan TrialDuration = ProTrialRecord.Duration;

    private static readonly TimeSpan StoreResultCacheDuration = TimeSpan.FromMinutes(5);

    private readonly IStoreLicenseProvider? _store;
    private readonly Func<ProTrialTerms> _trial;
    private readonly Action<string, string>? _log;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _storeLock = new(1, 1);
    private readonly HashSet<string> _logged = [];
    private CachedAccess? _cache;

    private sealed record CachedAccess(ProAccessSource Source, DateTimeOffset AtUtc);

    /// <param name="store">Null when the Store isn't reachable at all (no Store context).</param>
    /// <param name="trial">The in-app trial: its start (null until a Pro feature is first used) and its length.</param>
    /// <param name="log">Receives failures as (context, reason) for the error log.</param>
    public ProEntitlementService(
        IStoreLicenseProvider? store,
        Func<ProTrialTerms> trial,
        Action<string, string>? log = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _store = store;
        _trial = trial;
        _log = log;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
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
        if (IsTrialActive) return ProAccessSource.Trial;
        return await QueryCachedAsync(forceRefresh).ConfigureAwait(false);
    }

    /// <summary>
    /// True when Pro is owned for good (the durable add-on), even while the trial is still running.
    /// Lets the UI hide trial wording from someone who does not need the trial. Store failures read as "not owned" and are not cached.
    /// </summary>
    public async Task<bool> OwnsPermanentAccessAsync(bool forceRefresh = false) =>
        await QueryCachedAsync(forceRefresh).ConfigureAwait(false) != ProAccessSource.None;

    private async Task<ProAccessSource> QueryCachedAsync(bool forceRefresh)
    {
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

    private CachedAccess? FreshCache() =>
        _cache is { } cache && _utcNow() - cache.AtUtc < StoreResultCacheDuration ? cache : null;

    private async Task<(ProAccessSource Source, bool Conclusive)> QueryStoreAsync()
    {
        if (_store is null)
        {
            Log("Pro licence check", "Store unavailable; using the trial gate.");
            return (ProAccessSource.None, false);
        }

        try
        {
            return await _store.IsDurableAddOnOwnedAsync(ProAddOnStoreId).ConfigureAwait(false)
                ? (ProAccessSource.AddOn, true)
                : (ProAccessSource.None, true);
        }
        catch (Exception ex)
        {
            Log("Pro add-on check failed", $"{ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}");
            return (ProAccessSource.None, false);
        }
    }

    private void Log(string context, string reason)
    {
        lock (_logged)
        {
            if (!_logged.Add(context + "\n" + reason)) return;
        }
        System.Diagnostics.Debug.WriteLine($"BatteryCarePortal: {context}: {reason}");
        try
        {
            _log?.Invoke(context, reason);
        }
        catch
        {
        }
    }
}
