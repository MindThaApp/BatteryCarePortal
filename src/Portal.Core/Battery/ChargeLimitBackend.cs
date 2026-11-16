namespace Portal.Core.Battery;

public enum ChargeLimitKind
{
    /// <summary>No charge limit (the backend may still be able to pause charging).</summary>
    None,
    /// <summary>Any whole percentage between <see cref="ChargeLimitCapabilities.MinLimit"/> and <see cref="ChargeLimitCapabilities.MaxLimit"/>.</summary>
    ContinuousRange,
    /// <summary>Only the values in <see cref="ChargeLimitCapabilities.FixedValues"/>.</summary>
    FixedValues,
    /// <summary>An on/off conservation mode that holds the battery near <see cref="ChargeLimitCapabilities.ConservationPercent"/>.</summary>
    OnOffConservation,
}

public sealed record ChargeLimitCapabilities
{
    public static ChargeLimitCapabilities Unsupported { get; } = new() { Kind = ChargeLimitKind.None };

    public ChargeLimitKind Kind { get; init; }
    public int MinLimit { get; init; } = 50;
    public int MaxLimit { get; init; } = 100;
    public IReadOnlyList<int> FixedValues { get; init; } = Array.Empty<int>();
    /// <summary>Approximate level an on/off conservation mode holds (model dependent, often 60 or 80).</summary>
    public int ConservationPercent { get; init; } = 60;
    public bool SupportsStartThreshold { get; init; }
    public int MinStartThreshold { get; init; } = 40;
    public int MaxStartThreshold { get; init; } = 95;
    /// <summary>Smallest allowed gap between the start and stop thresholds.</summary>
    public int MinStartStopGap { get; init; } = 5;
    public bool CanPauseCharging { get; init; }

    public bool SupportsLimit => Kind != ChargeLimitKind.None;

    /// <summary>The values a UI should offer, ascending. Empty for continuous ranges and no-limit backends.</summary>
    public IReadOnlyList<int> DiscreteValues => Kind switch
    {
        ChargeLimitKind.FixedValues => FixedValues.OrderBy(v => v).ToArray(),
        ChargeLimitKind.OnOffConservation => new[] { ConservationPercent, 100 },
        _ => Array.Empty<int>(),
    };

    /// <summary>Snaps a requested limit to the nearest value the backend can actually apply. Null when unsupported.</summary>
    public int? Snap(int requested)
    {
        switch (Kind)
        {
            case ChargeLimitKind.ContinuousRange:
                return Math.Clamp(requested, MinLimit, MaxLimit);
            case ChargeLimitKind.FixedValues when FixedValues.Count > 0:
                return FixedValues.OrderBy(v => Math.Abs(v - requested)).ThenByDescending(v => v).First();
            case ChargeLimitKind.OnOffConservation:
                return requested >= 100 ? 100 : ConservationPercent;
            default:
                return null;
        }
    }

    public bool IsValidLimit(int limit) => Kind switch
    {
        ChargeLimitKind.ContinuousRange => limit >= MinLimit && limit <= MaxLimit,
        ChargeLimitKind.FixedValues => FixedValues.Contains(limit),
        ChargeLimitKind.OnOffConservation => limit == 100 || limit == ConservationPercent,
        _ => false,
    };
}

public sealed record ChargeLimitState
{
    public static ChargeLimitState Unknown { get; } = new();

    /// <summary>Current limit in percent (100 = no limit). Null when it could not be read.</summary>
    public int? Limit { get; init; }
    public int? StartThreshold { get; init; }
    public bool? ChargingPaused { get; init; }
    /// <summary>Raw value(s) as read from the firmware, for diagnostics.</summary>
    public string? RawValue { get; init; }
    /// <summary>Why the state could not be read, if it could not.</summary>
    public string? Message { get; init; }
    /// <summary>The read failed only because the firmware interface refused access to a standard user.</summary>
    public bool ReadAccessDenied { get; init; }
    /// <summary>The firmware's charge mode where it has one (Dell PrimaryBattChargeCfg), compared on restore.</summary>
    public string? Mode { get; init; }
}

/// <summary>Target values for one apply. Null fields are left unchanged.</summary>
public sealed record ChargeLimitRequest
{
    public int? Limit { get; init; }
    public int? StartThreshold { get; init; }
    /// <summary>Puts the start threshold back to the firmware default instead of setting a value.</summary>
    public bool ResetStartThreshold { get; init; }
    public bool? ChargingPaused { get; init; }
    /// <summary>False for unattended changes: a backend must then return <see cref="BackendWriteResult.NeedsElevation"/> instead of prompting.</summary>
    public bool AllowElevationPrompt { get; init; } = true;

    public bool IsEmpty => Limit is null && StartThreshold is null && !ResetStartThreshold && ChargingPaused is null;
}

/// <summary>
/// The firmware values a backend found before its first write, so "Restore defaults" can put them back.
/// Persisted in <see cref="OriginalStateStore"/>.
/// </summary>
public sealed record BackendOriginalState
{
    public string BackendId { get; init; } = string.Empty;
    public DateTimeOffset CapturedUtc { get; init; }
    /// <summary>Firmware setting name to original value, exactly as read.</summary>
    public Dictionary<string, string> Values { get; init; } = new(StringComparer.Ordinal);
}

public sealed record BackendWriteResult
{
    public bool Success { get; init; }
    public bool Cancelled { get; init; }
    /// <summary>The change needs an administrator prompt, which this (unattended) request did not allow. Nothing was written.</summary>
    public bool NeedsElevation { get; init; }
    public string Message { get; init; } = string.Empty;
    /// <summary>State read back after the write, when the backend could read it.</summary>
    public ChargeLimitState? ReadBack { get; init; }
    /// <summary>Set when the <see cref="WriteGate"/> refused the write. Nothing was written.</summary>
    public WriteRefusal Refusal { get; init; }
    /// <summary>The app's own read-back comparison after the write.</summary>
    public ReadBackCheck ReadBackCheck { get; init; }
    /// <summary>The failure happened before any firmware write was attempted, so the firmware is certainly unchanged.</summary>
    public bool NotAttempted { get; init; }

    public static BackendWriteResult Ok(string message = "OK", ChargeLimitState? readBack = null) =>
        new() { Success = true, Message = message, ReadBack = readBack };

    /// <summary>A failure where a write may have been attempted (the outcome is uncertain).</summary>
    public static BackendWriteResult Fail(string message) => new() { Success = false, Message = message };

    /// <summary>A failure before any write was attempted.</summary>
    public static BackendWriteResult FailBeforeWrite(string message) => new() { Success = false, Message = message, NotAttempted = true };

    public static BackendWriteResult Refused(WriteGateResult gate) =>
        new() { Success = false, Refusal = gate.Refusal, Message = gate.Message, NotAttempted = true };

    public static BackendWriteResult ElevationNeeded() =>
        new() { Success = false, NeedsElevation = true, Message = "This change needs administrator approval.", NotAttempted = true };

    public static BackendWriteResult UserCancelled() =>
        new() { Success = false, Cancelled = true, Message = "The administrator prompt was cancelled. Nothing was changed.", NotAttempted = true };
}

public enum BackendConfidence
{
    /// <summary>Simulated backend, safe everywhere.</summary>
    Simulated,
    /// <summary>Interface matches the vendor's own tooling; enabled when detection succeeds.</summary>
    Verified,
    /// <summary>Plausible but not checked on a real device; writes only on a verified model or with the user's beta opt-in (see <see cref="WriteGate"/>).</summary>
    Unverified,
}

/// <summary>
/// A vendor-specific way to limit charging. Implementations must:
/// never write anything in <see cref="Detect"/> or <see cref="GetState"/>; read the current value before writing;
/// validate every request against <see cref="Capabilities"/>; never retry a failed write; and treat any
/// write error, missing class or method, or BIOS password as a failure with a clear message.
/// </summary>
public interface IChargeLimitBackend
{
    string Id { get; }
    /// <summary>Human-readable description such as "Lenovo conservation mode".</summary>
    string Name { get; }
    BackendConfidence Confidence { get; }
    /// <summary>True when writes need the elevated helper (one administrator prompt per apply).</summary>
    bool RequiresElevation { get; }

    /// <summary>Read-only probe. Returns false on any error.</summary>
    bool Detect();

    ChargeLimitCapabilities Capabilities { get; }

    ChargeLimitState GetState();

    /// <summary>Reads the firmware values that a later restore should put back.</summary>
    BackendOriginalState CaptureOriginal();

    /// <summary>The state a restore of <paramref name="original"/> must read back, or null when the original cannot be restored.</summary>
    ChargeLimitState? ExpectedStateAfterRestore(BackendOriginalState original) =>
        original.BackendId == Id && int.TryParse(original.Values.GetValueOrDefault("Limit"), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int limit)
            ? new ChargeLimitState { Limit = limit }
            : null;

    /// <summary>Applies all fields of the request in one operation (one administrator prompt at most).</summary>
    Task<BackendWriteResult> ApplyAsync(ChargeLimitRequest request, CancellationToken cancellationToken = default);

    /// <summary>Puts the captured original firmware values back.</summary>
    Task<BackendWriteResult> RestoreAsync(BackendOriginalState original, CancellationToken cancellationToken = default);

    Task<BackendWriteResult> SetLimitAsync(int limit, CancellationToken cancellationToken = default) =>
        ApplyAsync(new ChargeLimitRequest { Limit = limit }, cancellationToken);

    Task<BackendWriteResult> SetStartThresholdAsync(int? threshold, CancellationToken cancellationToken = default) =>
        ApplyAsync(threshold is int t
            ? new ChargeLimitRequest { StartThreshold = t }
            : new ChargeLimitRequest { ResetStartThreshold = true }, cancellationToken);

    Task<BackendWriteResult> SetChargingPausedAsync(bool paused, CancellationToken cancellationToken = default) =>
        ApplyAsync(new ChargeLimitRequest { ChargingPaused = paused }, cancellationToken);
}

public static class ChargeLimitRequestValidator
{
    /// <summary>Returns an error message, or null when the request can be applied as is.</summary>
    public static string? Validate(ChargeLimitRequest request, ChargeLimitCapabilities caps, int? effectiveLimit)
    {
        if (request.IsEmpty) return "Nothing to apply.";
        if (request.Limit is int limit)
        {
            if (!caps.SupportsLimit) return "This device does not support a charge limit.";
            if (!caps.IsValidLimit(limit)) return $"A charge limit of {limit} % is not supported by this device.";
        }
        if (request.StartThreshold is int start || request.ResetStartThreshold)
        {
            if (!caps.SupportsStartThreshold) return "This device does not support a charge start threshold.";
            if (request.StartThreshold is int s)
            {
                if (s < caps.MinStartThreshold || s > caps.MaxStartThreshold)
                    return $"A start threshold of {s} % is outside {caps.MinStartThreshold}–{caps.MaxStartThreshold} %.";
                int stop = request.Limit ?? effectiveLimit ?? 100;
                if (stop - s < caps.MinStartStopGap)
                    return $"The start threshold must be at least {caps.MinStartStopGap} % below the limit.";
            }
        }
        if (request.ChargingPaused is not null && !caps.CanPauseCharging) return "This device cannot pause charging.";
        return null;
    }
}
