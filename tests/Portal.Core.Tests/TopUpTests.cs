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

    private ChargeLimitController New(TestBackend backend) =>
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
        Assert.True(TopUpPolicy.CanStart(on, 80, 60));
        Assert.True(TopUpPolicy.CanStart(on, 80, null));
        Assert.False(TopUpPolicy.CanStart(on, 100, 60));
        Assert.False(TopUpPolicy.CanStart(on, null, 60));
        Assert.False(TopUpPolicy.CanStart(on, 80, 100));
        Assert.False(TopUpPolicy.CanStart(null, 80, 60));
        Assert.False(TopUpPolicy.CanStart(on with { Managed = false }, 80, 60));
        Assert.False(TopUpPolicy.CanStart(on with { TopUpActive = true }, 80, 60));
        Assert.False(TopUpPolicy.CanStart(on with { NeedsReapply = true }, 80, 60));
        Assert.False(TopUpPolicy.CanStart(on with { ReapplyFailed = true }, 80, 60));
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

    [Fact]
    public async Task Needing_elevation_never_prompts_leaves_it_to_the_user_and_tells_once()
    {
        var backend = new TestBackend(Continuous, requiresElevation: true);
        ChargeLimitController controller = New(backend);
        Assert.True((await controller.SetLimitAsync(true, 80)).Succeeded);
        Assert.True((await controller.StartTopUpAsync(TopUpPolicy.MaxDuration)).Succeeded);
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
}
