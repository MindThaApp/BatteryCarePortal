using Portal.Core.Battery;
using Xunit;

namespace Portal.Core.Tests;

public class FirstRunTests
{
    private static readonly PowerPlanManagerStatus NoPpm = PowerPlanManagerStatus.None;
    private static readonly PowerPlanManagerStatus PpmOwns = new(true, true, PowerPlanManagerChargeControl.Enabled);
    private static readonly PowerPlanManagerStatus PpmOff = new(true, false, PowerPlanManagerChargeControl.Disabled);

    [Fact]
    public void Supported_offers_the_charge_limit_page()
    {
        FirstRunPlan plan = FirstRunLogic.For(DeviceSupportState.Supported, NoPpm);
        Assert.Equal(FirstRunNextAction.OfferChargeLimit, plan.Action);
        Assert.Equal(new[] { FirstRunStep.Welcome, FirstRunStep.DeviceCheck, FirstRunStep.ChargeLimit, FirstRunStep.StartWithWindows, FirstRunStep.Notifications, FirstRunStep.CrashReports, FirstRunStep.Finish }, plan.Steps);
        Assert.True(plan.OffersChargeLimit);
    }

    [Fact]
    public void Supported_with_power_plan_manager_installed_but_off_still_offers_it()
    {
        Assert.True(FirstRunLogic.For(DeviceSupportState.Supported, PpmOff).OffersChargeLimit);
    }

    [Fact]
    public void Supported_while_power_plan_manager_owns_charging_gets_a_note_and_no_offer()
    {
        FirstRunPlan plan = FirstRunLogic.For(DeviceSupportState.Supported, PpmOwns);
        Assert.Equal(FirstRunNextAction.PowerPlanManagerOwnsCharging, plan.Action);
        Assert.Contains(FirstRunStep.PowerPlanManagerNote, plan.Steps);
        Assert.False(plan.OffersChargeLimit);
    }

    [Theory]
    [InlineData(DeviceSupportState.OneStepMissing, FirstRunNextAction.InstallMakerDriver)]
    [InlineData(DeviceSupportState.OnTestList, FirstRunNextAction.ConsiderBetaTesting)]
    [InlineData(DeviceSupportState.MonitorMode, FirstRunNextAction.UseReminders)]
    public void Other_states_never_offer_the_charge_limit(DeviceSupportState state, FirstRunNextAction action)
    {
        FirstRunPlan plan = FirstRunLogic.For(state, NoPpm);
        Assert.Equal(action, plan.Action);
        Assert.False(plan.OffersChargeLimit);
        Assert.DoesNotContain(FirstRunStep.PowerPlanManagerNote, plan.Steps);
        Assert.Contains(FirstRunStep.Notifications, plan.Steps);
        Assert.Equal(FirstRunStep.Finish, plan.Steps[^1]);
    }

    [Fact]
    public void No_battery_is_a_short_flow()
    {
        FirstRunPlan plan = FirstRunLogic.For(DeviceSupportState.NoBattery, NoPpm);
        Assert.Equal(FirstRunNextAction.NothingToDo, plan.Action);
        Assert.Equal(new[] { FirstRunStep.Welcome, FirstRunStep.DeviceCheck, FirstRunStep.CrashReports, FirstRunStep.Finish }, plan.Steps);
    }

    [Theory]
    [InlineData(DeviceSupportState.Supported)]
    [InlineData(DeviceSupportState.OneStepMissing)]
    [InlineData(DeviceSupportState.OnTestList)]
    [InlineData(DeviceSupportState.MonitorMode)]
    [InlineData(DeviceSupportState.NoBattery)]
    public void Every_plan_starts_with_welcome_and_device_check(DeviceSupportState state)
    {
        FirstRunPlan plan = FirstRunLogic.For(state, NoPpm);
        Assert.Equal(FirstRunStep.Welcome, plan.Steps[0]);
        Assert.Equal(FirstRunStep.DeviceCheck, plan.Steps[1]);
    }

    [Fact]
    public void Shows_once_until_completed_or_skipped()
    {
        var settings = new BatterySettings();
        Assert.True(FirstRunLogic.ShouldShow(settings));
        settings.FirstRunCompletedUtc = DateTimeOffset.UtcNow;
        Assert.False(FirstRunLogic.ShouldShow(settings.Clone()));
    }

    [Fact]
    public void Link_without_an_install_points_at_the_store_page()
    {
        PowerPlanManagerLaunch launch = PowerPlanManagerLink.Resolve(Array.Empty<string>());
        Assert.Null(launch.ProtocolUri);
        Assert.Null(launch.AppsFolderTarget);
        Assert.Equal("ms-windows-store://pdp/?ProductId=9NXJG6SXVVF2", launch.StoreUri);
    }

    [Fact]
    public void Link_uses_the_protocol_of_the_installed_edition()
    {
        PowerPlanManagerLaunch lite = PowerPlanManagerLink.Resolve(new[] { "34458MindtheApp.PowerPlanManagerLite_abc" });
        Assert.Equal("powerplanmanager-lite:", lite.ProtocolUri);
        Assert.Equal("34458MindtheApp.PowerPlanManagerLite_abc!PowerPlanTray", lite.AppsFolderTarget);

        PowerPlanManagerLaunch full = PowerPlanManagerLink.Resolve(new[] { "34458MindtheApp.PowerPlanManager_abc" });
        Assert.Equal("powerplanmanager:", full.ProtocolUri);
        Assert.Equal("34458MindtheApp.PowerPlanManager_abc!PowerPlanTray", full.AppsFolderTarget);
    }
}
