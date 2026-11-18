namespace Portal.Core.Battery;

public enum BatteryToastKind
{
    /// <summary>Charging stopped at the charge limit. Setting: <see cref="BatterySettings.NotifyWhenLimitReached"/>.</summary>
    LimitReached,
    /// <summary>No limit the app can use: the battery reached the reminder level on the charger. Same setting as <see cref="LimitReached"/>.</summary>
    Unplug,
    /// <summary>On battery at the low level. Setting: <see cref="BatterySettings.NotifyPlugIn"/>.</summary>
    PlugIn,
    /// <summary>The running app ended a top-up and the limit is back on. Setting: <see cref="BatterySettings.NotifyTopUpFinished"/>.</summary>
    TopUpFinished,
}

/// <summary>One notification to show. <paramref name="Limit"/> is the charge limit it refers to, if any.</summary>
public sealed record BatteryToast(BatteryToastKind Kind, int Percent, int? Limit = null)
{
    /// <summary>For <see cref="BatteryToastKind.TopUpFinished"/>: the battery was full, not just out of time.</summary>
    public bool Full { get; init; }

    /// <summary>For <see cref="BatteryToastKind.TopUpFinished"/>: ended early because the battery was warm.</summary>
    public bool TooHot { get; init; }
}

/// <summary>What the toast decision is worked out from. Plain values only, so it can be tested without hardware.</summary>
public sealed record BatteryToastInput
{
    public int? Percent { get; init; }
    public bool OnAc { get; init; }
    public bool Charging { get; init; }
    /// <summary>A charge limit below 100 % that is in place on the firmware and no top-up is running; null otherwise.</summary>
    public int? HoldLimit { get; init; }
    /// <summary>Monitor mode: the level to remind the user to unplug at; null when the laptop has a limit.</summary>
    public int? UnplugAt { get; init; }
}

/// <summary>
/// Decides when a battery notification is due. Each kind fires once per event (it re-arms only after the battery has
/// charged below the level again, or for "plug in" after charging or rising above the level) and never again within
/// <see cref="MinGap"/>. A switched-off kind still follows the events, so turning it on does not fire a stale one.
/// </summary>
public sealed class BatteryToastPlanner
{
    public static readonly TimeSpan MinGap = TimeSpan.FromMinutes(30);

    /// <summary>Charging that stops this close below the limit counts as holding at the limit.</summary>
    public const int HoldTolerance = 2;

    /// <summary>"Plug in" re-arms once the level is this far above the reminder level.</summary>
    public const int PlugInRearmGap = 5;

    private readonly Dictionary<BatteryToastKind, DateTimeOffset> _lastShown = new();
    private bool _limitArmed;
    private bool _unplugArmed;
    private bool _plugInArmed = true;

    public IReadOnlyList<BatteryToast> Evaluate(BatteryToastInput input, BatterySettings settings, DateTimeOffset now)
    {
        var due = new List<BatteryToast>();
        if (input.Percent is not int p) return due;

        if (input.HoldLimit is int limit && limit < 100)
        {
            if (input.OnAc && input.Charging && p < limit) _limitArmed = true;
            // Still charging at or above the limit is not holding: the limit is not stopping the charge.
            bool holding = input.OnAc && !input.Charging && p >= limit - HoldTolerance;
            if (_limitArmed && holding)
            {
                _limitArmed = false;
                if (settings.NotifyWhenLimitReached && TryShow(BatteryToastKind.LimitReached, now))
                    due.Add(new BatteryToast(BatteryToastKind.LimitReached, p, limit));
            }
        }
        else _limitArmed = false;

        if (input.UnplugAt is int level)
        {
            if (input.OnAc && input.Charging && p < level) _unplugArmed = true;
            if (_unplugArmed && input.OnAc && p >= level)
            {
                _unplugArmed = false;
                if (settings.NotifyWhenLimitReached && TryShow(BatteryToastKind.Unplug, now))
                    due.Add(new BatteryToast(BatteryToastKind.Unplug, p, level));
            }
        }
        else _unplugArmed = false;

        int low = settings.LowReminderPercent;
        if (input.OnAc || p > low + PlugInRearmGap) _plugInArmed = true;
        if (_plugInArmed && !input.OnAc && p <= low)
        {
            _plugInArmed = false;
            if (settings.NotifyPlugIn && TryShow(BatteryToastKind.PlugIn, now))
                due.Add(new BatteryToast(BatteryToastKind.PlugIn, p, low));
        }

        return due;
    }

    /// <summary>The toast for a top-up the running app ended, or null when the limit is not back on or the toast is off.</summary>
    public BatteryToast? TopUpEnded(TopUpCheckResult ended, int? percent, BatterySettings settings, DateTimeOffset now)
    {
        if (!ended.Result.Succeeded || !settings.NotifyTopUpFinished) return null;
        if (!TryShow(BatteryToastKind.TopUpFinished, now)) return null;
        return new BatteryToast(BatteryToastKind.TopUpFinished, percent ?? 0, ended.Limit) { Full = ended.Reason == TopUpEndReason.Full, TooHot = ended.Reason == TopUpEndReason.TooHot };
    }

    private bool TryShow(BatteryToastKind kind, DateTimeOffset now)
    {
        if (_lastShown.TryGetValue(kind, out DateTimeOffset last) && now - last < MinGap) return false;
        _lastShown[kind] = now;
        return true;
    }
}
