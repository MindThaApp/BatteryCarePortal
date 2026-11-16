using System.Globalization;
using System.Text.Json;
using Portal.Core.Services;
using Xunit;

namespace Portal.Core.Tests;

public sealed class ProTrialRecordTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 9, 30, 0, TimeSpan.Zero);

    private static (ProTrialRecord Record, Dictionary<string, object> Values) NewRecord()
    {
        var values = new Dictionary<string, object>();
        return (new ProTrialRecord(values), values);
    }

    [Fact]
    public void NewInstall_TrialWaitsForFirstUse_AndGrantsAccess()
    {
        var (record, _) = NewRecord();
        Assert.True(record.Migrate(firstLaunchUtcBeforeThisVersion: null));

        ProTrialTerms terms = record.Terms;
        Assert.True(record.IsMigrated);
        Assert.False(terms.IsStarted);
        Assert.Equal(TimeSpan.FromDays(14), terms.Duration);
        Assert.True(terms.GrantsAccessAt(Now));
        Assert.True(terms.GrantsAccessAt(Now.AddYears(1)));
        Assert.Equal(TimeSpan.FromDays(14), terms.RemainingAt(Now));
    }

    [Fact]
    public void FirstUse_StartsFourteenDays_Once()
    {
        var (record, values) = NewRecord();
        record.Migrate(null);

        Assert.True(record.TryStart(Now));
        Assert.False(record.TryStart(Now.AddDays(5)));

        ProTrialTerms terms = record.Terms;
        Assert.Equal(Now, terms.StartUtc);
        Assert.Equal(TimeSpan.FromDays(14), terms.Duration);
        Assert.True(terms.GrantsAccessAt(Now.AddDays(13)));
        Assert.False(terms.GrantsAccessAt(Now.AddDays(14)));
        Assert.Equal(TimeSpan.FromDays(4), terms.RemainingAt(Now.AddDays(10)));
        // Stored as an invariant round-trip string.
        Assert.Equal(Now.ToString("O", CultureInfo.InvariantCulture), values[ProTrialRecord.StartKey]);
    }

    [Fact]
    public void TryStart_BeforeTheMigration_RecordsNothing()
    {
        var (record, values) = NewRecord();
        Assert.False(record.TryStart(Now));
        Assert.False(values.ContainsKey(ProTrialRecord.StartKey));
    }

    [Fact]
    public void ExistingInstall_KeepsThirtyDaysFromItsFirstLaunch()
    {
        var (record, _) = NewRecord();
        DateTimeOffset firstLaunch = Now - TimeSpan.FromDays(5);
        Assert.True(record.Migrate(firstLaunch));

        ProTrialTerms terms = record.Terms;
        Assert.Equal(firstLaunch, terms.StartUtc);
        Assert.Equal(TimeSpan.FromDays(30), terms.Duration);
        // A running trial is not shortened to 14 days.
        Assert.Equal(TimeSpan.FromDays(25), terms.RemainingAt(Now));
        Assert.True(terms.GrantsAccessAt(Now.AddDays(20)));
        // A later use does not restart it.
        Assert.False(record.TryStart(Now));
        Assert.Equal(firstLaunch, record.Terms.StartUtc);
    }

    [Fact]
    public void ExistingInstall_ExpiredTrialStaysExpired()
    {
        var (record, _) = NewRecord();
        record.Migrate(Now - TimeSpan.FromDays(45));

        Assert.False(record.Terms.GrantsAccessAt(Now));
        Assert.False(record.TryStart(Now));
        Assert.False(record.Terms.GrantsAccessAt(Now));
        Assert.Equal(TimeSpan.Zero, record.Terms.RemainingAt(Now));
    }

    [Fact]
    public void Migration_RunsOnce()
    {
        var (record, _) = NewRecord();
        Assert.True(record.Migrate(null));
        // The next run sees the first launch this run created; that must not turn a new install into an old one.
        Assert.False(record.Migrate(Now - TimeSpan.FromDays(1)));
        Assert.False(record.Terms.IsStarted);
        Assert.Equal(TimeSpan.FromDays(14), record.Terms.Duration);

        var (legacy, _) = NewRecord();
        DateTimeOffset firstLaunch = Now - TimeSpan.FromDays(3);
        legacy.Migrate(firstLaunch);
        Assert.False(legacy.Migrate(null));
        Assert.Equal(firstLaunch, legacy.Terms.StartUtc);
        Assert.Equal(TimeSpan.FromDays(30), legacy.Terms.Duration);
    }
}
