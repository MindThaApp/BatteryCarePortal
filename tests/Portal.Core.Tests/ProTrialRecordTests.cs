using System.Globalization;
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

        ProTrialTerms terms = record.Terms;
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

        Assert.True(record.TryStart(Now));
        Assert.False(record.TryStart(Now.AddDays(5)));

        ProTrialTerms terms = record.Terms;
        Assert.Equal(Now, terms.StartUtc);
        Assert.Equal(TimeSpan.FromDays(14), terms.Duration);
        Assert.True(terms.GrantsAccessAt(Now.AddDays(13)));
        Assert.False(terms.GrantsAccessAt(Now.AddDays(14)));
        Assert.Equal(TimeSpan.FromDays(4), terms.RemainingAt(Now.AddDays(10)));
        Assert.Equal(Now.ToString("O", CultureInfo.InvariantCulture), values[ProTrialRecord.StartKey]);
    }
}
