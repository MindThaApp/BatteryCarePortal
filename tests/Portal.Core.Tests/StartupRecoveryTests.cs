using Portal.Core.Battery;
using Xunit;

namespace Portal.Core.Tests;

public class StartupRecoveryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bcp-recovery-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    [Fact]
    public void Retries_back_off_and_stop()
    {
        Assert.Equal(new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5) },
            StartupRecovery.RetryDelays);
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, true, false)]
    public void Check_is_retried_only_for_an_unfinished_top_up_or_pending_reapply(bool topUp, bool needsReapply, bool failed, bool expected)
    {
        var store = new ChargeIntentStore(_folder);
        store.TrySave(new ChargeIntent
        {
            BackendId = "x", Managed = true, LimitOn = true, Limit = 80, TopUpActive = topUp, NeedsReapply = needsReapply, ReapplyFailed = failed,
        });
        Assert.Equal(expected, StartupRecovery.ShouldRetryCheck(store));
    }

    [Fact]
    public void No_retry_without_a_readable_intent()
    {
        var store = new ChargeIntentStore(_folder);
        Assert.False(StartupRecovery.ShouldRetryCheck(store));
        Directory.CreateDirectory(_folder);
        File.WriteAllText(store.Path, "nope");
        Assert.False(StartupRecovery.ShouldRetryCheck(store));
        Assert.Equal("nope", File.ReadAllText(store.Path));
    }

    [Theory]
    [InlineData(ChargeActionStatus.NeedsElevation, true)]
    [InlineData(ChargeActionStatus.Failed, true)]
    [InlineData(ChargeActionStatus.ReadBackMismatch, true)]
    [InlineData(ChargeActionStatus.Applied, false)]
    [InlineData(ChargeActionStatus.ChangedElsewhere, false)]
    [InlineData(ChargeActionStatus.NothingToDo, false)]
    public void User_is_told_when_an_unattended_reapply_left_the_limit_off(ChargeActionStatus status, bool expected) =>
        Assert.Equal(expected, StartupRecovery.NeedsUserNotice(new ChargeActionResult(status, string.Empty)));

    [Fact]
    public async Task Dell_style_reapply_needs_the_user_and_never_prompts()
    {
        var backend = new TestBackend(new ChargeLimitCapabilities { Kind = ChargeLimitKind.ContinuousRange, MinLimit = 50, MaxLimit = 100 }, requiresElevation: true);
        var controller = new ChargeLimitController(backend, new OriginalStateStore(_folder), new ChargeIntentStore(_folder), writeLock: new ChargeWriteLock());
        Assert.True((await controller.SetLimitAsync(true, 80)).Succeeded);
        Assert.True((await controller.StartTopUpAsync(TimeSpan.FromHours(1))).Succeeded);
        int writes = backend.Applied.Count;

        ChargeActionResult result = await controller.ReapplyAsync(ReapplyReason.Startup);
        Assert.True(StartupRecovery.NeedsUserNotice(result));
        Assert.Equal(writes, backend.Applied.Count);
    }
}
