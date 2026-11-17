using Portal.Core.Battery;
using Xunit;

namespace Portal.Core.Tests;

public class ChargeStatusTests
{
    private static ChargeStatusInput Ready(int? limit = 80) => new() { HasController = true, Limit = limit };

    [Fact] public void On() => Assert.Equal(ChargeStatusKind.On, ChargeStatusSentence.Resolve(Ready(80)));
    [Fact] public void Off() => Assert.Equal(ChargeStatusKind.Off, ChargeStatusSentence.Resolve(Ready(100)));
    [Fact] public void Unreadable() => Assert.Equal(ChargeStatusKind.Unreadable, ChargeStatusSentence.Resolve(Ready(null)));
    [Fact] public void Writing() => Assert.Equal(ChargeStatusKind.Writing, ChargeStatusSentence.Resolve(Ready() with { Busy = true }));
    [Fact] public void NeedsElevation() => Assert.Equal(ChargeStatusKind.NeedsElevation, ChargeStatusSentence.Resolve(Ready() with { LastAction = ChargeActionStatus.NeedsElevation }));
    [Fact] public void Cancelled() => Assert.Equal(ChargeStatusKind.Cancelled, ChargeStatusSentence.Resolve(Ready() with { LastAction = ChargeActionStatus.Cancelled }));
    [Fact] public void ReadBackMismatch() => Assert.Equal(ChargeStatusKind.ReadBackMismatch, ChargeStatusSentence.Resolve(Ready() with { LastAction = ChargeActionStatus.ReadBackMismatch }));
    [Fact] public void PpmOwns() => Assert.Equal(ChargeStatusKind.PpmOwns, ChargeStatusSentence.Resolve(Ready() with { Refusal = WriteRefusal.OtherOwner }));
    [Fact] public void PpmUnconfirmed() => Assert.Equal(ChargeStatusKind.PpmUnconfirmed, ChargeStatusSentence.Resolve(Ready() with { Refusal = WriteRefusal.OtherOwnerUnconfirmed }));
    [Fact] public void BetaOffFromGate() => Assert.Equal(ChargeStatusKind.BetaOff, ChargeStatusSentence.Resolve(Ready() with { Refusal = WriteRefusal.BetaOff }));
    [Fact] public void NotVerifiedFromGate() => Assert.Equal(ChargeStatusKind.NotVerified, ChargeStatusSentence.Resolve(Ready() with { Refusal = WriteRefusal.NoReadBack }));
    [Fact] public void NotDetectedFromGate() => Assert.Equal(ChargeStatusKind.NotDetected, ChargeStatusSentence.Resolve(Ready() with { Refusal = WriteRefusal.NotDetected }));
    [Fact] public void ChangedElsewhere() => Assert.Equal(ChargeStatusKind.ChangedElsewhere, ChargeStatusSentence.Resolve(Ready() with { LastReapply = ChargeActionStatus.ChangedElsewhere }));
    [Fact] public void ReapplyFailed() => Assert.Equal(ChargeStatusKind.ReapplyFailed, ChargeStatusSentence.Resolve(Ready() with { ManagedLimitOn = true, ReapplyFailed = true, NeedsReapply = true }));
    [Fact] public void NeedsReapply() => Assert.Equal(ChargeStatusKind.NeedsReapply, ChargeStatusSentence.Resolve(Ready() with { ManagedLimitOn = true, NeedsReapply = true }));
    [Fact] public void StateFileUnusable() => Assert.Equal(ChargeStatusKind.StateFileUnusable, ChargeStatusSentence.Resolve(Ready() with { IntentState = IntentFileState.Unusable }));
    [Fact] public void NoBattery() => Assert.Equal(ChargeStatusKind.NoBattery, ChargeStatusSentence.Resolve(new() { Support = DeviceSupportState.NoBattery }));
    [Fact] public void MonitorMode() => Assert.Equal(ChargeStatusKind.MonitorMode, ChargeStatusSentence.Resolve(new() { Support = DeviceSupportState.MonitorMode }));
    [Fact] public void OneStepMissing() => Assert.Equal(ChargeStatusKind.OneStepMissing, ChargeStatusSentence.Resolve(new() { Support = DeviceSupportState.OneStepMissing }));
    [Fact] public void BetaOffWithoutController() => Assert.Equal(ChargeStatusKind.BetaOff, ChargeStatusSentence.Resolve(new() { Support = DeviceSupportState.OnTestList }));
    [Fact] public void NotVerifiedWithoutController() => Assert.Equal(ChargeStatusKind.NotVerified, ChargeStatusSentence.Resolve(new() { Support = DeviceSupportState.OnTestList, BetaOptIn = true }));
    [Fact] public void NotDetectedWithoutController() => Assert.Equal(ChargeStatusKind.NotDetected, ChargeStatusSentence.Resolve(new() { Support = DeviceSupportState.Supported }));

    [Fact]
    public void ReapplyNoticesIgnoredWhenAppDoesNotManageTheLimit() =>
        Assert.Equal(ChargeStatusKind.On, ChargeStatusSentence.Resolve(Ready() with { NeedsReapply = true, ReapplyFailed = true }));

    [Fact]
    public void ResourceKeyUsesPrefix() => Assert.Equal("ChargeStatusPpmOwns", ChargeStatusSentence.ResourceKey(ChargeStatusKind.PpmOwns));

    [Fact]
    public void EveryKindHasAResourceString()
    {
        string resw = File.ReadAllText(FindResw());
        foreach (ChargeStatusKind kind in Enum.GetValues<ChargeStatusKind>())
            Assert.Contains($"name=\"{ChargeStatusSentence.ResourceKey(kind)}\"", resw);
    }

    private static string FindResw()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "BatteryCarePortal.sln"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? throw new FileNotFoundException("solution root"), "src", "BatteryCarePortal.App", "Strings", "en-US", "Resources.resw");
    }
}

public class FakeBatteryFlagTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bcp-flag-" + Guid.NewGuid().ToString("N"));

    public FakeBatteryFlagTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void NoFileMeansOff() => Assert.False(BatteryEnvironment.HasFlagFile(_folder));

    [Fact]
    public void FileMeansOn()
    {
        File.WriteAllText(Path.Combine(_folder, BatteryEnvironment.FlagFileName), string.Empty);
        Assert.True(BatteryEnvironment.HasFlagFile(_folder));
    }

    [Fact]
    public void MissingFolderOrEmptyPathMeansOff()
    {
        Assert.False(BatteryEnvironment.HasFlagFile(Path.Combine(_folder, "nope")));
        Assert.False(BatteryEnvironment.HasFlagFile(string.Empty));
    }

    [Fact]
    public void FlagFileNameIsTheDocumentedOne() => Assert.Equal("fake-battery.flag", BatteryEnvironment.FlagFileName);
}

public class TodayLogTests
{
    private static BatteryHistoryEntry S(int minute, int pct, bool ac, bool charging) =>
        new() { UnixSeconds = new DateTimeOffset(2026, 11, 17, 8, minute, 0, TimeSpan.Zero).ToUnixTimeSeconds(), Percent = pct, OnAc = ac, Charging = charging };

    [Fact]
    public void ReportsUnplugPlugInAndChargingChanges()
    {
        var events = TodayLog.Build(new[]
        {
            S(0, 90, true, false), S(1, 89, false, false), S(2, 30, true, true), S(3, 60, true, false),
        }, TimeZoneInfo.Utc);
        Assert.Equal(new[] { TodayEventKind.Unplugged, TodayEventKind.PluggedIn, TodayEventKind.ChargingStarted, TodayEventKind.ChargingStopped },
            events.Select(e => e.Kind));
        Assert.Equal(30, events[1].Percent);
    }

    [Fact]
    public void SteadySamplesProduceNothing() =>
        Assert.Empty(TodayLog.Build(new[] { S(0, 60, true, false), S(1, 60, true, false) }, TimeZoneInfo.Utc));

    [Fact]
    public void DayStartIsLocalMidnight()
    {
        var now = new DateTimeOffset(2026, 11, 17, 15, 30, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero), TodayLog.DayStart(now, TimeZoneInfo.Utc));
    }
}

public class BatteryRingMathTests
{
    [Fact] public void FullCircleIs360() => Assert.Equal(360, BatteryRingMath.Degrees(100), 6);
    [Fact] public void ClampsOutOfRange() => Assert.Equal(0, BatteryRingMath.Degrees(-5));

    [Fact]
    public void QuarterIsRightOfCentre()
    {
        (double x, double y) = BatteryRingMath.PointAt(50, 50, 40, 25);
        Assert.Equal(90, x, 6);
        Assert.Equal(50, y, 6);
    }

    [Fact]
    public void ZeroIsTop()
    {
        (double x, double y) = BatteryRingMath.PointAt(50, 50, 40, 0);
        Assert.Equal(50, x, 6);
        Assert.Equal(10, y, 6);
    }

    [Fact] public void FullArcStopsShort() => Assert.True(BatteryRingMath.ArcPercent(100) < 100);
}
