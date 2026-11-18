using Portal.Core.Battery;
using Xunit;

namespace Portal.Core.Tests;

public class TopUpTests : IDisposable
{
    private static readonly ChargeLimitCapabilities Continuous = new() { Kind = ChargeLimitKind.ContinuousRange, MinLimit = 50, MaxLimit = 100 };
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bcp-topup-test-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new(Start);
    private readonly ChargeWriteLock _lock = new();

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    private ChargeLimitController New(IChargeLimitBackend backend) =>
        new(backend, new OriginalStateStore(_folder), new ChargeIntentStore(_folder), _time, _lock);

    private static ChargeIntent Running(DateTimeOffset? until, bool needsReapply = false, bool failed = false) => new()
    {
        BackendId = "test", Managed = true, LimitOn = true, Limit = 80, TopUpActive = true, TopUpUntil = until,
        NeedsReapply = needsReapply, ReapplyFailed = failed,
    };

    [Fact]
    public void Top_up_ends_when_full()
    {
        Assert.Equal(TopUpEndReason.Full, TopUpPolicy.ShouldEnd(Running(Start.AddHours(8)), 100, Start));
        Assert.Equal(TopUpEndReason.None, TopUpPolicy.ShouldEnd(Running(Start.AddHours(8)), 99, Start));
    }

    [Fact]
    public void Top_up_ends_when_expired()
    {
        Assert.Equal(TopUpEndReason.Expired, TopUpPolicy.ShouldEnd(Running(Start.AddHours(8)), 90, Start.AddHours(8)));
        Assert.Equal(TopUpEndReason.None, TopUpPolicy.ShouldEnd(Running(Start.AddHours(8)), 90, Start.AddHours(7.9)));
        Assert.Equal(TopUpEndReason.Expired, TopUpPolicy.ShouldEnd(Running(Start.AddHours(8)), null, Start.AddHours(9)));
        Assert.Equal(TopUpEndReason.Expired, TopUpPolicy.ShouldEnd(Running(until: null), 50, Start));
    }

    [Fact]
    public void Nothing_ends_without_a_running_top_up_or_while_the_user_has_to_act()
    {
        Assert.Equal(TopUpEndReason.None, TopUpPolicy.ShouldEnd(null, 100, Start));
        Assert.Equal(TopUpEndReason.None, TopUpPolicy.ShouldEnd(Running(Start) with { TopUpActive = false }, 100, Start));
        Assert.Equal(TopUpEndReason.None, TopUpPolicy.ShouldEnd(Running(Start) with { LimitOn = false, Limit = 100 }, 100, Start));
        Assert.Equal(TopUpEndReason.None, TopUpPolicy.ShouldEnd(Running(Start, needsReapply: true), 100, Start));
        Assert.Equal(TopUpEndReason.None, TopUpPolicy.ShouldEnd(Running(Start, failed: true), 100, Start));
    }

    [Fact]
    public void Top_up_is_offered_only_when_the_apps_limit_is_on_and_in_place()
    {
        var on = new ChargeIntent { BackendId = "test", Managed = true, LimitOn = true, Limit = 80 };
        Assert.True(TopUpPolicy.CanStart(on, 80, 60, true));
        Assert.True(TopUpPolicy.CanStart(on, 80, null, true));
        Assert.False(TopUpPolicy.CanStart(on, 100, 60, true));
        Assert.False(TopUpPolicy.CanStart(on, null, 60, true));
        Assert.False(TopUpPolicy.CanStart(on, 80, 100, true));
        Assert.False(TopUpPolicy.CanStart(null, 80, 60, true));
        Assert.False(TopUpPolicy.CanStart(on with { Managed = false }, 80, 60, true));
        Assert.False(TopUpPolicy.CanStart(on with { TopUpActive = true }, 80, 60, true));
        Assert.False(TopUpPolicy.CanStart(on with { NeedsReapply = true }, 80, 60, true));
        Assert.False(TopUpPolicy.CanStart(on with { ReapplyFailed = true }, 80, 60, true));
    }

    [Fact]
    public void Top_up_is_not_offered_where_it_could_not_end_by_itself() =>
        Assert.False(TopUpPolicy.CanStart(new ChargeIntent { BackendId = "test", Managed = true, LimitOn = true, Limit = 80 }, 80, 60, false));

    [Fact]
    public void Top_up_is_not_offered_when_the_firmware_shows_another_limit() =>
        Assert.False(TopUpPolicy.CanStart(new ChargeIntent { BackendId = "test", Managed = true, LimitOn = true, Limit = 80 }, 70, 60, true));

    [Fact]
    public void Stop_top_up_is_offered_whenever_a_top_up_is_saved_as_running()
    {
        Assert.True(TopUpPolicy.CanStop(Running(Start)));
        Assert.True(TopUpPolicy.CanStop(Running(Start, needsReapply: true)));
        Assert.False(TopUpPolicy.CanStop(Running(Start) with { TopUpActive = false }));
        Assert.False(TopUpPolicy.CanStop(null));
    }

    [Fact]
    public void Default_top_up_lasts_at_most_eight_hours() => Assert.Equal(TimeSpan.FromHours(8), TopUpPolicy.MaxDuration);

    [Fact]
    public async Task Full_battery_ends_the_top_up_and_puts_the_limit_back_through_the_controller()
    {
        var backend = new TestBackend(Continuous);
        ChargeLimitController controller = New(backend);
        Assert.True((await controller.SetLimitAsync(true, 80)).Succeeded);
        Assert.True((await controller.StartTopUpAsync(TopUpPolicy.MaxDuration)).Succeeded);
        Assert.Equal(100, backend.Limit);

        var monitor = new TopUpMonitor(controller, _time);
        Assert.Null(await monitor.CheckAsync(95));
        Assert.Equal(100, backend.Limit);

        TopUpCheckResult? ended = await monitor.CheckAsync(100);
        Assert.NotNull(ended);
        Assert.Equal(TopUpEndReason.Full, ended!.Reason);
        Assert.True(ended.Result.Succeeded);
        Assert.False(ended.NeedsUser);
        Assert.Equal(80, ended.Limit);
        Assert.Equal(80, backend.Limit);
        Assert.Equal(80, backend.Applied[^1].Limit);
        Assert.False(backend.Applied[^1].AllowElevationPrompt);
        Assert.False(controller.LoadIntent()!.TopUpActive);

        Assert.Null(await monitor.CheckAsync(100));
    }

    [Fact]
    public async Task Expired_top_up_puts_the_limit_back()
    {
        var backend = new TestBackend(Continuous);
        ChargeLimitController controller = New(backend);
        await controller.SetLimitAsync(true, 70);
        await controller.StartTopUpAsync(TopUpPolicy.MaxDuration);

        var monitor = new TopUpMonitor(controller, _time);
        _time.Advance(TimeSpan.FromHours(7));
        Assert.Null(await monitor.CheckAsync(85));
        _time.Advance(TimeSpan.FromHours(1));
        TopUpCheckResult? ended = await monitor.CheckAsync(88);

        Assert.Equal(TopUpEndReason.Expired, ended!.Reason);
        Assert.Equal(70, backend.Limit);
        Assert.False(controller.LoadIntent()!.TopUpActive);
    }

    private ChargeIntent? Saved() => new ChargeIntentStore(_folder).Load(out ChargeIntent? i) == AtomicJsonFile.ReadStatus.Ok ? i : null;

    private async Task<(TestBackend Backend, ChargeLimitController Controller)> LimitOnAt80()
    {
        var backend = new TestBackend(Continuous);
        ChargeLimitController controller = New(backend);
        Assert.True((await controller.SetLimitAsync(true, 80)).Succeeded);
        return (backend, controller);
    }

    [Fact]
    public async Task Top_up_never_starts_on_a_backend_that_needs_elevation()
    {
        var backend = new TestBackend(Continuous, requiresElevation: true);
        ChargeLimitController controller = New(backend);
        Assert.True((await controller.SetLimitAsync(true, 80)).Succeeded);
        int writes = backend.Applied.Count;

        Assert.False(controller.TopUpSupported);
        ChargeActionResult result = await controller.StartTopUpAsync(TopUpPolicy.MaxDuration);
        Assert.False(result.Succeeded);
        Assert.True(result.NotAttempted);
        Assert.Equal(writes, backend.Applied.Count);
        Assert.Equal(80, backend.Limit);
        Assert.False(Saved()!.TopUpActive);
    }

    [Fact]
    public async Task Top_up_does_not_start_when_the_in_process_write_is_denied()
    {
        var driver = new FakeEnergyDriver();
        var wmi = new FakeWmiReader();
        wmi.Add("Win32_ComputerSystem", new() { ["Manufacturer"] = "LENOVO", ["SystemFamily"] = "IdeaPad 5", ["Model"] = "82FG" });
        var executor = new RecordingExecutor();
        var backend = new Portal.Core.Battery.Backends.LenovoConservationBackend(wmi, executor, driver);
        Assert.True(backend.Detect());
        ChargeLimitController controller = New(backend);
        Assert.True((await controller.SetLimitAsync(true)).Succeeded);
        Assert.True(controller.TopUpSupported);

        driver.FailWritesWith = Portal.Core.Battery.Backends.DeviceIoResult.ErrorAccessDenied;
        ChargeActionResult result = await controller.StartTopUpAsync(TopUpPolicy.MaxDuration);
        Assert.Equal(ChargeActionStatus.NeedsElevation, result.Status);
        Assert.True(result.NotAttempted);
        Assert.Empty(executor.Plans);
        Assert.True(Portal.Core.Battery.Backends.LenovoConservationBackend.IsConservationOn(driver.ModeWord));
        Assert.False(Saved()!.TopUpActive);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task Top_up_does_not_start_while_anything_is_pending(bool topUp, bool needsReapply, bool failed)
    {
        var (backend, controller) = await LimitOnAt80();
        ChargeIntent pending = Saved()! with { TopUpActive = topUp, TopUpUntil = topUp ? Start.AddHours(1) : null, NeedsReapply = needsReapply, ReapplyFailed = failed };
        Assert.True(new ChargeIntentStore(_folder).TrySave(pending));
        int writes = backend.Applied.Count;

        ChargeActionResult result = await controller.StartTopUpAsync(TopUpPolicy.MaxDuration);
        Assert.False(result.Succeeded);
        Assert.True(result.NotAttempted);
        Assert.Equal(writes, backend.Applied.Count);
        Assert.Equal(pending, Saved());
    }

    [Fact]
    public async Task Top_up_does_not_start_when_the_firmware_shows_another_limit()
    {
        var (backend, controller) = await LimitOnAt80();
        backend.Limit = 70;
        int writes = backend.Applied.Count;

        ChargeActionResult result = await controller.StartTopUpAsync(TopUpPolicy.MaxDuration);
        Assert.True(result.NotAttempted);
        Assert.Equal(writes, backend.Applied.Count);
        Assert.Equal(70, backend.Limit);
        Assert.False(Saved()!.TopUpActive);
    }

    [Fact]
    public async Task Started_top_up_saves_a_fresh_intent_and_never_prompts()
    {
        var (backend, controller) = await LimitOnAt80();
        Assert.True((await controller.StartTopUpAsync(TimeSpan.FromHours(2))).Succeeded);
        Assert.False(backend.Applied[^1].AllowElevationPrompt);
        ChargeIntent saved = Saved()!;
        Assert.Equal(new ChargeIntent
        {
            BackendId = "test", Managed = true, LimitOn = true, Limit = 80, TopUpActive = true, TopUpUntil = Start.AddHours(2), UpdatedUtc = Start,
        }, saved);
    }

    [Fact]
    public async Task Error_after_the_unattended_write_started_is_never_retried_and_tells_the_user()
    {
        var (backend, controller) = await LimitOnAt80();
        Assert.True((await controller.StartTopUpAsync(TopUpPolicy.MaxDuration)).Succeeded);
        backend.WriteThrows = new IOException("device gone");
        int writes = backend.Applied.Count;

        var monitor = new TopUpMonitor(controller, _time);
        TopUpCheckResult? ended = await monitor.CheckAsync(100);
        Assert.Equal(ChargeActionStatus.Failed, ended!.Result.Status);
        Assert.False(ended.Result.NotAttempted);
        Assert.True(ended.NeedsUser);
        Assert.Equal(writes + 1, backend.Applied.Count);
        ChargeIntent saved = Saved()!;
        Assert.True(saved.ReapplyFailed);
        Assert.False(saved.TopUpActive);

        Assert.Null(await monitor.CheckAsync(100));
        Assert.Null(await new TopUpMonitor(controller, _time).CheckAsync(100));
        Assert.Equal(writes + 1, backend.Applied.Count);
    }

    [Fact]
    public async Task Error_before_the_unattended_write_leaves_it_pending_and_tells_the_user()
    {
        var (backend, controller) = await LimitOnAt80();
        Assert.True((await controller.StartTopUpAsync(TopUpPolicy.MaxDuration)).Succeeded);
        backend.ReadThrows = new IOException("busy");
        int writes = backend.Applied.Count;

        TopUpCheckResult? ended = await new TopUpMonitor(controller, _time).CheckAsync(100);
        Assert.Equal(ChargeActionStatus.Failed, ended!.Result.Status);
        Assert.True(ended.Result.NotAttempted);
        Assert.True(ended.NeedsUser);
        Assert.Equal(writes, backend.Applied.Count);
        Assert.True(Saved()!.NeedsReapply);
        Assert.False(Saved()!.ReapplyFailed);
    }

    [Fact]
    public async Task Unwritable_intent_file_never_makes_the_monitor_write_twice()
    {
        var (backend, controller) = await LimitOnAt80();
        Assert.True((await controller.StartTopUpAsync(TopUpPolicy.MaxDuration)).Succeeded);
        int writes = backend.Applied.Count;

        var monitor = new TopUpMonitor(controller, _time);
        // Readable but not replaceable: every save of the intent fails.
        using (new FileStream(Path.Combine(_folder, ChargeIntentStore.FileName), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.NotNull(await monitor.CheckAsync(100));
            Assert.True(Saved()!.TopUpActive);
            _time.Advance(TimeSpan.FromSeconds(30));
            Assert.Null(await monitor.CheckAsync(100));
            Assert.Null(await monitor.CheckAsync(100));
        }
        Assert.Equal(writes + 1, backend.Applied.Count);
    }

    [Fact]
    public async Task Unattended_end_keeps_a_limit_another_tool_set_during_the_top_up()
    {
        var (backend, controller) = await LimitOnAt80();
        Assert.True((await controller.StartTopUpAsync(TopUpPolicy.MaxDuration)).Succeeded);
        backend.Limit = 70;
        int writes = backend.Applied.Count;

        TopUpCheckResult? ended = await new TopUpMonitor(controller, _time).CheckAsync(100);
        Assert.Equal(ChargeActionStatus.ChangedElsewhere, ended!.Result.Status);
        Assert.False(ended.NeedsUser);
        Assert.Equal(writes, backend.Applied.Count);
        Assert.Equal(70, backend.Limit);
        Assert.False(Saved()!.TopUpActive);
    }

    [Fact]
    public async Task Saved_top_up_on_an_elevated_backend_never_prompts_and_tells_once()
    {
        var backend = new TestBackend(Continuous, requiresElevation: true);
        ChargeLimitController controller = New(backend);
        Assert.True((await controller.SetLimitAsync(true, 80)).Succeeded);
        // A top-up saved by an older build; this backend can no longer start one.
        Assert.True(new ChargeIntentStore(_folder).TrySave(Saved()! with { TopUpActive = true, TopUpUntil = Start.AddHours(8) }));
        backend.Limit = 100;
        int writes = backend.Applied.Count;

        var monitor = new TopUpMonitor(controller, _time);
        TopUpCheckResult? ended = await monitor.CheckAsync(100);

        Assert.Equal(ChargeActionStatus.NeedsElevation, ended!.Result.Status);
        Assert.True(ended.NeedsUser);
        Assert.Equal(writes, backend.Applied.Count);
        Assert.Equal(100, backend.Limit);
        ChargeIntent intent = controller.LoadIntent()!;
        Assert.True(intent.NeedsReapply);
        Assert.True(intent.LimitOn);

        Assert.Null(await monitor.CheckAsync(100));
        Assert.Equal(writes, backend.Applied.Count);
    }

    [Fact]
    public async Task Monitor_never_writes_when_the_limit_is_meant_to_be_off()
    {
        var backend = new TestBackend(Continuous);
        ChargeLimitController controller = New(backend);
        await controller.SetLimitAsync(false);
        int writes = backend.Applied.Count;

        Assert.Equal(ChargeActionStatus.NothingToDo, (await controller.StartTopUpAsync(TopUpPolicy.MaxDuration)).Status);
        var monitor = new TopUpMonitor(controller, _time);
        _time.Advance(TimeSpan.FromHours(9));
        Assert.Null(await monitor.CheckAsync(100));
        Assert.Equal(writes, backend.Applied.Count);
        Assert.Equal(100, backend.Limit);
    }

    [Fact]
    public async Task Stop_top_up_puts_the_limit_back()
    {
        var backend = new TestBackend(Continuous);
        ChargeLimitController controller = New(backend);
        await controller.SetLimitAsync(true, 80);
        await controller.StartTopUpAsync(TopUpPolicy.MaxDuration);

        Assert.True((await controller.EndTopUpAsync()).Succeeded);
        Assert.Equal(80, backend.Limit);
        Assert.Null(await new TopUpMonitor(controller, _time).CheckAsync(100));
    }

    [Fact]
    public void Hot_counter_grows_only_while_a_top_up_runs_and_the_action_is_on()
    {
        Assert.Equal(1, TopUpPolicy.NextHotTicks(0, true, true, 41, 40));
        Assert.Equal(2, TopUpPolicy.NextHotTicks(1, true, true, 40, 40));
        Assert.Equal(0, TopUpPolicy.NextHotTicks(1, true, true, 39.9, 40));
        Assert.Equal(0, TopUpPolicy.NextHotTicks(1, true, true, null, 40));
        Assert.Equal(0, TopUpPolicy.NextHotTicks(1, false, true, 50, 40));
        Assert.Equal(0, TopUpPolicy.NextHotTicks(1, true, false, 50, 40));
    }

    [Fact]
    public void Heat_ends_a_top_up_only_after_two_hot_checks_and_after_full_or_expiry_are_considered()
    {
        Assert.Equal(TopUpEndReason.None, TopUpPolicy.ShouldEnd(Running(Start.AddHours(8)), 80, Start, 1));
        Assert.Equal(TopUpEndReason.TooHot, TopUpPolicy.ShouldEnd(Running(Start.AddHours(8)), 80, Start, 2));
        Assert.Equal(TopUpEndReason.Full, TopUpPolicy.ShouldEnd(Running(Start.AddHours(8)), 100, Start, 5));
        Assert.Equal(TopUpEndReason.None, TopUpPolicy.ShouldEnd(Running(Start.AddHours(8), needsReapply: true), 80, Start, 5));
        Assert.Equal(TopUpEndReason.None, TopUpPolicy.ShouldEnd(null, 80, Start, 5));
    }

    [Fact]
    public async Task Two_hot_checks_in_a_row_end_the_top_up_through_the_unattended_path()
    {
        var backend = new TestBackend(Continuous);
        ChargeLimitController controller = New(backend);
        await controller.SetLimitAsync(true, 80);
        await controller.StartTopUpAsync(TopUpPolicy.MaxDuration);
        var monitor = new TopUpMonitor(controller, _time);

        Assert.Null(await monitor.CheckAsync(70, 45, 40));
        Assert.Null(await monitor.CheckAsync(70, 35, 40));
        Assert.Null(await monitor.CheckAsync(70, 45, 40));
        Assert.Equal(100, backend.Limit);
        TopUpCheckResult? ended = await monitor.CheckAsync(70, 46, 40);

        Assert.Equal(TopUpEndReason.TooHot, ended!.Reason);
        Assert.True(ended.Result.Succeeded);
        Assert.Equal(80, backend.Limit);
        Assert.False(backend.Applied[^1].AllowElevationPrompt);
        Assert.False(controller.LoadIntent()!.TopUpActive);
    }

    [Fact]
    public async Task Hot_battery_does_nothing_when_the_action_is_off_or_no_top_up_runs()
    {
        var backend = new TestBackend(Continuous);
        ChargeLimitController controller = New(backend);
        await controller.SetLimitAsync(true, 80);
        var monitor = new TopUpMonitor(controller, _time);
        int writes = backend.Applied.Count;
        for (int i = 0; i < 4; i++) Assert.Null(await monitor.CheckAsync(70, 50, 40));
        Assert.Equal(writes, backend.Applied.Count);

        await controller.StartTopUpAsync(TopUpPolicy.MaxDuration);
        writes = backend.Applied.Count;
        for (int i = 0; i < 4; i++) Assert.Null(await monitor.CheckAsync(70, 50, null));
        Assert.Equal(writes, backend.Applied.Count);
        Assert.Equal(100, backend.Limit);
    }

    [Fact]
    public void Heat_ended_top_up_toast_says_it_was_warm()
    {
        var ended = new TopUpCheckResult(TopUpEndReason.TooHot, new ChargeActionResult(ChargeActionStatus.Applied, ""), 80);
        BatteryToast? toast = new BatteryToastPlanner().TopUpEnded(ended, 70, new BatterySettings(), Start);
        Assert.True(toast!.TooHot);
        Assert.False(toast.Full);
    }}
