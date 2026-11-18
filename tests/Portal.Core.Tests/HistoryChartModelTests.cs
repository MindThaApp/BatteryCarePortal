using Portal.Core.Battery;
using Portal.Core.Services;
using Xunit;

namespace Portal.Core.Tests;

public class HistoryChartModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static BatteryHistoryEntry E(DateTimeOffset t, int? percent, double? watts = null) =>
        new() { UnixSeconds = t.ToUnixTimeSeconds(), Percent = percent, BatteryW = watts };

    [Fact]
    public void Day_range_has_96_quarter_hour_buckets_over_24_hours()
    {
        HistorySeries s = HistoryShaping.Build(Array.Empty<BatteryHistoryEntry>(), HistoryRange.Day, Now, HistoryShaping.Level);
        Assert.Equal(96, s.Buckets.Count);
        Assert.Equal(Now.AddHours(-24), s.From);
        Assert.False(s.HasData);
        Assert.Null(s.Min);
        Assert.Empty(s.Segments());
    }

    [Theory]
    [InlineData(HistoryRange.Week, 168)]
    [InlineData(HistoryRange.Month, 120)]
    public void Longer_ranges_use_coarser_buckets(HistoryRange range, int count) =>
        Assert.Equal(count, HistoryShaping.Build(Array.Empty<BatteryHistoryEntry>(), range, Now, HistoryShaping.Level).Buckets.Count);

    [Fact]
    public void Samples_are_averaged_per_bucket_with_min_and_max()
    {
        DateTimeOffset t = Now.AddHours(-1);
        var entries = new[] { E(t, 50), E(t.AddMinutes(2), 60), E(t.AddMinutes(4), 70) };
        HistorySeries s = HistoryShaping.Build(entries, HistoryRange.Day, Now, HistoryShaping.Level);
        HistoryBucket b = s.Buckets.Single(x => x.HasData);
        Assert.Equal(3, b.Count);
        Assert.Equal(60, b.Average);
        Assert.Equal(50, b.Min);
        Assert.Equal(70, b.Max);
        Assert.Equal(50, s.Min);
        Assert.Equal(70, s.Max);
        Assert.Equal(70, s.Last);
        Assert.Equal(3, s.SampleCount);
    }

    [Fact]
    public void Samples_outside_the_window_or_without_a_value_are_ignored()
    {
        var entries = new[] { E(Now.AddHours(-25), 10), E(Now.AddMinutes(1), 20), E(Now.AddHours(-2), null), E(Now.AddHours(-3), 40) };
        HistorySeries s = HistoryShaping.Build(entries, HistoryRange.Day, Now, HistoryShaping.Level);
        Assert.Equal(1, s.SampleCount);
        Assert.Equal(40, s.Last);
    }

    [Fact]
    public void An_empty_stretch_splits_the_line_into_segments()
    {
        var entries = new[]
        {
            E(Now.AddHours(-10), 50), E(Now.AddHours(-9.8), 52),
            E(Now.AddHours(-5), 80),
            E(Now.AddHours(-2), 81), E(Now.AddHours(-1.8), 82),
        };
        IReadOnlyList<HistorySegment> segments = HistoryShaping.Build(entries, HistoryRange.Day, Now, HistoryShaping.Level).Segments();
        Assert.Equal(3, segments.Count);
        Assert.All(segments, seg => Assert.All(seg.Buckets, b => Assert.True(b.HasData)));
    }

    [Fact]
    public void Watts_keep_their_sign_and_use_the_battery_value()
    {
        var entries = new[] { E(Now.AddHours(-2), 50, -12.5), E(Now.AddHours(-1), 55, 30) };
        HistorySeries s = HistoryShaping.Build(entries, HistoryRange.Day, Now, HistoryShaping.Watts);
        Assert.Equal(-12.5, s.Min);
        Assert.Equal(30, s.Max);
    }

    [Fact]
    public void Level_scale_is_always_zero_to_one_hundred()
    {
        HistoryScale scale = HistoryShaping.LevelScale();
        Assert.Equal(0, scale.Low);
        Assert.Equal(100, scale.High);
        Assert.Equal(0.5, scale.Fraction(50));
    }

    [Theory]
    [InlineData(-12.5, 30.0, -20, 30)]
    [InlineData(null, null, 0, 5)]
    [InlineData(2.0, 7.0, 0, 10)]
    [InlineData(-40.0, 15.0, -40, 20)]
    public void Watts_scale_includes_zero_and_rounds_out(double? min, double? max, double low, double high)
    {
        HistoryScale s = HistoryShaping.WattsScale(min, max);
        Assert.Equal(low, s.Low);
        Assert.Equal(high, s.High);
    }

    [Fact]
    public void Table_rows_are_newest_first_and_skip_gaps()
    {
        var entries = new[] { E(Now.AddHours(-10), 50), E(Now.AddHours(-2), 60) };
        IReadOnlyList<HistoryTableRow> rows = HistoryShaping.TableRows(HistoryShaping.Build(entries, HistoryRange.Day, Now, HistoryShaping.Level));
        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].Start > rows[1].Start);
        Assert.Equal(60, rows[0].Average);
    }

    [Fact]
    public void Nearest_picks_a_bucket_with_data()
    {
        var entries = new[] { E(Now.AddHours(-20), 50), E(Now.AddHours(-1), 90) };
        HistorySeries s = HistoryShaping.Build(entries, HistoryRange.Day, Now, HistoryShaping.Level);
        Assert.Equal(50, HistoryShaping.Nearest(s, 0.1)!.Average);
        Assert.Equal(90, HistoryShaping.Nearest(s, 0.99)!.Average);
        Assert.Null(HistoryShaping.Nearest(HistoryShaping.Build(Array.Empty<BatteryHistoryEntry>(), HistoryRange.Day, Now, HistoryShaping.Level), 0.5));
    }

    [Fact]
    public void Recording_that_began_late_is_reported_so_the_page_can_say_so()
    {
        DateTimeOffset from = Now.AddHours(-24);
        TimeSpan size = TimeSpan.FromMinutes(15);
        Assert.True(HistoryShaping.StartedInsideWindow(Now.AddHours(-3), from, size));
        Assert.False(HistoryShaping.StartedInsideWindow(Now.AddHours(-23.9), from, size));
        Assert.False(HistoryShaping.StartedInsideWindow(null, from, size));
    }

    [Fact]
    public void Windows_follow_the_tier_config()
    {
        Assert.Equal(new[] { HistoryRange.Day }, HistoryShaping.AvailableRanges(FeatureTiers.FreeHistoryWindowDays));
        Assert.Equal(new[] { HistoryRange.Day, HistoryRange.Week, HistoryRange.Month }, HistoryShaping.AvailableRanges(FeatureTiers.ProHistoryDays));
        Assert.Equal(FeatureTiers.ProHistoryDays, FeatureTiers.HistoryWindowDays(false));
    }

    [Fact]
    public void Simulated_history_is_used_only_in_fake_mode_with_little_recorded_data()
    {
        Assert.True(HistoryShaping.UseSimulatedEntries(true, 5));
        Assert.False(HistoryShaping.UseSimulatedEntries(true, 500));
        Assert.False(HistoryShaping.UseSimulatedEntries(false, 0));
        Assert.True(HistoryShaping.UseSimulatedHealth(true, 1));
        Assert.False(HistoryShaping.UseSimulatedHealth(false, 0));
    }

    [Fact]
    public void Simulated_entries_fill_the_window_and_stay_in_range()
    {
        IReadOnlyList<BatteryHistoryEntry> list = HistoryShaping.SimulatedEntries(Now, TimeSpan.FromDays(7), TimeSpan.FromMinutes(10));
        Assert.All(list, e => Assert.InRange(e.Percent!.Value, 0, 100));
        HistorySeries s = HistoryShaping.Build(list, HistoryRange.Week, Now, HistoryShaping.Level);
        Assert.All(s.Buckets, b => Assert.True(b.HasData));
    }

    [Fact]
    public void Health_trend_reports_first_last_and_change()
    {
        var points = new[]
        {
            new BatteryHealthPoint(new DateOnly(2026, 11, 10), 91.7, 212),
            new BatteryHealthPoint(new DateOnly(2026, 9, 1), 92.4, 190),
        };
        HealthTrendModel m = HealthTrendModel.Build(points);
        Assert.Equal(92.4, m.First);
        Assert.Equal(91.7, m.Last);
        Assert.Equal(-0.7, m.Change!.Value, 6);
        Assert.Equal(212, m.Cycles);
        Assert.Equal(0, m.DayFraction(new DateOnly(2026, 9, 1)));
        Assert.Equal(1, m.DayFraction(new DateOnly(2026, 11, 10)));
        Assert.False(HealthTrendModel.Build(Array.Empty<BatteryHealthPoint>()).HasData);
        Assert.Equal(0.5, HealthTrendModel.Build(points.Take(1)).DayFraction(new DateOnly(2026, 11, 10)));
    }

    [Fact]
    public void Simulated_health_is_ordered_and_small()
    {
        IReadOnlyList<BatteryHealthPoint> list = HistoryShaping.SimulatedHealth(new DateOnly(2026, 11, 18));
        Assert.True(list.Count >= 3);
        Assert.Equal(list.OrderBy(p => p.Date).ToList(), list.ToList());
        Assert.InRange(list[0].HealthPercent - list[^1].HealthPercent, 0.5, 1);
    }
}
