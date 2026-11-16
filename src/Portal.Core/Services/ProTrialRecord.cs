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
/// the first launch. All keys contain "Trial", so profiles never carry them.
/// </summary>
public sealed class ProTrialRecord
{
    public const string StartKey = "ProTrialStartUtc";

    public static readonly TimeSpan Duration = TimeSpan.FromDays(14);

    private readonly IDictionary<string, object> _values;
    private readonly object _gate = new();

    public ProTrialRecord(IDictionary<string, object> values) => _values = values;

    public ProTrialTerms Terms
    {
        get
        {
            lock (_gate) return new ProTrialTerms(ReadStart(), Duration);
        }
    }

    /// <summary>Starts the trial at <paramref name="nowUtc"/> unless it has started already. True only for the call that started it.</summary>
    public bool TryStart(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
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
