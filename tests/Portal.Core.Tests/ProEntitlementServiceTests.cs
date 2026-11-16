using Portal.Core.Services;
using Xunit;

namespace Portal.Core.Tests;

public sealed class ProEntitlementServiceTests
{
    private static readonly DateTimeOffset Now = new(2027, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ExpiredTrialStart = Now - ProEntitlementService.TrialDuration - TimeSpan.FromDays(1);

    private sealed class FakeStore : IStoreLicenseProvider
    {
        public bool AddOnOwned { get; set; }
        public Exception? AddOnError { get; set; }
        public int AddOnCalls { get; private set; }

        public Task<bool> IsDurableAddOnOwnedAsync(string addOnStoreId)
        {
            AddOnCalls++;
            if (AddOnError is not null) return Task.FromException<bool>(AddOnError);
            return Task.FromResult(AddOnOwned && addOnStoreId == ProEntitlementService.ProAddOnStoreId);
        }
    }

    private sealed class Harness
    {
        public DateTimeOffset? TrialStart = ExpiredTrialStart;
        public readonly List<string> Log = [];
        public FakeStore? Store = new();
        public DateTimeOffset Clock = Now;

        public ProEntitlementService Create() => new(
            Store,
            () => new ProTrialTerms(TrialStart, ProEntitlementService.TrialDuration),
            (context, reason) => Log.Add($"{context}: {reason}"),
            () => Clock);
    }

    [Fact]
    public async Task AddOnOwnedInsideTheTrial_IsPermanentAccess()
    {
        var h = new Harness { TrialStart = Now - TimeSpan.FromDays(3) };
        h.Store!.AddOnOwned = true;
        ProEntitlementService service = h.Create();

        Assert.True(service.IsTrialActive);
        Assert.True(await service.OwnsPermanentAccessAsync());
    }

    [Fact]
    public async Task TrialOnly_IsNotPermanentAccess_AndStoreFailureReadsAsNotOwned()
    {
        var h = new Harness { TrialStart = Now - TimeSpan.FromDays(3) };
        h.Store!.AddOnError = new InvalidOperationException("offline");

        Assert.False(await h.Create().OwnsPermanentAccessAsync());
    }

    [Fact]
    public async Task AddOnOwned_Grants()
    {
        var h = new Harness();
        h.Store!.AddOnOwned = true;

        Assert.Equal(ProAccessSource.AddOn, await h.Create().GetAccessSourceAsync());
    }

    [Fact]
    public async Task NeitherButTrialActive_Grants()
    {
        var h = new Harness { TrialStart = Now - TimeSpan.FromDays(13) };

        Assert.Equal(ProAccessSource.Trial, await h.Create().GetAccessSourceAsync());
    }

    [Fact]
    public void TrialIsFourteenDays() => Assert.Equal(TimeSpan.FromDays(14), ProEntitlementService.TrialDuration);

    [Fact]
    public async Task TrialNotStarted_GrantsWithoutAskingTheStore()
    {
        var h = new Harness { TrialStart = null };
        h.Store!.AddOnError = new InvalidOperationException("must not be asked");
        ProEntitlementService service = h.Create();

        Assert.Equal(ProAccessSource.Trial, await service.GetAccessSourceAsync());
        Assert.True(service.IsTrialActive);
        Assert.False(service.IsTrialStarted);
        Assert.Equal(TimeSpan.FromDays(14), service.TrialRemaining);
        Assert.Equal(0, h.Store.AddOnCalls);
    }

    [Fact]
    public async Task TrialStartedToday_RunsFourteenDaysFromTheStart()
    {
        var h = new Harness { TrialStart = Now - TimeSpan.FromDays(1) };
        ProEntitlementService service = h.Create();

        Assert.True(service.IsTrialStarted);
        Assert.Equal(TimeSpan.FromDays(13), service.TrialRemaining);
        Assert.True(await service.HasProAccessAsync());

        h.TrialStart = Now - TimeSpan.FromDays(14);
        Assert.False(service.IsTrialActive);
        Assert.Equal(TimeSpan.Zero, service.TrialRemaining);
    }

    [Fact]
    public async Task NeitherAndTrialExpired_DeniesWithoutThrowing()
    {
        var h = new Harness();
        ProEntitlementService service = h.Create();

        Assert.Equal(ProAccessSource.None, await service.GetAccessSourceAsync());
        Assert.Equal(TimeSpan.Zero, service.TrialRemaining);
        Assert.Empty(h.Log);
    }

    [Fact]
    public async Task StoreThrows_FallsBackToTrialGateAndLogs()
    {
        var h = new Harness();
        h.Store!.AddOnError = new InvalidOperationException("Store add-on query failed");

        Assert.Equal(ProAccessSource.None, await h.Create().GetAccessSourceAsync());
        Assert.Contains(h.Log, l => l.Contains("add-on check failed"));
    }

    [Fact]
    public async Task StoreThrowsAfterTrial_Denies()
    {
        var h = new Harness();
        h.Store!.AddOnError = new InvalidOperationException("offline");

        Assert.False(await h.Create().HasProAccessAsync());
    }

    [Fact]
    public async Task FailedCheck_IsNotCached_SoTheNextCheckCanGrant()
    {
        var h = new Harness();
        h.Store!.AddOnError = new InvalidOperationException("offline");
        ProEntitlementService service = h.Create();
        Assert.False(await service.HasProAccessAsync());

        h.Store.AddOnError = null;
        h.Store.AddOnOwned = true;
        Assert.True(await service.HasProAccessAsync());
    }

    [Fact]
    public async Task ConclusiveAnswer_IsCachedBriefly()
    {
        var h = new Harness();
        ProEntitlementService service = h.Create();
        Assert.False(await service.HasProAccessAsync());
        await service.HasProAccessAsync();
        Assert.Equal(1, h.Store!.AddOnCalls);

        h.Clock = Now.AddMinutes(6);
        await service.HasProAccessAsync();
        Assert.Equal(2, h.Store.AddOnCalls);
    }

    [Fact]
    public async Task NoStoreContext_FallsBackToTrialGateAndLogs()
    {
        var h = new Harness { Store = null };

        Assert.False(await h.Create().HasProAccessAsync());
        Assert.Single(h.Log);
    }

    [Fact]
    public void FeatureTiers_BasicLimitAndMonitoringFree_OthersPro()
    {
        Assert.True(FeatureTiers.IsAvailable(PortalFeature.Monitoring, hasPro: false));
        Assert.True(FeatureTiers.IsAvailable(PortalFeature.BasicChargeLimit, hasPro: false));
        Assert.False(FeatureTiers.IsAvailable(PortalFeature.HeatProtection, hasPro: false));
        Assert.True(FeatureTiers.IsAvailable(PortalFeature.HeatProtection, hasPro: true));
        foreach (PortalFeature f in Enum.GetValues<PortalFeature>())
            Assert.True(FeatureTiers.IsAvailable(f, hasPro: true));
    }
}

