namespace Portal.Core.Power;

/// <summary>Result of fitting estimated values to a target total.</summary>
public sealed record FitResult(IReadOnlyList<double> Values, double Leftover);

/// <summary>
/// Joins measured sensors, model estimates, PSU loss and an optional wall reading into one tree whose children add up to the root.
/// Pure: the same inputs always give the same tree.
/// </summary>
public static class PowerReconciler
{
    public static readonly TimeSpan WallFreshness = TimeSpan.FromSeconds(10);
    /// <summary>Leftovers smaller than this are rounding, not a missing component.</summary>
    public const double UnaccountedThresholdW = 0.5;
    /// <summary>Battery readings below this are idle noise; above the upper bound they are a misread.</summary>
    public const double MinBatteryW = 0.5;
    public const double MaxBatteryW = 500;
    public const string BatteryId = "battery";

    /// <summary>Plausible range of a fitted value, as factors of its estimate.</summary>
    public static (double Min, double Max) Range(PowerCategory category) => category switch
    {
        PowerCategory.Memory => (0.5, 2.0),
        PowerCategory.Storage => (0.3, 2.5),
        PowerCategory.Motherboard => (0.5, 3.0),
        _ => (0.3, 3.0),
    };

    public static bool IsFresh(WallReading? wall, DateTimeOffset now) =>
        wall is not null && double.IsFinite(wall.Watts) && wall.Watts > 0 && wall.Watts < 5000
        && now - wall.At <= WallFreshness && wall.At - now <= TimeSpan.FromSeconds(2);

    /// <summary>
    /// Scales <paramref name="estimates"/> so they sum to <paramref name="target"/>, each kept inside its own [min × e, max × e] range.
    /// Every value moves by the same factor until it hits its bound; whatever the bounds cannot absorb is returned as the leftover.
    /// </summary>
    public static FitResult Fit(IReadOnlyList<double> estimates, IReadOnlyList<(double Min, double Max)> ranges, double target)
    {
        int n = estimates.Count;
        double[] e = estimates.Select(v => double.IsFinite(v) && v > 0 ? v : 0).ToArray();
        double Sum(double k)
        {
            double s = 0;
            for (int i = 0; i < n; i++) s += e[i] * Math.Clamp(k, ranges[i].Min, ranges[i].Max);
            return s;
        }
        double[] Values(double k) => Enumerable.Range(0, n).Select(i => e[i] * Math.Clamp(k, ranges[i].Min, ranges[i].Max)).ToArray();

        if (n == 0 || e.Sum() <= 0) return new FitResult(e, target);
        double kLo = ranges.Min(r => r.Min), kHi = ranges.Max(r => r.Max);
        double low = Sum(kLo), high = Sum(kHi);
        if (target <= low) return new FitResult(Values(kLo), target - low);
        if (target >= high) return new FitResult(Values(kHi), target - high);
        // Sum(k) is continuous and non-decreasing, so bisection finds the factor.
        for (int i = 0; i < 60; i++)
        {
            double mid = (kLo + kHi) / 2;
            if (Sum(mid) < target) kLo = mid; else kHi = mid;
        }
        double[] fitted = Values((kLo + kHi) / 2);
        return new FitResult(fitted, target - fitted.Sum());
    }

    /// <summary>Applies a learned calibration to every estimated leaf (quality becomes Calibrated).</summary>
    public static IReadOnlyList<PowerNode> ApplyCalibration(IReadOnlyList<PowerNode> components, PowerCalibration calibration)
    {
        if (!calibration.IsLearned) return components;
        return components.Select(c => PowerNodes.MapLeaves(c, leaf =>
        {
            if (leaf.Quality != PowerQuality.Estimated || leaf.Watts is not double w) return leaf;
            double baseW = leaf.Id == PowerModelIds.ChipsetId ? Math.Max(0, w + calibration.BaselineOffsetW) : w;
            return leaf with { Watts = Math.Round(baseW * calibration.Scale, 2), Quality = PowerQuality.Calibrated };
        })).ToArray();
    }

    /// <summary>Builds the whole-PC tree.</summary>
    /// <param name="components">Component nodes from the model builder (uncalibrated).</param>
    /// <param name="psu">What the PSU reports about itself, if anything.</param>
    /// <param name="wall">Latest wall reading; ignored when older than <see cref="WallFreshness"/>.</param>
    /// <param name="batteryW">Signed battery power on a laptop: positive while charging, negative while discharging, null without a reading.</param>
    public static PowerTreeSnapshot Reconcile(IReadOnlyList<PowerNode> components, PsuMeasurement psu, WallReading? wall, PowerModelSettings settings, DateTimeOffset now, double? batteryW = null)
    {
        PowerModelSettings s = settings;
        double? battery = batteryW is double bw && double.IsFinite(bw) && Math.Abs(bw) < MaxBatteryW && Math.Abs(bw) >= MinBatteryW ? Math.Round(bw, 2) : null;
        double charge = battery is > 0 ? battery.Value : 0;
        double discharge = battery is < 0 ? -battery.Value : 0;
        IReadOnlyList<PowerNode> calibrated = ApplyCalibration(components, s.Calibration);
        double measuredDc = SumLeaves(calibrated, n => n.Quality == PowerQuality.Measured);
        double estimatedDc = SumLeaves(calibrated, n => n.IsEstimate);
        bool psuLossMeasured = psu.InputW is double && psu.OutputW is double;

        double? reference = null;
        PowerModelMode mode;
        string? source = null;
        bool onBattery = false;
        // A wall meter or PSU input includes whatever the charger puts into the battery; that part is shown as its own stream.
        if (IsFresh(wall, now)) { reference = wall!.Watts; mode = PowerModelMode.WallMeter; source = wall.SourceName; }
        else if (psu.InputW is double input) { reference = input; mode = PowerModelMode.PsuMeasured; source = psu.Name; }
        else if (discharge > 0) { reference = discharge; mode = PowerModelMode.Battery; onBattery = true; }
        else mode = s.Calibration.IsLearned ? PowerModelMode.Calibrated : PowerModelMode.Estimate;

        var children = new List<PowerNode>();
        double dc, wallW;
        PowerQuality rootQuality;
        if (reference is double refAll)
        {
            if (!onBattery && charge >= refAll) charge = 0;
            double refW = onBattery ? refAll : refAll - charge;
            dc = onBattery ? refW : psu.OutputW ?? PsuEfficiency.DcFromWall(refW, s.PsuRatedWatts, s.PsuLevel, s.HighLineMains);
            double residual = dc - measuredDc;
            // Fit every estimated leaf across the tree so the components add up to the DC target.
            var leaves = calibrated.SelectMany(c => c.Leaves()).Where(l => l.IsEstimate && l.Watts is double).ToList();
            FitResult fit = Fit(leaves.Select(l => l.Watts!.Value).ToArray(), leaves.Select(l => Range(l.Category)).ToArray(), residual);
            var fitted = new Dictionary<PowerNode, double>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < leaves.Count; i++) fitted[leaves[i]] = fit.Values[i];
            foreach (PowerNode c in calibrated)
                children.Add(PowerNodes.MapLeaves(c, leaf => fitted.TryGetValue(leaf, out double v)
                    ? leaf with { Watts = Math.Round(v, 2), Quality = PowerQuality.Calibrated }
                    : leaf));
            if (Math.Abs(fit.Leftover) >= UnaccountedThresholdW)
                children.Add(PowerNodes.Leaf("unaccounted", "Unaccounted", PowerCategory.Unaccounted, fit.Leftover, PowerQuality.Estimated,
                    fit.Leftover > 0 ? "Measured total minus every known part" : "Known parts exceed the measured total"));
            wallW = refW;
            // On battery there is no power supply between the source and the parts.
            if (!onBattery)
                children.Add(PowerNodes.Leaf("psu.loss", "Power supply loss", PowerCategory.PsuLoss, Math.Max(0, refW - dc),
                    psuLossMeasured ? PowerQuality.Measured : PowerQuality.Estimated, psuLossMeasured ? "Reported by the power supply" : PsuDetail(s)));
            if (charge > 0) { children.Add(BatteryLeaf(charge)); wallW += charge; }
            rootQuality = PowerQuality.Measured;
        }
        else
        {
            children.AddRange(calibrated);
            dc = measuredDc + estimatedDc;
            wallW = PsuEfficiency.WallFromDc(dc, s.PsuRatedWatts, s.PsuLevel, s.HighLineMains);
            // The learned calibration was fitted through this same curve, so with it the loss is part of the calibrated total.
            if (dc > 0)
                children.Add(PowerNodes.Leaf("psu.loss", "Power supply loss", PowerCategory.PsuLoss, wallW - dc,
                    mode == PowerModelMode.Calibrated ? PowerQuality.Calibrated : PowerQuality.Estimated, PsuDetail(s)));
            if (charge > 0) { children.Add(BatteryLeaf(charge)); wallW += charge; }
            rootQuality = PowerNodes.Combine(children);
        }

        bool hasAny = children.Any(c => c.HasValue);
        string detail = mode switch
        {
            PowerModelMode.WallMeter => "Wall meter: " + source,
            PowerModelMode.PsuMeasured => "Power supply input: " + source,
            PowerModelMode.Battery => "Battery discharge",
            PowerModelMode.Calibrated => "Model with calibration",
            _ => "Model estimate",
        };
        var root = new PowerNode
        {
            Id = "pc",
            Name = "Whole PC",
            Category = PowerCategory.Total,
            Watts = hasAny ? Math.Round(wallW, 2) : null,
            Quality = hasAny ? rootQuality : PowerQuality.Unavailable,
            Detail = detail,
            Children = children,
        };
        return new PowerTreeSnapshot
        {
            Timestamp = now,
            Mode = mode,
            Root = root,
            DcWatts = hasAny ? Math.Round(dc, 2) : null,
            WallSourceName = mode == PowerModelMode.WallMeter ? source : null,
            BatteryWatts = battery,
        };
    }

    private static PowerNode BatteryLeaf(double chargeW) =>
        PowerNodes.Leaf(BatteryId, "Battery", PowerCategory.Battery, chargeW, PowerQuality.Measured, "Charging");

    private static string PsuDetail(PowerModelSettings s) =>
        $"{s.PsuLevel} {s.PsuRatedWatts} W efficiency curve";

    private static double SumLeaves(IEnumerable<PowerNode> nodes, Func<PowerNode, bool> where) =>
        nodes.SelectMany(n => n.Leaves()).Where(l => l.HasValue && where(l)).Sum(l => l.Watts!.Value);
}

/// <summary>Learns <see cref="PowerCalibration"/> from stable wall readings at low load.</summary>
public static class PowerCalibrator
{
    public const double MinScale = 0.5;
    public const double MaxScale = 2.0;
    public const double MaxCpuLoadPercent = 15;
    public static readonly TimeSpan StableWindow = TimeSpan.FromSeconds(30);

    /// <summary>True when the readings cover at least <see cref="StableWindow"/> and vary by no more than 5 % (at least 3 W).</summary>
    public static bool IsStable(IReadOnlyList<WallReading> recent, DateTimeOffset now)
    {
        var window = recent.Where(r => now - r.At <= StableWindow + TimeSpan.FromSeconds(5) && double.IsFinite(r.Watts) && r.Watts > 0).ToList();
        if (window.Count < 5) return false;
        if (window.Max(r => r.At) - window.Min(r => r.At) < StableWindow - TimeSpan.FromSeconds(5)) return false;
        double min = window.Min(r => r.Watts), max = window.Max(r => r.Watts);
        double mean = window.Average(r => r.Watts);
        return max - min <= Math.Max(3, 0.05 * mean);
    }

    /// <summary>
    /// One learning step. <paramref name="components"/> are the uncalibrated nodes. The gap between wall-derived DC and the model goes
    /// first into the board baseline (the least known part at idle), then into the overall scale. Returns null when the sample is rejected.
    /// </summary>
    public static PowerCalibration? Learn(PowerCalibration current, IReadOnlyList<PowerNode> components, double wallW, PowerModelSettings settings, DateTimeOffset now, string? sourceName)
    {
        if (!double.IsFinite(wallW) || wallW <= 0) return null;
        var leaves = components.SelectMany(c => c.Leaves()).Where(l => l.HasValue).ToList();
        double measured = leaves.Where(l => l.Quality == PowerQuality.Measured).Sum(l => l.Watts!.Value);
        double estimated = leaves.Where(l => l.Quality == PowerQuality.Estimated).Sum(l => l.Watts!.Value);
        double board = leaves.Where(l => l.Id == PowerModelIds.ChipsetId).Sum(l => l.Watts!.Value);
        if (estimated <= 0) return null;
        double dc = PsuEfficiency.DcFromWall(wallW, settings.PsuRatedWatts, settings.PsuLevel, settings.HighLineMains);
        double residual = dc - measured;
        if (residual <= 0) return null;

        double gap = residual - estimated;
        double offset = Math.Clamp(gap, -0.6 * board, 2.0 * board);
        double scale = Math.Clamp(residual / (estimated + offset), MinScale, MaxScale);

        PowerCalibration next = current.Clone();
        double alpha = current.Samples < 10 ? 1.0 / (current.Samples + 1) : 0.1;
        next.BaselineOffsetW = current.IsLearned ? current.BaselineOffsetW + alpha * (offset - current.BaselineOffsetW) : offset;
        next.Scale = current.IsLearned ? current.Scale + alpha * (scale - current.Scale) : scale;
        next.Samples = current.Samples + 1;
        next.LearnedUtc = now;
        next.SourceName = sourceName;
        return next.Normalize();
    }
}
