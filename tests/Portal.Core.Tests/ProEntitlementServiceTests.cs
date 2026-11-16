using Portal.Core.Services;
using Xunit;

namespace Portal.Core.Tests;

public sealed class ProEntitlementServiceTests
{
    private static readonly DateTimeOffset Cutoff = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2027, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ExpiredTrialStart = Now - ProEntitlementService.TrialDuration - TimeSpan.FromDays(1);

    private sealed class FakeStore : IStoreLicenseProvider
    {
        public IReadOnlyList<StoreSkuInfo> Skus { get; set; } = [];
        public bool AddOnOwned { get; set; }
        public Exception? SkuError { get; set; }
        public Exception? AddOnError { get; set; }
        public int SkuCalls { get; private set; }
        public int AddOnCalls { get; private set; }

        public Task<IReadOnlyList<StoreSkuInfo>> GetCurrentAppSkusAsync()
        {
            SkuCalls++;
            return SkuError is null ? Task.FromResult(Skus) : Task.FromException<IReadOnlyList<StoreSkuInfo>>(SkuError);
        }

        public Task<bool> IsDurableAddOnOwnedAsync(string addOnStoreId)
        {
            AddOnCalls++;
            if (AddOnError is not null) return Task.FromException<bool>(AddOnError);
            return Task.FromResult(AddOnOwned && addOnStoreId == ProEntitlementService.ProAddOnStoreId);
        }
    }

    private sealed class Harness
    {
        public bool LegacyConfirmed;
        public DateTimeOffset? TrialStart = ExpiredTrialStart;
        public TimeSpan TrialLength = ProEntitlementService.TrialDuration;
        public readonly List<string> Log = [];
        public FakeStore? Store = new();

        public ProEntitlementService Create() => new(
            Store,
            () => new ProTrialTerms(TrialStart, TrialLength),
            () => LegacyConfirmed,
            () => LegacyConfirmed = true,
            (context, reason) => Log.Add($"{context}: {reason}"),
            () => Now,
            Cutoff);
    }

    private static string SkuJson(string acquired, bool collectionTrial = false, bool skuTrial = false) =>
        $$"""
        {
          "productId": "9PLACEHOLDER00",
          "skuId": "0010",
          "isTrial": {{(skuTrial ? "true" : "false")}},
          "collectionData": { "acquiredDate": "{{acquired}}", "isTrial": {{(collectionTrial ? "true" : "false")}} }
        }
        """;

    private const string CatalogOnlySkuJson = """{ "productId": "9PLACEHOLDER00", "skuId": "0011", "isTrial": true }""";

    [Fact]
    public async Task AcquiredBeforeCutoff_GrantsLegacyAndRemembersIt()
    {
        var h = new Harness();
        h.Store!.Skus = [new StoreSkuInfo(CatalogOnlySkuJson), new StoreSkuInfo(SkuJson("2026-09-14T08:30:00.0000000Z"))];

        ProAccessSource source = await h.Create().GetAccessSourceAsync();

        Assert.Equal(ProAccessSource.LegacyPurchase, source);
        Assert.True(h.LegacyConfirmed);
        Assert.Empty(h.Log);
    }

    [Fact]
    public async Task RememberedLegacyPurchase_GrantsWithoutAskingTheStore()
    {
        var h = new Harness { LegacyConfirmed = true };
        h.Store!.SkuError = new InvalidOperationException("offline");
        h.Store.AddOnError = new InvalidOperationException("offline");

        Assert.True(await h.Create().HasProAccessAsync());
        Assert.Equal(0, h.Store.SkuCalls);
        Assert.Equal(0, h.Store.AddOnCalls);
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
        h.Store.SkuError = new InvalidOperationException("offline");

        Assert.False(await h.Create().OwnsPermanentAccessAsync());
    }

    [Fact]
    public async Task StoreTrialAcquiredBeforeCutoff_IsNotAPaidPurchase()
    {
        var h = new Harness();
        h.Store!.Skus = [new StoreSkuInfo(SkuJson("2026-09-14T08:30:00Z", collectionTrial: true))];

        Assert.False(await h.Create().HasProAccessAsync());
        Assert.False(h.LegacyConfirmed);
    }

    [Fact]
    public async Task AddOnOwned_Grants()
    {
        var h = new Harness();
        h.Store!.AddOnOwned = true;
        h.Store.Skus = [new StoreSkuInfo(SkuJson("2027-01-10T10:00:00Z"))];

        Assert.Equal(ProAccessSource.AddOn, await h.Create().GetAccessSourceAsync());
        Assert.False(h.LegacyConfirmed);
    }

    [Fact]
    public async Task NeitherButTrialActive_Grants()
    {
        var h = new Harness { TrialStart = Now - TimeSpan.FromDays(13) };
        h.Store!.Skus = [new StoreSkuInfo(SkuJson("2027-02-01T10:00:00Z"))];

        Assert.Equal(ProAccessSource.Trial, await h.Create().GetAccessSourceAsync());
    }

    [Fact]
    public void TrialIsFourteenDays()
    {
        Assert.Equal(TimeSpan.FromDays(14), ProEntitlementService.TrialDuration);
        Assert.Equal(TimeSpan.FromDays(30), ProTrialRecord.LegacyDuration);
    }

    [Fact]
    public async Task TrialNotStarted_GrantsWithoutAskingTheStore()
    {
        var h = new Harness { TrialStart = null };
        h.Store!.SkuError = new InvalidOperationException("must not be asked");
        ProEntitlementService service = h.Create();

        Assert.Equal(ProAccessSource.Trial, await service.GetAccessSourceAsync());
        Assert.True(service.IsTrialActive);
        Assert.False(service.IsTrialStarted);
        Assert.Equal(TimeSpan.FromDays(14), service.TrialRemaining);
        Assert.Equal(0, h.Store.SkuCalls);
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
    public void LegacyTerms_RunThirtyDays()
    {
        var h = new Harness { TrialStart = Now - TimeSpan.FromDays(20), TrialLength = ProTrialRecord.LegacyDuration };
        ProEntitlementService service = h.Create();

        Assert.True(service.IsTrialActive);
        Assert.Equal(TimeSpan.FromDays(10), service.TrialRemaining);
    }

    [Fact]
    public async Task NeitherAndTrialExpired_DeniesWithoutThrowing()
    {
        var h = new Harness();
        h.Store!.Skus = [new StoreSkuInfo(SkuJson("2027-01-10T10:00:00Z"))];
        ProEntitlementService service = h.Create();

        Assert.Equal(ProAccessSource.None, await service.GetAccessSourceAsync());
        Assert.Equal(TimeSpan.Zero, service.TrialRemaining);
        Assert.False(h.LegacyConfirmed);
        Assert.Empty(h.Log);
    }

    [Fact]
    public async Task StoreThrows_FallsBackToTrialGateAndLogs()
    {
        var h = new Harness { TrialStart = Now - TimeSpan.FromDays(3) };
        h.Store!.SkuError = new InvalidOperationException("Store product query failed");
        h.Store.AddOnError = new TimeoutException();
        ProEntitlementService service = h.Create();

        Assert.True(await service.HasProAccessAsync());

        h.TrialStart = ExpiredTrialStart;
        Assert.False(await service.HasProAccessAsync());
        Assert.Contains(h.Log, line => line.StartsWith("Paid Pro purchase check failed"));
        Assert.Contains(h.Log, line => line.StartsWith("Pro add-on check failed"));
        Assert.False(h.LegacyConfirmed);
    }

    [Fact]
    public async Task StoreThrows_ButAddOnAnswers_StillGrants()
    {
        var h = new Harness();
        h.Store!.SkuError = new InvalidOperationException("offline");
        h.Store.AddOnOwned = true;

        Assert.True(await h.Create().HasProAccessAsync());
    }

    [Fact]
    public async Task FailedCheck_IsNotCached_SoTheNextCheckCanGrant()
    {
        var h = new Harness();
        h.Store!.SkuError = new InvalidOperationException("offline");
        ProEntitlementService service = h.Create();
        Assert.False(await service.HasProAccessAsync());

        h.Store.SkuError = null;
        h.Store.Skus = [new StoreSkuInfo(SkuJson("2026-10-01T00:00:00Z"))];
        Assert.True(await service.HasProAccessAsync());
    }

    [Fact]
    public async Task NoStoreContext_FallsBackToTrialGateAndLogs()
    {
        var h = new Harness { Store = null };

        Assert.False(await h.Create().HasProAccessAsync());
        Assert.Single(h.Log);
    }

    [Fact]
    public async Task UnreadableJson_FallsBackAndLogs()
    {
        var h = new Harness();
        h.Store!.Skus = [new StoreSkuInfo("{ not json")];

        Assert.False(await h.Create().HasProAccessAsync());
        Assert.Contains(h.Log, line => line.StartsWith("Paid Pro purchase check inconclusive"));
    }

    [Fact]
    public void UnsetAcquiredDate_IsNotReadAsPaid()
    {
        IReadOnlyList<StoreSkuInfo> skus = [new StoreSkuInfo(SkuJson("0001-01-01T00:00:00Z"))];

        Assert.Equal(LegacyPurchaseResult.Unknown, ProEntitlementService.EvaluateLegacyPurchase(skus, Cutoff, out _));
    }

    [Fact]
    public void NoAcquisitionData_IsUnknown()
    {
        IReadOnlyList<StoreSkuInfo> skus = [new StoreSkuInfo(CatalogOnlySkuJson)];

        Assert.Equal(LegacyPurchaseResult.Unknown, ProEntitlementService.EvaluateLegacyPurchase(skus, Cutoff, out _));
    }

    [Fact]
    public void PascalCaseJson_IsRead()
    {
        const string json = """{ "ProductId": "9PLACEHOLDER00", "Properties": { "IsTrial": false }, "CollectionData": { "AcquiredDate": "2026-09-01T00:00:00Z" } }""";

        Assert.Equal(LegacyPurchaseResult.Paid, ProEntitlementService.EvaluateLegacyPurchase([new StoreSkuInfo(json)], Cutoff, out _));
    }

    [Fact]
    public void TypedCollectionData_IsUsedWhenJsonHasNone()
    {
        IReadOnlyList<StoreSkuInfo> skus =
            [new StoreSkuInfo(CatalogOnlySkuJson.Replace("true", "false"), IsInUserCollection: true, CollectionAcquiredUtc: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero))];

        Assert.Equal(LegacyPurchaseResult.Paid, ProEntitlementService.EvaluateLegacyPurchase(skus, Cutoff, out _));
    }

    [Fact]
    public void AcquiredOnOrAfterCutoff_IsNotPaid()
    {
        IReadOnlyList<StoreSkuInfo> skus = [new StoreSkuInfo(SkuJson("2026-12-01T00:00:00Z"))];

        Assert.Equal(LegacyPurchaseResult.NotPaid, ProEntitlementService.EvaluateLegacyPurchase(skus, Cutoff, out _));
    }
}
