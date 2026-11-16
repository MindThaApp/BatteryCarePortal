namespace Portal.Core.Battery;

/// <summary>Maps the engine's three calibration phases onto the five-step graphic on the Battery page.</summary>
public static class CalibrationStepper
{
    public const int StepCount = 5;
    /// <summary>After the last charge the battery rests at full for about this long before the limit takes over.</summary>
    public static readonly TimeSpan HoldWindow = TimeSpan.FromHours(1);
    /// <summary>How long after finishing the graphic still shows "back to your limit" while the level is above the limit.</summary>
    public static readonly TimeSpan ReturnWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// The highlighted step, 1 to 5, or 0 when no calibration is running or just finished.
    /// 1 charge to 100 %, 2 discharge to the low target, 3 charge to 100 % again, 4 hold at full, 5 back to the limit.
    /// </summary>
    public static int Current(CalibrationProgress progress, int? percent, int limit, bool limitEnabled, DateTimeOffset now)
    {
        switch (progress.Step)
        {
            case CalibrationStep.ChargeToFull: return 1;
            case CalibrationStep.DischargeToLow: return 2;
            case CalibrationStep.RechargeToFull: return 3;
        }
        if (progress.LastCompletedUtc is not DateTimeOffset done || now < done) return 0;
        TimeSpan since = now - done;
        if (since <= HoldWindow && percent is >= 100) return 4;
        if (since <= ReturnWindow && limitEnabled && limit < 100 && percent is int p && p > limit) return 5;
        return 0;
    }

    /// <summary>Whole days since the last finished calibration, or null if there never was one.</summary>
    public static int? DaysSince(CalibrationProgress progress, DateTimeOffset now) =>
        progress.LastCompletedUtc is DateTimeOffset done ? Math.Max(0, (int)Math.Floor((now - done).TotalDays)) : null;
}

public enum SuspendAdvice
{
    None,
    /// <summary>Software-only charge care cannot act while the PC sleeps, so the battery may pass the limit.</summary>
    MayExceedLimit,
}

/// <summary>Decides what to do when Windows goes to sleep and wakes up.</summary>
public static class SleepPolicy
{
    public static SuspendAdvice OnSuspend(ChargeCareEvaluation evaluation, BatterySnapshot snapshot)
    {
        if (!snapshot.Present || !snapshot.IsOnAc || snapshot.Percent is not int percent) return SuspendAdvice.None;
        if (evaluation.Mode != ChargeCareMode.Software) return SuspendAdvice.None;
        if (evaluation.EffectiveLimit >= 100) return SuspendAdvice.None;
        return percent >= evaluation.EffectiveLimit ? SuspendAdvice.MayExceedLimit : SuspendAdvice.None;
    }

    public static bool ReapplyOnResume(ChargeCareMode mode) => mode == ChargeCareMode.Hardware;
}

/// <summary>Connects the suspend and resume events to the engine. Keeps no state of its own.</summary>
public sealed class SleepGuard
{
    private readonly ChargeCareEngine _engine;
    private readonly Action<string> _log;

    public SleepGuard(ChargeCareEngine engine, Action<string> log)
    {
        _engine = engine;
        _log = log;
    }

    public SuspendAdvice OnSuspend()
    {
        SuspendAdvice advice = SleepPolicy.OnSuspend(_engine.Last, _engine.LastSnapshot);
        _log(advice == SuspendAdvice.MayExceedLimit
            ? $"Sleep: software charge care cannot act while asleep; level {_engine.LastSnapshot.Percent} % may pass the limit."
            : "Sleep: nothing to warn about.");
        return advice;
    }

    /// <summary>Re-applies the limit through the backend. Returns true when a hardware re-apply was requested.</summary>
    public async Task<bool> OnResumeAsync(CancellationToken cancellationToken = default)
    {
        if (!SleepPolicy.ReapplyOnResume(_engine.Mode))
        {
            _log("Resume: no hardware limit to re-apply.");
            return false;
        }
        _log("Resume: re-applying the charge limit through the backend.");
        await _engine.ReapplyAfterResumeAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}

/// <summary>The live heat-protection line: current battery temperature and the pause threshold.</summary>
public sealed record HeatReadout(double? TemperatureC, double ThresholdC, bool ProtectionEnabled, bool Paused)
{
    public bool HasReading => TemperatureC.HasValue;

    public static HeatReadout From(BatterySnapshot snapshot, HeatProtectionSettings heat, bool paused) =>
        new(snapshot.TemperatureC, heat.MaxTempC, heat.Enabled, paused);
}

public enum ChartRange
{
    Day,
    Week,
    Month,
    ThreeMonths,
    Year,
    All,
}

public sealed record ChartPoint(double X, double Y);

/// <summary>Turns history samples and the daily health trend into chart-ready points (x between 0 and 1).</summary>
public static class BatteryChartBuilder
{
    public static TimeSpan Window(ChartRange range) => range switch
    {
        ChartRange.Day => TimeSpan.FromHours(24),
        ChartRange.Week => TimeSpan.FromDays(7),
        ChartRange.Month => TimeSpan.FromDays(30),
        ChartRange.ThreeMonths => TimeSpan.FromDays(90),
        ChartRange.Year => TimeSpan.FromDays(365),
        _ => TimeSpan.MaxValue,
    };

    /// <summary>
    /// Averages the samples that carry a value into at most <paramref name="maxPoints"/> evenly spaced buckets over
    /// the covered time. The covered time starts at the first sample inside the window (never less than
    /// <paramref name="minSpan"/>) and ends at <paramref name="now"/>. Empty when fewer than two samples have a value.
    /// </summary>
    public static IReadOnlyList<ChartPoint> Series(IReadOnlyList<BatteryHistoryEntry> entries, DateTimeOffset now, TimeSpan window,
        Func<BatteryHistoryEntry, double?> value, int maxPoints = 240, TimeSpan? minSpan = null)
    {
        var with = new List<(DateTimeOffset T, double V)>();
        foreach (BatteryHistoryEntry e in entries)
        {
            if (value(e) is double v && e.Time <= now && now - e.Time <= window) with.Add((e.Time, v));
        }
        if (with.Count < 2) return Array.Empty<ChartPoint>();

        TimeSpan least = minSpan ?? TimeSpan.FromMinutes(10);
        TimeSpan available = now - with.Min(w => w.T);
        TimeSpan span = available < least ? least : available;
        DateTimeOffset from = now - span;
        int buckets = Math.Max(2, maxPoints);
        var sum = new double[buckets];
        var count = new int[buckets];
        foreach ((DateTimeOffset t, double v) in with)
        {
            double x = (t - from).TotalSeconds / span.TotalSeconds;
            int b = Math.Clamp((int)(x * buckets), 0, buckets - 1);
            sum[b] += v;
            count[b]++;
        }
        var points = new List<ChartPoint>();
        for (int b = 0; b < buckets; b++)
        {
            if (count[b] > 0) points.Add(new ChartPoint((b + 0.5) / buckets, sum[b] / count[b]));
        }
        return points;
    }

    /// <summary>How much time the samples inside the window cover, for the left axis label.</summary>
    public static TimeSpan Covered(IReadOnlyList<BatteryHistoryEntry> entries, DateTimeOffset now, TimeSpan window)
    {
        foreach (BatteryHistoryEntry e in entries)
        {
            if (now - e.Time <= window) return now - e.Time;
        }
        return TimeSpan.Zero;
    }

    /// <summary>Daily health and cycle points inside the range, x spread over the days they cover.</summary>
    public static (IReadOnlyList<ChartPoint> Health, IReadOnlyList<ChartPoint> Cycles, DateOnly? From, DateOnly? To) HealthTrend(
        IReadOnlyList<BatteryHealthPoint> trend, DateOnly today, ChartRange range)
    {
        TimeSpan window = Window(range);
        IEnumerable<BatteryHealthPoint> inRange = trend.OrderBy(p => p.Date);
        if (window != TimeSpan.MaxValue) inRange = inRange.Where(p => today.DayNumber - p.Date.DayNumber <= window.TotalDays);
        List<BatteryHealthPoint> list = inRange.ToList();
        if (list.Count < 2) return (Array.Empty<ChartPoint>(), Array.Empty<ChartPoint>(), null, null);
        double first = list[0].Date.DayNumber, span = Math.Max(1, list[^1].Date.DayNumber - first);
        var health = list.Select(p => new ChartPoint((p.Date.DayNumber - first) / span, p.HealthPercent)).ToList();
        var cycles = list.Where(p => p.CycleCount.HasValue)
            .Select(p => new ChartPoint((p.Date.DayNumber - first) / span, p.CycleCount!.Value)).ToList();
        return (health, cycles, list[0].Date, list[^1].Date);
    }
}
