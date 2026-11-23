using Portal.Core.Battery;
using Portal.Core.Services;
using Xunit;

namespace Portal.Core.Tests;

// Shares the mutable FeatureTiers.EverythingFree switch with the other tests in this collection.
[Collection("FeatureTiers")]
public class HistoryThrottleTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bcp-throttle-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private static readonly TimeSpan Minute = TimeSpan.FromSeconds(60);

    [Fact]
    public void Same_state_waits_for_the_interval()
    {
        BatterySnapshot s = Snap.At(60, onAc: true, charging: true);
        Assert.False(BatteryHistoryLogger.ShouldRecord(TimeSpan.FromSeconds(30), (true, true), s, Minute));
        Assert.True(BatteryHistoryLogger.ShouldRecord(TimeSpan.FromSeconds(60), (true, true), s, Minute));
    }

    [Fact]
    public void A_plug_or_charging_change_records_early_but_not_in_a_flap()
    {
        BatterySnapshot unplugged = Snap.At(60, onAc: false, charging: false);
        Assert.True(BatteryHistoryLogger.ShouldRecord(TimeSpan.FromSeconds(10), (true, true), unplugged, Minute));
        Assert.False(BatteryHistoryLogger.ShouldRecord(TimeSpan.FromSeconds(1), (true, true), unplugged, Minute));
        BatterySnapshot stopped = Snap.At(80, onAc: true, charging: false);
        Assert.True(BatteryHistoryLogger.ShouldRecord(TimeSpan.FromSeconds(10), (true, true), stopped, Minute));
    }

    [Fact]
    public void Logger_writes_the_unplug_inside_the_interval_so_the_today_log_sees_it()
    {
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(now);
        var logger = new BatteryHistoryLogger(_folder, time);
        Assert.True(logger.Record(Snap.At(60, onAc: true, charging: false)));
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.False(logger.Record(Snap.At(60, onAc: true, charging: false)));
        Assert.True(logger.Record(Snap.At(60, onAc: false, charging: false)));

        IReadOnlyList<TodayEvent> events = TodayLog.Build(logger.Read(now.AddHours(-1), now.AddHours(1)), TimeZoneInfo.Utc);
        Assert.Contains(events, e => e.Kind == TodayEventKind.Unplugged);
    }

    [Fact]
    public void Retention_is_thirty_days_while_everything_is_free()
    {
        Assert.Equal(30, FeatureTiers.HistoryRetentionDays(hasPro: false));
        Assert.Equal(BatteryHistoryLogger.DefaultRetentionDays, FeatureTiers.ProHistoryDays);
    }

    [Theory]
    [InlineData("Simulated", "Simulated laptop", "Simulated laptop")]
    [InlineData("LENOVO", "Yoga Slim 7", "LENOVO Yoga Slim 7")]
    [InlineData("Dell Inc.", "Dell Inc. XPS", "Dell Inc. XPS")]
    [InlineData("", "Yoga", "Yoga")]
    [InlineData("Dell", "", "Dell")]
    public void Device_name_does_not_repeat_the_vendor(string vendor, string model, string expected) =>
        Assert.Equal(expected, DeviceNameText.Join(vendor, model));
}
