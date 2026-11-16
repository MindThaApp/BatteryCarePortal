using System.Globalization;

namespace Portal.Core.Services;

/// <summary>The Pro trial as stored: when it started (null while it has not started) and how long it runs.</summary>
public readonly record struct ProTrialTerms(DateTimeOffset? StartUtc, TimeSpan Duration)
{
    public bool IsStarted => StartUtc is not null;

    /// <summary>A trial that has not started yet still grants Pro; a started one grants it until its length has passed.</summary>
    public bool GrantsAccessAt(DateTimeOffset nowUtc) =>
        StartUtc is not { } start || (nowUtc >= start && nowUtc - start < Duration);

    /// <summary>Time left; the full length while the trial has not started.</summary>
    public TimeSpan RemainingAt(DateTimeOffset nowUtc)
    {
        if (StartUtc is not { } start) return Duration;
        TimeSpan remaining = Duration - (nowUtc - start);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}

/// <summary>
/// Keeps the Pro trial in the app's local settings. The 14-day trial starts the first time a Pro feature is used, not at
/// the first launch. Installs from before this rule keep their old terms: 30 days from the first launch, settled once by
/// <see cref="Migrate"/>. All keys contain "Trial", so profiles never carry them.
/// </summary>
public sealed class ProTrialRecord
{
    public const string StartKey = "ProTrialStartUtc";
    public const string LegacyTermsKey = "ProTrialLegacyTerms";
    public const string MigratedKey = "ProTrialTermsMigrated";

    public static readonly TimeSpan Duration = TimeSpan.FromDays(14);
    public static readonly TimeSpan LegacyDuration = TimeSpan.FromDays(30);

    private readonly IDictionary<string, object> _values;
    private readonly object _gate = new();

    public ProTrialRecord(IDictionary<string, object> values) => _values = values;

    public bool IsMigrated
    {
        get { lock (_gate) return _values.TryGetValue(MigratedKey, out object? done) && done is true; }
    }

    /// <summary>
    /// Settles the terms once. <paramref name="firstLaunchUtcBeforeThisVersion"/> is the first-launch time that was already
    /// stored when this version first ran (an existing install), or null on a new install. An existing install keeps the
    /// trial it had: 30 days from its first launch, so a running trial is not shortened and an ended one stays ended.
    /// Later calls change nothing. Returns true when it wrote anything.
    /// </summary>
    public bool Migrate(DateTimeOffset? firstLaunchUtcBeforeThisVersion)
    {
        lock (_gate)
        {
            if (_values.TryGetValue(MigratedKey, out object? done) && done is true) return false;
            if (firstLaunchUtcBeforeThisVersion is { } firstLaunch && ReadStart() is null)
            {
                _values[StartKey] = Format(firstLaunch);
                _values[LegacyTermsKey] = true;
            }
            _values[MigratedKey] = true;
            return true;
        }
    }

    public ProTrialTerms Terms
    {
        get
        {
            lock (_gate)
            {
                bool legacy = _values.TryGetValue(LegacyTermsKey, out object? flag) && flag is true;
                return new ProTrialTerms(ReadStart(), legacy ? LegacyDuration : Duration);
            }
        }
    }

    /// <summary>Starts the trial at <paramref name="nowUtc"/> unless it has started already. True only for the call that started it.</summary>
    public bool TryStart(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            if (!(_values.TryGetValue(MigratedKey, out object? done) && done is true)) return false;
            if (ReadStart() is not null) return false;
            _values[StartKey] = Format(nowUtc);
            return true;
        }
    }

    private DateTimeOffset? ReadStart() => Parse(_values.TryGetValue(StartKey, out object? value) ? value as string : null);

    public static DateTimeOffset? Parse(string? serialized) =>
        !string.IsNullOrWhiteSpace(serialized) &&
        DateTimeOffset.TryParseExact(serialized, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
            ? parsed.ToUniversalTime()
            : null;

    private static string Format(DateTimeOffset utc) => utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
