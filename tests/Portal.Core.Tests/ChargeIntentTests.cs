using System.Diagnostics;
using Portal.Core.Battery;
using Portal.Core.Battery.Backends;
using Xunit;

namespace Portal.Core.Tests;

public class ChargeIntentTests : IDisposable
{
    private static readonly ChargeLimitCapabilities Continuous = new() { Kind = ChargeLimitKind.ContinuousRange, MinLimit = 50, MaxLimit = 100 };
    private const uint On = 1u << LenovoConservationBackend.ConservationBit;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bcp-intent-test-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    private ChargeLimitController New(IChargeLimitBackend backend) =>
        new(backend, new OriginalStateStore(_folder), new ChargeIntentStore(_folder), _time);

    private ChargeIntent? Intent() => new ChargeIntentStore(_folder).Load(out ChargeIntent? i) == AtomicJsonFile.ReadStatus.Ok ? i : null;

    private static LenovoConservationBackend Lenovo(FakeEnergyDriver driver, WriteGateContext? gate = null)
    {
        var wmi = new FakeWmiReader();
        wmi.Add("Win32_ComputerSystem", new() { ["Manufacturer"] = "LENOVO", ["SystemFamily"] = "IdeaPad 5", ["Model"] = "82FG" });
        var backend = new LenovoConservationBackend(wmi, new RecordingExecutor(), driver) { GateContext = gate ?? WriteGateContext.Default };
        Assert.True(backend.Detect());
        return backend;
    }

    [Fact]
    public async Task Intent_is_saved_before_the_top_up_write()
    {
        var backend = new SpyBackend(() => Intent());
        ChargeLimitController controller = New(backend);
        Assert.True((await controller.SetLimitAsync(true, 80)).Succeeded);

        Assert.True((await controller.StartTopUpAsync(TimeSpan.FromHours(2))).Succeeded);
        ChargeIntent seen = backend.IntentAtWrite[^1]!;
        Assert.True(seen.TopUpActive);
        Assert.True(seen.LimitOn);
        Assert.Equal(80, seen.Limit);
        Assert.Equal(100, backend.Limit);
    }

    [Fact]
    public async Task Crash_during_top_up_is_fixed_at_the_next_start()
    {
        var driver = new FakeEnergyDriver();
        Assert.True((await New(Lenovo(driver)).SetLimitAsync(true)).Succeeded);
        Assert.True((await New(Lenovo(driver)).StartTopUpAsync(TimeSpan.FromHours(12))).Succeeded);
        Assert.False(LenovoConservationBackend.IsConservationOn(driver.ModeWord));

        // A new controller is a new process: nothing in memory survives, only the intent file.
        ChargeActionResult result = await New(Lenovo(driver)).ReapplyAsync(ReapplyReason.Startup);
        Assert.Equal(ChargeActionStatus.Applied, result.Status);
        Assert.True(LenovoConservationBackend.IsConservationOn(driver.ModeWord));
        Assert.False(Intent()!.TopUpActive);
        Assert.False(Intent()!.NeedsReapply);
    }

    [Fact]
    public async Task Quit_during_top_up_puts_the_limit_back()
    {
        var driver = new FakeEnergyDriver();
        ChargeLimitController controller = New(Lenovo(driver));
        await controller.SetLimitAsync(true);
        await controller.StartTopUpAsync(TimeSpan.FromHours(12));

        ChargeActionResult result = controller.ReapplyOnExit(ChargeLimitController.DefaultExitBudget);
        Assert.True(result.Succeeded, result.Message);
        Assert.True(LenovoConservationBackend.IsConservationOn(driver.ModeWord));
        Assert.False(Intent()!.TopUpActive);
    }

    [Fact]
    public async Task Quit_that_needs_elevation_never_prompts_and_leaves_needs_reapply()
    {
        var backend = new TestBackend(Continuous, requiresElevation: true);
        ChargeLimitController controller = New(backend);
        await controller.SetLimitAsync(true, 80);
        await controller.StartTopUpAsync(TimeSpan.FromHours(12));
        Assert.Equal(100, backend.Limit);

        ChargeActionResult result = controller.ReapplyOnExit(ChargeLimitController.DefaultExitBudget);
        Assert.Equal(ChargeActionStatus.NeedsElevation, result.Status);
        Assert.True(Intent()!.NeedsReapply);
        Assert.True(Intent()!.TopUpActive);
        Assert.Equal(100, backend.Limit);

        // The next start tries again unattended (still no prompt); the user then puts it back with a prompt.
        Assert.Equal(ChargeActionStatus.NeedsElevation, (await New(backend).ReapplyAsync(ReapplyReason.Startup)).Status);
        Assert.True((await New(backend).SetLimitAsync(true, 80)).Succeeded);
        Assert.False(Intent()!.NeedsReapply);
        Assert.Equal(80, backend.Limit);
        // Only the three user actions wrote; both unattended attempts stopped before writing.
        Assert.Equal(3, backend.Applied.Count);
        Assert.All(backend.Applied, r => Assert.True(r.AllowElevationPrompt));
    }

    [Fact]
    public async Task Quit_is_bounded_in_time_when_the_backend_hangs()
    {
        var backend = new ChargeLimitControllerTests.BlockingBackend();
        new ChargeIntentStore(_folder).TrySave(new ChargeIntent { BackendId = backend.Id, Managed = true, LimitOn = true, Limit = 80, TopUpActive = true });
        ChargeLimitController controller = New(backend);

        var watch = Stopwatch.StartNew();
        ChargeActionResult result = controller.ReapplyOnExit(TimeSpan.FromMilliseconds(200));
        watch.Stop();

        Assert.Equal(ChargeActionStatus.Failed, result.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), watch.Elapsed.ToString());
        Assert.True(Intent()!.NeedsReapply);
    }

    [Fact]
    public async Task Wake_reapplies_a_lost_limit_but_start_without_flags_asks_instead()
    {
        var driver = new FakeEnergyDriver();
        await New(Lenovo(driver)).SetLimitAsync(true);
        driver.ModeWord = 0;

        int writesBefore = driver.Sent.Count(c => c == LenovoConservationBackend.ConservationOnCode);
        Assert.Equal(ChargeActionStatus.ChangedElsewhere, (await New(Lenovo(driver)).ReapplyAsync(ReapplyReason.Startup)).Status);
        Assert.Equal(writesBefore, driver.Sent.Count(c => c == LenovoConservationBackend.ConservationOnCode));

        Assert.Equal(ChargeActionStatus.Applied, (await New(Lenovo(driver)).ReapplyAsync(ReapplyReason.Resume)).Status);
        Assert.True(LenovoConservationBackend.IsConservationOn(driver.ModeWord));
    }

    [Fact]
    public async Task Wake_during_a_running_top_up_leaves_it_running()
    {
        var driver = new FakeEnergyDriver();
        ChargeLimitController controller = New(Lenovo(driver));
        await controller.SetLimitAsync(true);
        await controller.StartTopUpAsync(TimeSpan.FromHours(2));

        Assert.Equal(ChargeActionStatus.NothingToDo, (await controller.ReapplyAsync(ReapplyReason.Resume)).Status);
        Assert.False(LenovoConservationBackend.IsConservationOn(driver.ModeWord));

        _time.Advance(TimeSpan.FromHours(3));
        Assert.Equal(ChargeActionStatus.Applied, (await controller.ReapplyAsync(ReapplyReason.Resume)).Status);
        Assert.True(LenovoConservationBackend.IsConservationOn(driver.ModeWord));
    }

    [Fact]
    public async Task Unattended_reapply_never_turns_a_limit_off()
    {
        var driver = new FakeEnergyDriver();
        ChargeLimitController controller = New(Lenovo(driver));
        await controller.SetLimitAsync(false);
        driver.ModeWord = On;

        foreach (ReapplyReason reason in Enum.GetValues<ReapplyReason>())
            Assert.Equal(ChargeActionStatus.NothingToDo, (await controller.ReapplyAsync(reason)).Status);
        Assert.True(LenovoConservationBackend.IsConservationOn(driver.ModeWord));
    }

    [Fact]
    public async Task Unattended_reapply_never_overrides_a_limit_set_elsewhere()
    {
        var backend = new TestBackend(Continuous);
        ChargeLimitController controller = New(backend);
        await controller.SetLimitAsync(true, 80);
        backend.Limit = 70;
        int writes = backend.Applied.Count;

        Assert.Equal(ChargeActionStatus.ChangedElsewhere, (await controller.ReapplyAsync(ReapplyReason.Resume)).Status);
        Assert.Equal(writes, backend.Applied.Count);
        Assert.Equal(70, backend.Limit);

        Assert.True(controller.KeepFirmwareValue().Succeeded);
        Assert.Equal(70, Intent()!.Limit);
    }

    [Fact]
    public async Task Reapply_refused_by_another_owner_writes_nothing_and_stays_pending()
    {
        var driver = new FakeEnergyDriver();
        string? owner = null;
        var gate = new WriteGateContext { OtherOwner = () => owner is null ? null : new OtherOwnerClaim(owner) };
        ChargeLimitController controller = New(Lenovo(driver, gate));
        await controller.SetLimitAsync(true);
        await controller.StartTopUpAsync(TimeSpan.FromHours(1));
        owner = PowerPlanManagerProbe.DisplayName;

        ChargeActionResult result = await controller.ReapplyAsync(ReapplyReason.Startup);
        Assert.Equal(ChargeActionStatus.Refused, result.Status);
        Assert.Equal(WriteRefusal.OtherOwner, result.Refusal);
        Assert.False(LenovoConservationBackend.IsConservationOn(driver.ModeWord));
        Assert.True(Intent()!.NeedsReapply);
    }

    [Fact]
    public async Task Cancelled_user_write_keeps_the_previous_intent()
    {
        var driver = new FakeEnergyDriver();
        ChargeLimitController controller = New(Lenovo(driver));
        Assert.True((await controller.SetLimitAsync(true)).Succeeded);

        driver.FailWritesWith = DeviceIoResult.ErrorAccessDenied;
        var cancelling = new LenovoConservationBackend(LenovoWmiFor(), new RecordingExecutor { Result = new() { Cancelled = true } }, driver);
        Assert.True(cancelling.Detect());
        Assert.Equal(ChargeActionStatus.Cancelled, (await New(cancelling).SetLimitAsync(false)).Status);
        Assert.True(Intent()!.LimitOn);
    }

    [Fact]
    public async Task Restore_defaults_stops_the_app_managing_the_limit()
    {
        var driver = new FakeEnergyDriver();
        ChargeLimitController controller = New(Lenovo(driver));
        await controller.SetLimitAsync(true);
        Assert.True((await controller.RestoreDefaultsAsync()).Succeeded);
        Assert.False(Intent()!.Managed);
        Assert.Equal(ChargeActionStatus.NothingToDo, (await controller.ReapplyAsync(ReapplyReason.Resume)).Status);
        Assert.False(LenovoConservationBackend.IsConservationOn(driver.ModeWord));
    }

    [Fact]
    public async Task Unreadable_intent_file_blocks_writes_and_is_left_alone()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, ChargeIntentStore.FileName);
        File.WriteAllText(path, "nope");
        var driver = new FakeEnergyDriver();
        ChargeLimitController controller = New(Lenovo(driver));
        Assert.Equal(ChargeActionStatus.StateFileUnavailable, (await controller.SetLimitAsync(true)).Status);
        Assert.Equal(ChargeActionStatus.StateFileUnavailable, (await controller.ReapplyAsync(ReapplyReason.Startup)).Status);
        Assert.Equal("nope", File.ReadAllText(path));
        Assert.DoesNotContain(LenovoConservationBackend.ConservationOnCode, driver.Sent);
    }

    private static FakeWmiReader LenovoWmiFor()
    {
        var wmi = new FakeWmiReader();
        wmi.Add("Win32_ComputerSystem", new() { ["Manufacturer"] = "LENOVO", ["SystemFamily"] = "IdeaPad 5", ["Model"] = "82FG" });
        return wmi;
    }

    private sealed class SpyBackend(Func<ChargeIntent?> readIntent) : IChargeLimitBackend
    {
        public List<ChargeIntent?> IntentAtWrite { get; } = new();
        public int Limit { get; private set; } = 100;
        public string Id => "spy";
        public string Name => "Spy";
        public BackendConfidence Confidence => BackendConfidence.Simulated;
        public bool RequiresElevation => false;
        public bool Detect() => true;
        public ChargeLimitCapabilities Capabilities => Continuous;
        public ChargeLimitState GetState() => new() { Limit = Limit };
        public BackendOriginalState CaptureOriginal() => new() { BackendId = Id, Values = new() { ["Limit"] = Limit.ToString() } };

        public Task<BackendWriteResult> ApplyAsync(ChargeLimitRequest request, CancellationToken cancellationToken = default)
        {
            IntentAtWrite.Add(readIntent());
            Limit = request.Limit ?? Limit;
            return Task.FromResult(BackendWriteResult.Ok());
        }

        public Task<BackendWriteResult> RestoreAsync(BackendOriginalState original, CancellationToken cancellationToken = default) =>
            Task.FromResult(BackendWriteResult.Ok());
    }
}

public class PowerPlanManagerProbeTests : IDisposable
{
    private const string Family = PowerPlanManagerProbe.PackageNamePrefix + "_test123";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bcp-ppm-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Install() => Directory.CreateDirectory(Path.Combine(_root, "Packages", Family));

    private void WriteBackup(bool enabled, bool virtualized = false)
    {
        string folder = virtualized
            ? Path.Combine(_root, "Packages", Family, "LocalCache", "Local", "PowerPlanManagerBackup", Family)
            : Path.Combine(_root, "PowerPlanManagerBackup", Family);
        Directory.CreateDirectory(folder);
        string battery = System.Text.Json.JsonSerializer.Serialize(new { Enabled = enabled, Limit = 80 });
        string backup = System.Text.Json.JsonSerializer.Serialize(new { Version = 1, LocalSettings = new Dictionary<string, object> { ["BatterySettings"] = battery, ["Theme"] = "Dark" } });
        File.WriteAllText(Path.Combine(folder, "settings-backup.json"), backup);
    }

    [Fact]
    public void Not_installed_is_no_owner_even_with_a_leftover_backup()
    {
        WriteBackup(enabled: true);
        PowerPlanManagerStatus s = new PowerPlanManagerProbe(_root, () => false).Check();
        Assert.False(s.Installed);
        Assert.False(s.OwnsCharging);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Installed_with_battery_care_on_owns_charging(bool enabled, bool virtualized)
    {
        Install();
        WriteBackup(enabled, virtualized);
        var probe = new PowerPlanManagerProbe(_root, () => true);
        PowerPlanManagerStatus s = probe.Check();
        Assert.True(s.Installed);
        Assert.Equal(enabled ? PowerPlanManagerChargeControl.Enabled : PowerPlanManagerChargeControl.Disabled, s.ChargeControl);
        Assert.True(s.Running);
        Assert.Equal(enabled, s.OwnsCharging);
        Assert.Equal(!enabled, s.ConfirmedOff);
        Assert.Equal(enabled ? PowerPlanManagerProbe.DisplayName : null, probe.OtherOwner()?.Name);
    }

    private string BackupPath()
    {
        string folder = Path.Combine(_root, "PowerPlanManagerBackup", Family);
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, "settings-backup.json");
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("{\"Version\":1}")]
    [InlineData("{\"LocalSettings\":{\"Theme\":\"Dark\"}}")]
    [InlineData("{\"LocalSettings\":{\"BatterySettings\":\"{\\\"Limit\\\":80}\"}}")]
    [InlineData("{\"LocalSettings\":{\"BatterySettings\":\"{\\\"Enabled\\\":\\\"no\\\"}\"}}")]
    public void Installed_with_unreadable_settings_refuses_as_unconfirmed(string content)
    {
        Install();
        File.WriteAllText(BackupPath(), content);
        var probe = new PowerPlanManagerProbe(_root, () => true);
        PowerPlanManagerStatus s = probe.Check();
        Assert.True(s.Installed);
        Assert.Equal(PowerPlanManagerChargeControl.Unreadable, s.ChargeControl);
        Assert.False(s.ConfirmedOff);
        Assert.Equal(new OtherOwnerClaim(PowerPlanManagerProbe.DisplayName, Confirmed: false), probe.OtherOwner());
    }

    [Fact]
    public void Installed_without_any_settings_mirror_refuses_as_unconfirmed()
    {
        Install();
        var probe = new PowerPlanManagerProbe(_root, () => false);
        Assert.Equal(PowerPlanManagerChargeControl.Absent, probe.Check().ChargeControl);
        Assert.False(probe.OtherOwner()!.Confirmed);
    }

    [Fact]
    public void Locked_settings_mirror_is_unconfirmed_not_off()
    {
        Install();
        WriteBackup(enabled: false);
        using (new FileStream(BackupPath(), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(PowerPlanManagerChargeControl.Unreadable, new PowerPlanManagerProbe(_root, () => true).Check().ChargeControl);
        Assert.Null(new PowerPlanManagerProbe(_root, () => true).OtherOwner());
    }

    [Fact]
    public void One_unreadable_mirror_outweighs_one_that_says_off()
    {
        Install();
        WriteBackup(enabled: false);
        string virtualized = Path.Combine(_root, "Packages", Family, "LocalCache", "Local", "PowerPlanManagerBackup", Family);
        Directory.CreateDirectory(virtualized);
        File.WriteAllText(Path.Combine(virtualized, "settings-backup.json"), "{ half");
        Assert.Equal(PowerPlanManagerChargeControl.Unreadable, new PowerPlanManagerProbe(_root, () => true).Check().ChargeControl);
    }
}
