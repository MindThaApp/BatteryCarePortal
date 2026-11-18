namespace Portal.Core.Battery;

public enum HistoryRange { Day, Week, Month }

/// <summary>One slice of time. Without samples <see cref="Average"/>, <see cref="Min"/> and <see cref="Max"/> are null: a gap, never a filled-in value.</summary>
public sealed record HistoryBucket(DateTimeOffset Start, DateTimeOffset End, double? Average, double? Min, double? Max, int Count)
{
    public bool HasData => Count > 0;
}

/// <summary>A run of neighbouring buckets that all have data; the chart draws one line per segment.</summary>
public sealed record HistorySegment(IReadOnlyList<HistoryBucket> Buckets);

public sealed record HistorySeries(
    HistoryRange Range,
    DateTimeOffset From,
    DateTimeOffset To,
    TimeSpan BucketSize,
    IReadOnlyList<HistoryBucket> Buckets,
    double? Min,
    double? Max,
    double? Last,
    int SampleCount)
{
    public bool HasData => SampleCount > 0;

    public IReadOnlyList<HistorySegment> Segments()
    {
        var segments = new List<HistorySegment>();
        var run = new List<HistoryBucket>();
        foreach (HistoryBucket b in Buckets)
        {
            if (b.HasData) { run.Add(b); continue; }
            if (run.Count > 0) { segments.Add(new HistorySegment(run)); run = new List<HistoryBucket>(); }
        }
        if (run.Count > 0) segments.Add(new HistorySegment(run));
        return segments;
    }
}

/// <summary>The chart's vertical range.</summary>
public sealed record HistoryScale(double Low, double High)
{
    public double Fraction(double value) => High <= Low ? 0 : Math.Clamp((value - Low) / (High - Low), 0, 1);
}

public sealed record HistoryTableRow(DateTimeOffset Start, double? Average, double? Min, double? Max);

/// <summary>Turns recorded samples into chart buckets. Pure: no files, no clock, no UI.</summary>
public static class HistoryShaping
{
    public static TimeSpan Span(HistoryRange range) => range switch
    {
        HistoryRange.Day => TimeSpan.FromHours(24),
        HistoryRange.Week => TimeSpan.FromDays(7),
        _ => TimeSpan.FromDays(30),
    };

    public static int Days(HistoryRange range) => (int)Span(range).TotalDays;

    public static TimeSpan BucketSize(HistoryRange range) => range switch
    {
        HistoryRange.Day => TimeSpan.FromMinutes(15),
        HistoryRange.Week => TimeSpan.FromHours(1),
        _ => TimeSpan.FromHours(6),
    };

    /// <summary>The ranges a window of this many days allows, shortest first. The 24-hour range is always there.</summary>
    public static IReadOnlyList<HistoryRange> AvailableRanges(int windowDays)
    {
        var ranges = new List<HistoryRange> { HistoryRange.Day };
        if (windowDays >= 7) ranges.Add(HistoryRange.Week);
        if (windowDays >= 30) ranges.Add(HistoryRange.Month);
        return ranges;
    }

    public static double? Level(BatteryHistoryEntry e) => e.Percent;
    public static double? Watts(BatteryHistoryEntry e) => e.BatteryW;

    public static HistorySeries Build(IEnumerable<BatteryHistoryEntry> entries, HistoryRange range, DateTimeOffset now,
        Func<BatteryHistoryEntry, double?> value)
    {
        TimeSpan span = Span(range), size = BucketSize(range);
        DateTimeOffset from = now - span;
        int count = (int)Math.Ceiling(span / size);
        var values = new List<double>[count];

        int samples = 0;
        double? last = null;
        DateTimeOffset lastTime = DateTimeOffset.MinValue;
        foreach (BatteryHistoryEntry e in entries)
        {
            DateTimeOffset t = e.Time;
            if (t < from || t > now || value(e) is not double v || double.IsNaN(v) || double.IsInfinity(v)) continue;
            int index = Math.Min(count - 1, (int)((t - from) / size));
            (values[index] ??= new List<double>()).Add(v);
            samples++;
            if (t >= lastTime) { lastTime = t; last = v; }
        }

        var buckets = new List<HistoryBucket>(count);
        double? min = null, max = null;
        for (int i = 0; i < count; i++)
        {
            DateTimeOffset start = from + size * i;
            List<double>? list = values[i];
            if (list is null) { buckets.Add(new HistoryBucket(start, start + size, null, null, null, 0)); continue; }
            double lo = list.Min(), hi = list.Max();
            buckets.Add(new HistoryBucket(start, start + size, list.Average(), lo, hi, list.Count));
            min = min is null ? lo : Math.Min(min.Value, lo);
            max = max is null ? hi : Math.Max(max.Value, hi);
        }
        return new HistorySeries(range, from, now, size, buckets, min, max, last, samples);
    }

    /// <summary>Charge level always uses the full 0 to 100 scale.</summary>
    public static HistoryScale LevelScale() => new(0, 100);

    /// <summary>Watts scale: includes zero (charging above, discharging below) and rounds out to 5, 10 or 20 W steps.</summary>
    public static HistoryScale WattsScale(double? min, double? max)
    {
        double lo = Math.Min(0, min ?? 0), hi = Math.Max(0, max ?? 0);
        double step = hi - lo <= 20 ? 5 : hi - lo <= 60 ? 10 : 20;
        lo = Math.Floor(lo / step) * step;
        hi = Math.Ceiling(hi / step) * step;
        if (hi <= lo) hi = lo + step;
        return new HistoryScale(lo, hi);
    }

    /// <summary>Newest first, buckets with data only: the accessible alternative to a chart.</summary>
    public static IReadOnlyList<HistoryTableRow> TableRows(HistorySeries series) =>
        series.Buckets.Where(b => b.HasData).Reverse().Select(b => new HistoryTableRow(b.Start, b.Average, b.Min, b.Max)).ToList();

    /// <summary>The bucket nearest to a horizontal position 0 to 1 that has data; null when the series has none.</summary>
    public static HistoryBucket? Nearest(HistorySeries series, double fraction)
    {
        double target = Math.Clamp(fraction, 0, 1) * series.Buckets.Count - 0.5;
        HistoryBucket? best = null;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < series.Buckets.Count; i++)
        {
            if (!series.Buckets[i].HasData) continue;
            double d = Math.Abs(i - target);
            if (d < bestDistance) { bestDistance = d; best = series.Buckets[i]; }
        }
        return best;
    }

    /// <summary>True when recording began after the start of the window, so the left part of the chart is empty by design.</summary>
    public static bool StartedInsideWindow(DateTimeOffset? firstSample, DateTimeOffset from, TimeSpan bucketSize) =>
        firstSample is DateTimeOffset first && first > from + bucketSize;

    /// <summary>Display-only samples for fake mode when little was recorded. Deterministic, never written anywhere.</summary>
    public static IReadOnlyList<BatteryHistoryEntry> SimulatedEntries(DateTimeOffset now, TimeSpan span, TimeSpan step)
    {
        var list = new List<BatteryHistoryEntry>();
        DateTimeOffset from = now - span;
        for (DateTimeOffset t = from; t <= now; t += step)
        {
            double hours = (t - from).TotalHours;
            double phase = hours % 24;
            bool onBattery = phase is >= 9 and < 15;
            double level = phase switch
            {
                < 9 => 60,
                < 15 => 60 - (phase - 9) * (26.0 / 6),
                < 18 => 34 + (phase - 15) * (26.0 / 3),
                _ => 60,
            };
            double watts = onBattery ? -(9 + 3 * Math.Sin(hours * 1.7)) : phase is >= 15 and < 18 ? 28 + 4 * Math.Sin(hours * 1.3) : 0.5 * Math.Sin(hours);
            list.Add(new BatteryHistoryEntry
            {
                UnixSeconds = t.ToUnixTimeSeconds(),
                Percent = (int)Math.Round(level),
                Charging = phase is >= 15 and < 18,
                OnAc = !onBattery,
                BatteryW = Math.Round(watts, 1),
            });
        }
        return list;
    }

    /// <summary>Display-only health trend for fake mode: about 92.4 % sliding slowly to 91.7 % over twelve weeks.</summary>
    public static IReadOnlyList<BatteryHealthPoint> SimulatedHealth(DateOnly today)
    {
        var list = new List<BatteryHealthPoint>();
        for (int i = 84; i >= 0; i -= 7)
        {
            double f = (84 - i) / 84.0;
            list.Add(new BatteryHealthPoint(today.AddDays(-i), Math.Round(92.4 - 0.7 * f, 1), 212 - i * 2 / 7));
        }
        return list;
    }

    /// <summary>Simulated data is used only in fake mode and only when too little was recorded to draw anything useful.</summary>
    public static bool UseSimulatedEntries(bool fakeMode, int recordedSamples) => fakeMode && recordedSamples < 30;

    public static bool UseSimulatedHealth(bool fakeMode, int recordedPoints) => fakeMode && recordedPoints < 3;
}

/// <summary>Summary of the daily health values for the health chart and its table.</summary>
public sealed record HealthTrendModel(IReadOnlyList<BatteryHealthPoint> Points, double? First, double? Last, double? Change, int? Cycles)
{
    public bool HasData => Points.Count > 0;

    public static HealthTrendModel Build(IEnumerable<BatteryHealthPoint> points)
    {
        List<BatteryHealthPoint> ordered = points.OrderBy(p => p.Date).ToList();
        if (ordered.Count == 0) return new HealthTrendModel(ordered, null, null, null, null);
        double first = ordered[0].HealthPercent, last = ordered[^1].HealthPercent;
        return new HealthTrendModel(ordered, first, last, last - first, ordered[^1].CycleCount);
    }

    /// <summary>Horizontal position 0 to 1 of a day between the first and last point (0.5 for a single point).</summary>
    public double DayFraction(DateOnly day)
    {
        if (Points.Count < 2) return 0.5;
        int total = Points[^1].Date.DayNumber - Points[0].Date.DayNumber;
        return total <= 0 ? 0.5 : (double)(day.DayNumber - Points[0].Date.DayNumber) / total;
    }

    /// <summary>Health always uses the full 0 to 100 scale, so a small loss looks small.</summary>
    public static HistoryScale Scale() => new(0, 100);
}
