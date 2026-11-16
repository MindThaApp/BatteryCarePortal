using System.Text.Json;
using Portal.Core.Battery;
using Xunit;

namespace Portal.Core.Tests;

public class BatteryPageLogicTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ppt-pagelogic-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static readonly ChargeLimitCapabilities ThresholdCaps = new()
    {
        Kind = ChargeLimitKind.ContinuousRange, MinLimit = 50, MaxLimit = 100,
        SupportsStartThreshold = true, MinStartThreshold = 40, MaxStartThreshold = 95, MinStartStopGap = 5,
    };

    // ---- sailing as a difference

    [Fact]
    public void Sailing_range_gives_the_resume_level_and_follows_the_limit()
    {
        var s = new BatterySettings { Limit = 80, SailingRange = 5 };
        Assert.Equal(75, s.SailingLower);
        s.Limit = 90;
        Assert.Equal(85, s.SailingLower);
        Assert.Equal((90, 85), SailingMath.Describe(90, 5));
    }

    [Fact]
    public void Older_settings_with_an_absolute_resume_level_become_a_range()
    {
        BatterySettings s = JsonSerializer.Deserialize<BatterySettings>("{\"Enabled\":true,\"Limit\":85,\"SailingLower\":72}")!.Normalize();
        Assert.Equal(10, s.SailingRange);
        Assert.Equal(75, s.SailingLower);

        BatterySettings t = JsonSerializer.Deserialize<BatterySettings>("{\"Limit\":80,\"SailingLower\":78}")!.Normalize();
        Assert.Equal(2, t.SailingRange);
        Assert.Equal(78, t.SailingLower);

        BatterySettings same = JsonSerializer.Deserialize<BatterySettings>("{\"Limit\":80,\"SailingLower\":80}")!.Normalize();
        Assert.Equal(1, same.SailingRange);
    }

    [Fact]
    public void New_settings_round_trip_without_changing_the_range()
    {
        var s = new BatterySettings { Enabled = true, Limit = 70, SailingRange = 8 }.Normalize();
        BatterySettings back = s.Clone();
        Assert.Equal(8, back.SailingRange);
        Assert.Equal(62, back.SailingLower);
        Assert.Equal(10, new BatterySettings { SailingRange = 99 }.Normalize().SailingRange);
        Assert.Equal(1, new BatterySettings { SailingRange = -4 }.Normalize().SailingRange);
    }

    [Fact]
    public void Range_bounds_respect_the_hardware_floor_and_gap()
    {
        Assert.Equal((1, 10), SailingMath.Bounds(80));
        Assert.Equal((5, 10), SailingMath.Bounds(80, minGap: 5, minStartThreshold: 40));
        Assert.Equal((5, 7), SailingMath.Bounds(47, minGap: 5, minStartThreshold: 40));
        Assert.Equal(10, SailingMath.ClampRange(30, 80));
        Assert.Equal(5, SailingMath.ClampRange(2, 80, minGap: 5, minStartThreshold: 40));
    }

    [Fact]
    public void Limits_under_eighty_percent_are_flagged()
    {
        Assert.True(SailingMath.IsLowLimit(79));
        Assert.False(SailingMath.IsLowLimit(80));
    }

    [Fact]
    public async Task Engine_uses_the_range_for_the_start_threshold()
    {
        var backend = new TestBackend(ThresholdCaps);
        var engine = new ChargeCareEngine(backend, new BatterySettings { Enabled = true, Limit = 85, SailingRange = 7 },
            new ManualTimeProvider(Start));
        await engine.ProcessAsync(Snap.At(60, onAc: true, charging: true));
        Assert.Equal(78, backend.Start);
    }

    // ---- suspend and resume

    [Fact]
    public async Task Software_mode_over_the_limit_warns_before_sleep()
    {
        var engine = new ChargeCareEngine(null, new BatterySettings { Enabled = true, Limit = 80 }, new ManualTimeProvider(Start));
        var log = new List<string>();
        var guard = new SleepGuard(engine, log.Add);

        await engine.ProcessAsync(Snap.At(82, onAc: true));
        Assert.Equal(SuspendAdvice.MayExceedLimit, guard.OnSuspend());

        await engine.ProcessAsync(Snap.At(60, onAc: true, charging: true));
        Assert.Equal(SuspendAdvice.None, guard.OnSuspend());

        await engine.ProcessAsync(Snap.At(90, onAc: false));
        Assert.Equal(SuspendAdvice.None, guard.OnSuspend());
        Assert.Equal(3, log.Count);
    }

    [Fact]
    public async Task No_sleep_warning_in_hardware_mode_top_up_or_when_charge_care_is_off()
    {
        var hardware = new ChargeCareEngine(new TestBackend(ThresholdCaps), new BatterySettings { Enabled = true, Limit = 80 },
            new ManualTimeProvider(Start));
        await hardware.ProcessAsync(Snap.At(85, onAc: true));
        Assert.Equal(SuspendAdvice.None, new SleepGuard(hardware, _ => { }).OnSuspend());

        var topUp = new ChargeCareEngine(null, new BatterySettings { Enabled = true, Limit = 80 }, new ManualTimeProvider(Start));
        topUp.StartTopUp(TimeSpan.FromHours(2));
        await topUp.ProcessAsync(Snap.At(85, onAc: true));
        Assert.Equal(SuspendAdvice.None, new SleepGuard(topUp, _ => { }).OnSuspend());

        var off = new ChargeCareEngine(null, new BatterySettings { Enabled = false, Limit = 80 }, new ManualTimeProvider(Start));
        await off.ProcessAsync(Snap.At(95, onAc: true));
        Assert.Equal(SuspendAdvice.None, new SleepGuard(off, _ => { }).OnSuspend());
    }

    [Fact]
    public async Task Resume_writes_the_limit_again_in_hardware_mode_even_when_it_reads_back_fine()
    {
        var backend = new TestBackend(ThresholdCaps);
        var engine = new ChargeCareEngine(backend, new BatterySettings { Enabled = true, Limit = 80, SailingRange = 5 },
            new ManualTimeProvider(Start));
        var log = new List<string>();
        var guard = new SleepGuard(engine, log.Add);
        await engine.ProcessAsync(Snap.At(70, onAc: true, charging: true));
        Assert.Single(backend.Applied);

        Assert.True(await guard.OnResumeAsync());
        Assert.Equal(2, backend.Applied.Count);
        Assert.Equal(80, backend.Applied[1].Limit);
        Assert.Contains(log, l => l.StartsWith("Resume: re-applying"));

        await engine.ProcessAsync(Snap.At(71, onAc: true, charging: true));
        Assert.Equal(2, backend.Applied.Count);
    }

    [Fact]
    public async Task Resume_does_nothing_in_software_mode()
    {
        var engine = new ChargeCareEngine(null, new BatterySettings { Enabled = true, Limit = 80 }, new ManualTimeProvider(Start));
        await engine.ProcessAsync(Snap.At(70, onAc: true));
        var log = new List<string>();
        Assert.False(await new SleepGuard(engine, log.Add).OnResumeAsync());
        Assert.Single(log);
        Assert.False(SleepPolicy.ReapplyOnResume(ChargeCareMode.Disabled));
    }

    // ---- calibration steps

    [Theory]
    [InlineData(CalibrationStep.ChargeToFull, 1)]
    [InlineData(CalibrationStep.DischargeToLow, 2)]
    [InlineData(CalibrationStep.RechargeToFull, 3)]
    public void Running_calibration_maps_to_the_first_three_steps(CalibrationStep step, int expected)
    {
        var progress = new CalibrationProgress { Step = step };
        Assert.Equal(expected, CalibrationStepper.Current(progress, 50, 80, true, Start));
    }

    [Fact]
    public void Finished_calibration_shows_hold_then_back_to_the_limit_then_nothing()
    {
        var progress = new CalibrationProgress { LastCompletedUtc = Start };
        Assert.Equal(4, CalibrationStepper.Current(progress, 100, 80, true, Start.AddMinutes(10)));
        Assert.Equal(5, CalibrationStepper.Current(progress, 93, 80, true, Start.AddHours(3)));
        Assert.Equal(0, CalibrationStepper.Current(progress, 80, 80, true, Start.AddHours(3)));
        Assert.Equal(0, CalibrationStepper.Current(progress, 93, 100, false, Start.AddHours(3)));
        Assert.Equal(0, CalibrationStepper.Current(progress, 93, 80, true, Start.AddDays(3)));
        Assert.Equal(0, CalibrationStepper.Current(new CalibrationProgress(), 100, 80, true, Start));
        Assert.Equal(5, CalibrationStepper.StepCount);
    }

    [Fact]
    public void Days_since_the_last_calibration()
    {
        Assert.Null(CalibrationStepper.DaysSince(new CalibrationProgress(), Start));
        Assert.Equal(12, CalibrationStepper.DaysSince(new CalibrationProgress { LastCompletedUtc = Start.AddDays(-12).AddHours(-3) }, Start));
        Assert.Equal(0, CalibrationStepper.DaysSince(new CalibrationProgress { LastCompletedUtc = Start.AddHours(1) }, Start));
    }

    // ---- heat readout

    [Fact]
    public void Heat_readout_has_no_reading_without_a_temperature()
    {
        var heat = new HeatProtectionSettings { Enabled = true, MaxTempC = 42 };
        HeatReadout none = HeatReadout.From(Snap.At(50, onAc: true), heat, paused: false);
        Assert.False(none.HasReading);
        HeatReadout hot = HeatReadout.From(Snap.At(50, onAc: true, tempC: 43.5), heat, paused: true);
        Assert.True(hot.HasReading);
        Assert.Equal(42, hot.ThresholdC);
        Assert.True(hot.Paused);
    }

    // ---- daily health trend and chart data

    [Fact]
    public void Health_trend_keeps_one_point_per_day_with_the_latest_value_and_cycles()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));
        var logger = new BatteryHistoryLogger(_folder, time);
        BatterySnapshot b = Snap.At(60, onAc: true) with { HealthPercent = 91.0, CycleCount = 100 };

        Assert.True(logger.Record(b));
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.True(logger.Record(b with { HealthPercent = 90.9, CycleCount = 101 }));
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.True(logger.Record(b with { HealthPercent = 90.9, CycleCount = 101 }));
        time.Advance(TimeSpan.FromDays(1));
        Assert.True(logger.Record(b with { HealthPercent = 90.8, CycleCount = 103 }));
        time.Advance(TimeSpan.FromDays(1));
        Assert.True(logger.Record(b with { HealthPercent = null }));

        IReadOnlyList<BatteryHealthPoint> trend = logger.ReadHealthTrend();
        Assert.Equal(2, trend.Count);
        Assert.Equal(new[] { 90.9, 90.8 }, trend.Select(p => p.HealthPercent));
        Assert.Equal(new int?[] { 101, 103 }, trend.Select(p => p.CycleCount));
        Assert.Equal(new DateOnly(2026, 10, 1), trend[0].Date);
    }

    [Fact]
    public void Health_trend_survives_a_new_logger_and_a_damaged_file()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));
        var first = new BatteryHistoryLogger(_folder, time);
        first.Record(Snap.At(60, onAc: true) with { HealthPercent = 92, CycleCount = 5 });
        Assert.Single(new BatteryHistoryLogger(_folder, time).ReadHealthTrend());

        File.WriteAllText(Path.Combine(first.Folder, "health-trend.json"), "{not json");
        Assert.Empty(new BatteryHistoryLogger(_folder, time).ReadHealthTrend());
    }

    [Fact]
    public void Chart_series_averages_into_buckets_and_needs_two_samples()
    {
        DateTimeOffset now = Start;
        BatteryHistoryEntry[] entries = Enumerable.Range(0, 120)
            .Select(i => new BatteryHistoryEntry { UnixSeconds = now.AddMinutes(-120 + i).ToUnixTimeSeconds(), TemperatureC = 30 + i / 12.0 })
            .ToArray();

        IReadOnlyList<ChartPoint> points = BatteryChartBuilder.Series(entries, now, TimeSpan.FromHours(24), e => e.TemperatureC, maxPoints: 12);
        Assert.InRange(points.Count, 10, 12);
        Assert.All(points, p => Assert.InRange(p.X, 0, 1));
        Assert.True(points[^1].Y > points[0].Y);

        Assert.Empty(BatteryChartBuilder.Series(entries, now, TimeSpan.FromHours(24), e => (double?)null));
        Assert.Empty(BatteryChartBuilder.Series(entries.Take(1).ToArray(), now, TimeSpan.FromHours(24), e => e.TemperatureC));
        Assert.Empty(BatteryChartBuilder.Series(entries, now, TimeSpan.FromMinutes(1), e => e.TemperatureC));
    }

    [Fact]
    public void Health_chart_filters_by_range()
    {
        var today = new DateOnly(2026, 10, 20);
        var trend = new List<BatteryHealthPoint>
        {
            new(today.AddDays(-200), 95, 10),
            new(today.AddDays(-40), 93, 80),
            new(today.AddDays(-10), 92, 95),
            new(today, 91.5, null),
        };

        var month = BatteryChartBuilder.HealthTrend(trend, today, ChartRange.Month);
        Assert.Equal(2, month.Health.Count);
        Assert.Single(month.Cycles);
        Assert.Equal(today.AddDays(-10), month.From);

        var all = BatteryChartBuilder.HealthTrend(trend, today, ChartRange.All);
        Assert.Equal(4, all.Health.Count);
        Assert.Equal(0, all.Health[0].X);
        Assert.Equal(1, all.Health[^1].X);

        var none = BatteryChartBuilder.HealthTrend(trend, today, ChartRange.Day);
        Assert.Empty(none.Health);
        Assert.Null(none.From);
    }
}
