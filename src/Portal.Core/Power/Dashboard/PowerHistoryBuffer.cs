namespace Portal.Core.Power.Dashboard;

/// <summary>One watts value at one moment.</summary>
public readonly record struct PowerSample(DateTimeOffset At, double Watts);

/// <summary>A range of the history cut down to a drawable number of points.</summary>
public sealed record PowerSeries(IReadOnlyList<PowerSample> Points, double Min, double Max, double Average)
{
    public static PowerSeries Empty { get; } = new([], 0, 0, 0);
}

/// <summary>
/// In-memory history of the total draw for the live chart. The last hour is kept one value per second and the last day
/// as 30 second averages, so memory stays small (about 6,500 values) however long the app runs.
/// </summary>
public sealed class PowerHistoryBuffer
{
    public static readonly TimeSpan FineSpan = TimeSpan.FromHours(1);
    public static readonly TimeSpan CoarseSpan = TimeSpan.FromHours(24);
    public const int CoarseStepSeconds = 30;

    private readonly object _gate = new();
    private readonly List<PowerSample> _fine = [];
    private readonly List<PowerSample> _coarse = [];
    private long _bucket = long.MinValue;
    private double _bucketSum;
    private int _bucketCount;

    public int FineCount { get { lock (_gate) return _fine.Count; } }
    public int CoarseCount { get { lock (_gate) return _coarse.Count; } }

    public void Add(DateTimeOffset at, double watts)
    {
        if (!double.IsFinite(watts) || watts < 0) return;
        lock (_gate)
        {
            if (_fine.Count > 0 && at <= _fine[^1].At) return;
            _fine.Add(new PowerSample(at, watts));
            Trim(_fine, at - FineSpan);

            long bucket = at.ToUnixTimeSeconds() / CoarseStepSeconds;
            if (bucket != _bucket)
            {
                FlushBucket();
                _bucket = bucket;
            }
            _bucketSum += watts;
            _bucketCount++;
            Trim(_coarse, at - CoarseSpan);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _fine.Clear();
            _coarse.Clear();
            _bucket = long.MinValue;
            _bucketSum = 0;
            _bucketCount = 0;
        }
    }

    /// <summary>The newest <paramref name="range"/> of the history as at most <paramref name="maxPoints"/> averaged points.</summary>
    public PowerSeries Get(TimeSpan range, int maxPoints, DateTimeOffset now)
    {
        maxPoints = Math.Max(2, maxPoints);
        List<PowerSample> source;
        lock (_gate)
        {
            // Short ranges read the per-second values; longer ones the 30 second averages (plus the bucket still filling).
            if (range <= FineSpan) source = [.. _fine];
            else
            {
                source = [.. _coarse];
                if (_bucketCount > 0 && _fine.Count > 0)
                    source.Add(new PowerSample(_fine[^1].At, _bucketSum / _bucketCount));
            }
        }
        DateTimeOffset from = now - range;
        List<PowerSample> inRange = source.Where(s => s.At >= from && s.At <= now).ToList();
        if (inRange.Count == 0) return PowerSeries.Empty;
        double min = inRange.Min(s => s.Watts), max = inRange.Max(s => s.Watts), avg = inRange.Average(s => s.Watts);
        return new PowerSeries(Downsample(inRange, from, range, maxPoints), min, max, avg);
    }

    /// <summary>Averages the samples into equal time bins; empty bins are skipped so gaps stay gaps.</summary>
    public static IReadOnlyList<PowerSample> Downsample(IReadOnlyList<PowerSample> samples, DateTimeOffset from, TimeSpan range, int maxPoints)
    {
        if (samples.Count <= maxPoints) return samples;
        var result = new List<PowerSample>(maxPoints);
        double binTicks = (double)range.Ticks / maxPoints;
        int i = 0;
        for (int bin = 0; bin < maxPoints && i < samples.Count; bin++)
        {
            DateTimeOffset end = from.AddTicks((long)Math.Ceiling(binTicks * (bin + 1)));
            double sum = 0;
            long tickSum = 0;
            int n = 0;
            while (i < samples.Count && (samples[i].At < end || bin == maxPoints - 1))
            {
                sum += samples[i].Watts;
                tickSum += samples[i].At.Ticks - from.Ticks;
                n++;
                i++;
            }
            if (n > 0) result.Add(new PowerSample(from.AddTicks(tickSum / n), sum / n));
        }
        return result;
    }

    private void FlushBucket()
    {
        if (_bucketCount > 0)
            _coarse.Add(new PowerSample(DateTimeOffset.FromUnixTimeSeconds(_bucket * CoarseStepSeconds + CoarseStepSeconds / 2), _bucketSum / _bucketCount));
        _bucketSum = 0;
        _bucketCount = 0;
    }

    private static void Trim(List<PowerSample> list, DateTimeOffset oldest)
    {
        int n = 0;
        while (n < list.Count && list[n].At < oldest) n++;
        // Remove in chunks so the cost of shifting the list is paid rarely.
        if (n >= 64 || (n > 0 && n == list.Count)) list.RemoveRange(0, n);
    }
}
