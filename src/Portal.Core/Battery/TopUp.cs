namespace Portal.Core.Battery;

public enum TopUpEndReason
{
    None,
    Full,
    Expired,
}

/// <summary>When a top-up may start and when it is over. Plain values only, so it can be tested without hardware.</summary>
public static class TopUpPolicy
{
    /// <summary>A top-up ends at 100 % or after this long, whichever comes first.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(8);

    public const int FullPercent = 100;

    /// <summary>
    /// The backend writes without elevation (so the app can end the top-up by itself), the app's own limit is on and is
    /// the value the firmware reports, nothing is pending, and the battery is not full yet.
    /// </summary>
    public static bool CanStart(ChargeIntent? intent, int? firmwareLimit, int? percent, bool writesWithoutElevation) =>
        writesWithoutElevation &&
        intent is { Managed: true, LimitOn: true, TopUpActive: false, NeedsReapply: false, ReapplyFailed: false } &&
        firmwareLimit is int limit && limit < 100 && limit == intent.Limit &&
        percent is not >= FullPercent;

    /// <summary>"Stop top-up" stays available while a top-up is saved as running, whatever the write gate says; the controller answers.</summary>
    public static bool CanStop(ChargeIntent? intent) => intent is { Managed: true, LimitOn: true, TopUpActive: true };

    /// <summary>A top-up the running app should end now. Never one that is already waiting for the user to put the limit back.</summary>
    public static TopUpEndReason ShouldEnd(ChargeIntent? intent, int? percent, DateTimeOffset now)
    {
        if (intent is not { Managed: true, LimitOn: true, TopUpActive: true, NeedsReapply: false, ReapplyFailed: false })
            return TopUpEndReason.None;
        if (percent is >= FullPercent) return TopUpEndReason.Full;
        if (intent.TopUpUntil is not DateTimeOffset until || now >= until) return TopUpEndReason.Expired;
        return TopUpEndReason.None;
    }
}

/// <summary>What one top-up check did.</summary>
public sealed record TopUpCheckResult(TopUpEndReason Reason, ChargeActionResult Result, int Limit)
{
    /// <summary>The limit may still be off and the app will not try again by itself; tell the user once.</summary>
    public bool NeedsUser { get; init; }
}

/// <summary>
/// Ends a running top-up when the battery is full or the top-up has expired, while the app runs. It only calls
/// <see cref="ChargeLimitController.EndTopUpAsync"/> without an elevation prompt, which can only put the steady limit back on,
/// and tries at most once per saved top-up.
/// </summary>
public sealed class TopUpMonitor
{
    private readonly ChargeLimitController _controller;
    private readonly TimeProvider _time;
    private DateTimeOffset? _attemptedFor;

    public TopUpMonitor(ChargeLimitController controller, TimeProvider? time = null)
    {
        _controller = controller;
        _time = time ?? TimeProvider.System;
    }

    public ChargeLimitController Controller => _controller;

    /// <summary>Null when there is nothing to end.</summary>
    public async Task<TopUpCheckResult?> CheckAsync(int? percent, CancellationToken cancellationToken = default)
    {
        ChargeIntent? intent = _controller.LoadIntent();
        TopUpEndReason reason = TopUpPolicy.ShouldEnd(intent, percent, _time.GetUtcNow());
        if (reason == TopUpEndReason.None) return null;

        // When the intent file cannot be updated after a try, the same saved top-up is still there next time; it is never written twice unattended.
        if (_attemptedFor == intent!.UpdatedUtc) return null;

        ChargeActionResult result = await _controller.EndTopUpAsync(allowPrompt: false, cancellationToken).ConfigureAwait(false);
        if (result.Status is ChargeActionStatus.Busy or ChargeActionStatus.NothingToDo) return null;
        _attemptedFor = intent.UpdatedUtc;

        // A limit another tool set is not off; the Charging page asks the user which one to keep.
        bool needsUser = !result.Succeeded && result.Status != ChargeActionStatus.ChangedElsewhere;
        return new TopUpCheckResult(reason, result, intent.Limit) { NeedsUser = needsUser };
    }
}
