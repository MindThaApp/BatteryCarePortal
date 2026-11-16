namespace Portal.Core.Power;

/// <summary>Energy integrated over a period. <see cref="NodeWh"/> is keyed by <see cref="PowerNode.Id"/>.</summary>
public sealed record EnergyTotals
{
    public DateTimeOffset Since { get; init; }
    public DateTimeOffset Until { get; init; }
    /// <summary>Energy drawn from the wall (including charging the battery). Time on battery is not in here, see <see cref="BatteryWh"/>.</summary>
    public double TotalWh { get; init; }
    /// <summary>Energy used while running on the battery. Not grid energy, so costs ignore it.</summary>
    public double BatteryWh { get; init; }
    /// <summary>Weakest quality of any total that went into <see cref="TotalWh"/>.</summary>
    public PowerQuality TotalQuality { get; init; } = PowerQuality.Unavailable;
    public IReadOnlyDictionary<string, double> NodeWh { get; init; } = new Dictionary<string, double>();
}

/// <summary>A finished local day of energy.</summary>
public sealed record EnergyDay(DateOnly Day, EnergyTotals Totals);

/// <summary>
/// Integrates the total and every node's watts into watt-hours. Each sample's watts are held until the next sample;
/// gaps longer than <see cref="MaxGap"/> (sleep, a stalled timer) are not integrated. Thread-safe.
/// </summary>
public sealed class EnergyAccumulator
{
    public static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly Func<DateTimeOffset, DateOnly> _localDay;
    private Period _sinceStart;
    private Period _today;
    private DateOnly _day;
    private PowerTreeSnapshot? _last;

    private sealed class Period
    {
        public DateTimeOffset Since;
        public DateTimeOffset Until;
        public double TotalWh;
        public double BatteryWh;
        public PowerQuality Quality = PowerQuality.Unavailable;
        public readonly Dictionary<string, double> Nodes = new(StringComparer.Ordinal);

        public EnergyTotals ToTotals() => new()
        {
            Since = Since,
            Until = Until,
            TotalWh = Math.Round(TotalWh, 4),
            BatteryWh = Math.Round(BatteryWh, 4),
            TotalQuality = Quality,
            NodeWh = Nodes.ToDictionary(p => p.Key, p => Math.Round(p.Value, 4), StringComparer.Ordinal),
        };
    }

    /// <param name="localDay">Maps a time to the local calendar day; defaults to the system time zone.</param>
    public EnergyAccumulator(Func<DateTimeOffset, DateOnly>? localDay = null)
    {
        _localDay = localDay ?? (t => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(t, TimeZoneInfo.Local).DateTime));
        _sinceStart = new Period { Since = DateTimeOffset.MinValue };
        _today = new Period { Since = DateTimeOffset.MinValue };
    }

    /// <summary>Raised (on the caller's thread) when a local day ends; history storage can persist it.</summary>
    public event EventHandler<EnergyDay>? DayCompleted;

    public EnergyTotals SinceStart { get { lock (_gate) return _sinceStart.ToTotals(); } }
    public EnergyTotals Today { get { lock (_gate) return _today.ToTotals(); } }

    /// <summary>Adds one snapshot. Returns the totals since start.</summary>
    public EnergyTotals Add(PowerTreeSnapshot snapshot)
    {
        EnergyDay? finished = null;
        EnergyTotals result;
        lock (_gate)
        {
            DateTimeOffset now = snapshot.Timestamp;
            if (_last is null)
            {
                _sinceStart.Since = _sinceStart.Until = now;
                _today.Since = _today.Until = now;
                _day = _localDay(now);
            }
            else
            {
                TimeSpan dt = now - _last.Timestamp;
                if (dt > TimeSpan.Zero && dt <= MaxGap)
                {
                    double hours = dt.TotalHours;
                    Integrate(_sinceStart, _last, hours);
                    Integrate(_today, _last, hours);
                }
                if (dt > TimeSpan.Zero) { _sinceStart.Until = now; _today.Until = now; }
                DateOnly day = _localDay(now);
                if (day != _day)
                {
                    finished = new EnergyDay(_day, _today.ToTotals());
                    _today = new Period { Since = now, Until = now };
                    _day = day;
                }
            }
            _last = snapshot;
            result = _sinceStart.ToTotals();
        }
        if (finished is not null) { try { DayCompleted?.Invoke(this, finished); } catch { } }
        return result;
    }

    public void Reset()
    {
        lock (_gate)
        {
            _sinceStart = new Period { Since = DateTimeOffset.MinValue };
            _today = new Period { Since = DateTimeOffset.MinValue };
            _last = null;
        }
    }

    private static void Integrate(Period p, PowerTreeSnapshot s, double hours)
    {
        if (s.Root.HasValue)
        {
            double wh = s.Root.Watts!.Value * hours;
            if (s.Mode == PowerModelMode.Battery) p.BatteryWh += wh; else p.TotalWh += wh;
            p.Quality = Weakest(p.Quality, s.Root.Quality);
        }
        foreach (PowerNode n in s.Root.Descendants())
        {
            if (!n.HasValue) continue;
            p.Nodes[n.Id] = (p.Nodes.TryGetValue(n.Id, out double wh) ? wh : 0) + n.Watts!.Value * hours;
        }
    }

    private static PowerQuality Weakest(PowerQuality a, PowerQuality b)
    {
        static int Rank(PowerQuality q) => q switch { PowerQuality.Measured => 3, PowerQuality.Calibrated => 2, PowerQuality.Estimated => 1, _ => 4 };
        return Rank(a) <= Rank(b) ? a : b;
    }
}
