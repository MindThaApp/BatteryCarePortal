using Portal.Core.Battery;
using Xunit;

namespace Portal.Core.Tests;

public class BatteryToastTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);
    private readonly BatteryToastPlanner _planner = new();
    private readonly BatterySettings _settings = new BatterySettings().Normalize();
    private DateTimeOffset _now = T0;

    private IReadOnlyList<BatteryToast> At(int percent, bool onAc, bool charging = false, int? hold = null, int? unplugAt = null, TimeSpan? after = null)
    {
        _now += after ?? TimeSpan.FromMinutes(1);
        return _planner.Evaluate(new BatteryToastInput { Percent = percent, OnAc = onAc, Charging = charging, HoldLimit = hold, UnplugAt = unplugAt },
            _settings, _now);
    }

    private static TopUpCheckResult Ended(ChargeActionStatus status, TopUpEndReason reason = TopUpEndReason.Full) =>
        new(reason, new ChargeActionResult(status, ""), 80);

    [Fact]
    public void Limit_reached_fires_once_after_charging_up_to_the_limit()
    {
        Assert.Empty(At(70, onAc: true, charging: true, hold: 80));
        BatteryToast toast = Assert.Single(At(80, onAc: true, charging: false, hold: 80));
        Assert.Equal(BatteryToastKind.LimitReached, toast.Kind);
        Assert.Equal(80, toast.Percent);
        Assert.Equal(80, toast.Limit);
        Assert.Empty(At(80, onAc: true, hold: 80, after: TimeSpan.FromHours(2)));
    }

    [Fact]
    public void Charging_that_stops_just_below_the_limit_counts_as_holding()
    {
        At(55, onAc: true, charging: true, hold: 60);
        Assert.Equal(BatteryToastKind.LimitReached, Assert.Single(At(59, onAc: true, charging: false, hold: 60)).Kind);
    }

    [Fact]
    public void Still_charging_past_the_limit_is_not_holding()
    {
        At(70, onAc: true, charging: true, hold: 80);
        Assert.Empty(At(80, onAc: true, charging: true, hold: 80));
        Assert.Empty(At(84, onAc: true, charging: true, hold: 80));
        Assert.Equal(BatteryToastKind.LimitReached, Assert.Single(At(80, onAc: true, charging: false, hold: 80)).Kind);
    }

    [Fact]
    public void Limit_reached_needs_a_charge_up_first_so_a_start_at_the_limit_is_quiet()
    {
        Assert.Empty(At(80, onAc: true, hold: 80));
        Assert.Empty(At(81, onAc: true, hold: 80));
    }

    [Fact]
    public void Limit_reached_does_not_repeat_within_thirty_minutes()
    {
        At(70, onAc: true, charging: true, hold: 80);
        Assert.Single(At(80, onAc: true, hold: 80));
        At(70, onAc: true, charging: true, hold: 80, after: TimeSpan.FromMinutes(5));
        Assert.Empty(At(80, onAc: true, hold: 80, after: TimeSpan.FromMinutes(5)));
        At(70, onAc: true, charging: true, hold: 80, after: TimeSpan.FromMinutes(25));
        Assert.Single(At(80, onAc: true, hold: 80));
    }

    [Fact]
    public void No_limit_reached_during_a_top_up_or_when_switched_off()
    {
        At(70, onAc: true, charging: true, hold: null);
        Assert.Empty(At(80, onAc: true, hold: null));

        _settings.NotifyWhenLimitReached = false;
        At(70, onAc: true, charging: true, hold: 80);
        Assert.Empty(At(80, onAc: true, hold: 80));
    }

    [Fact]
    public void Turning_a_toast_on_does_not_fire_a_stale_event()
    {
        _settings.NotifyWhenLimitReached = false;
        At(70, onAc: true, charging: true, hold: 80);
        Assert.Empty(At(80, onAc: true, hold: 80));
        _settings.NotifyWhenLimitReached = true;
        Assert.Empty(At(80, onAc: true, hold: 80));
    }

    [Fact]
    public void Unplug_reminder_in_monitor_mode()
    {
        Assert.Empty(At(75, onAc: true, charging: true, unplugAt: 80));
        BatteryToast toast = Assert.Single(At(80, onAc: true, charging: true, unplugAt: 80));
        Assert.Equal(BatteryToastKind.Unplug, toast.Kind);
        Assert.Empty(At(85, onAc: true, charging: true, unplugAt: 80));
    }

    [Fact]
    public void Plugging_in_above_the_unplug_level_is_quiet()
    {
        Assert.Empty(At(85, onAc: false, unplugAt: 80));
        Assert.Empty(At(85, onAc: true, charging: true, unplugAt: 80));
    }

    [Fact]
    public void Plug_in_fires_once_on_battery_at_the_low_level_and_rearms_after_charging()
    {
        Assert.Empty(At(25, onAc: false));
        BatteryToast toast = Assert.Single(At(20, onAc: false));
        Assert.Equal(BatteryToastKind.PlugIn, toast.Kind);
        Assert.Empty(At(15, onAc: false, after: TimeSpan.FromHours(1)));

        Assert.Empty(At(16, onAc: true, charging: true));
        Assert.Equal(BatteryToastKind.PlugIn, Assert.Single(At(16, onAc: false)).Kind);
    }

    [Fact]
    public void Plug_in_respects_its_setting_and_level()
    {
        _settings.NotifyPlugIn = false;
        Assert.Empty(At(10, onAc: false));

        var planner = new BatteryToastPlanner();
        var settings = new BatterySettings { LowReminderPercent = 30 }.Normalize();
        Assert.Single(planner.Evaluate(new BatteryToastInput { Percent = 30, OnAc = false }, settings, T0));
    }

    [Fact]
    public void Unknown_level_fires_nothing() =>
        Assert.Empty(_planner.Evaluate(new BatteryToastInput { Percent = null, OnAc = false, HoldLimit = 80 }, _settings, T0));

    [Fact]
    public void Top_up_finished_only_when_the_limit_is_back_on()
    {
        BatteryToast? full = _planner.TopUpEnded(Ended(ChargeActionStatus.Applied), 100, _settings, T0);
        Assert.NotNull(full);
        Assert.Equal(BatteryToastKind.TopUpFinished, full!.Kind);
        Assert.True(full.Full);
        Assert.Equal(80, full.Limit);

        var planner = new BatteryToastPlanner();
        Assert.Null(planner.TopUpEnded(Ended(ChargeActionStatus.NeedsElevation), 100, _settings, T0));
        Assert.Null(planner.TopUpEnded(Ended(ChargeActionStatus.Failed), 100, _settings, T0));
        BatteryToast? expired = planner.TopUpEnded(Ended(ChargeActionStatus.Applied, TopUpEndReason.Expired), 93, _settings, T0);
        Assert.False(expired!.Full);
        Assert.Equal(93, expired.Percent);
    }

    [Fact]
    public void Top_up_finished_respects_its_setting_and_the_gap()
    {
        Assert.NotNull(_planner.TopUpEnded(Ended(ChargeActionStatus.Applied), 100, _settings, T0));
        Assert.Null(_planner.TopUpEnded(Ended(ChargeActionStatus.Applied), 100, _settings, T0.AddMinutes(10)));

        _settings.NotifyTopUpFinished = false;
        Assert.Null(new BatteryToastPlanner().TopUpEnded(Ended(ChargeActionStatus.Applied), 100, _settings, T0));
    }

    [Fact]
    public void New_settings_default_to_on()
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<BatterySettings>("{}")!;
        Assert.True(s.NotifyWhenLimitReached);
        Assert.True(s.NotifyPlugIn);
        Assert.True(s.NotifyTopUpFinished);
    }
}
