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

    // One lock per test, like the one process-wide lock in the app; tests run in parallel.
    private readonly ChargeWriteLock _lock = new();

    private ChargeLimitController New(IChargeLimitBackend backend) =>
        new(backend, new OriginalStateStore(_folder), new ChargeIntentStore(_folder), _time, _lock);

    private ChargeIntent? Intent() => new ChargeIntentStore(_folder).Load(out ChargeIntent? i) == AtomicJsonFile.ReadStatus.Ok ? i : null;

    /// <summary>A running top-up on a backend that can no longer start one (saved by an older build), with the limit off.</summary>
    private void SeedTopUp(ChargeLimitController controller, TestBackend backend)
    {
        Assert.True(new ChargeIntentStore(_folder).TrySave(controller.LoadIntent()! with { TopUpActive = true, TopUpUntil = _time.GetUtcNow().AddHours(12) }));
        backend.Limit = 100;
    }

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
        SeedTopUp(controller, backend);
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
        // Only the two user actions wrote; both unattended attempts stopped before writing.
        Assert.Equal(2, backend.Applied.Count);
        Assert.All(backend.Applied, r => Assert.True(r.AllowElevationPrompt));
    }

    [Fact]
    public async Task Session_end_puts_the_limit_back_in_process_only()
    {
        var driver = new FakeEnergyDriver();
        ChargeLimitController lenovo = New(Lenovo(driver));
        await lenovo.SetLimitAsync(true);
        await lenovo.StartTopUpAsync(TimeSpan.FromHours(12));
        Assert.True(lenovo.ReapplyOnSessionEnd(ChargeLimitController.DefaultExitBudget).Succeeded);
        Assert.True(LenovoConservationBackend.IsConservationOn(driver.ModeWord));

        // The elevated-helper kind is left alone: no write, no prompt, the intent stays for the next start.
        var elevated = new TestBackend(Continuous, requiresElevation: true);
        string otherFolder = Path.Combine(_folder, "elevated");
        var controller = new ChargeLimitController(elevated, new OriginalStateStore(otherFolder), new ChargeIntentStore(otherFolder), _time, _lock);
        await controller.SetLimitAsync(true, 80);
        new ChargeIntentStore(otherFolder).TrySave(controller.LoadIntent()! with { TopUpActive = true, TopUpUntil = _time.GetUtcNow().AddHours(12) });
        elevated.Limit = 100;
        int writes = elevated.Applied.Count;
        ChargeActionResult result = controller.ReapplyOnSessionEnd(ChargeLimitController.DefaultExitBudget);
        Assert.Equal(ChargeActionStatus.NothingToDo, result.Status);
        Assert.Equal(writes, elevated.Applied.Count);
        Assert.True(controller.LoadIntent()!.TopUpActive);
    }

    [Fact]
    public void Simulated_mode_keeps_its_state_in_a_separate_folder()
    {
        string real = Path.Combine(_folder, "LocalState");
        Assert.Equal(real, AppStateFolder.Resolve(real, simulated: false));
        string simulated = AppStateFolder.Resolve(real, simulated: true);
        Assert.NotEqual(real, simulated);
        Assert.Equal(real, Path.GetDirectoryName(simulated));
        Assert.NotEqual(new ChargeIntentStore(real).Path, new ChargeIntentStore(simulated).Path);
        Assert.NotEqual(new OriginalStateStore(real).Path, new OriginalStateStore(simulated).Path);
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

    private static int Writes(FakeEnergyDriver driver) =>
        driver.Sent.Count(c => c is LenovoConservationBackend.ConservationOnCode or LenovoConservationBackend.ConservationOffCode);

    [Fact]
    public async Task Start_and_wake_without_pending_flags_never_override_a_change_made_elsewhere()
    {
        var driver = new FakeEnergyDriver();
        await New(Lenovo(driver)).SetLimitAsync(true);
        driver.ModeWord = 0; // turned off in another tool

        int writesBefore = Writes(driver);
        Assert.Equal(ChargeActionStatus.ChangedElsewhere, (await New(Lenovo(driver)).ReapplyAsync(ReapplyReason.Startup)).Status);
        Assert.Equal(ChargeActionStatus.ChangedElsewhere, (await New(Lenovo(driver)).ReapplyAsync(ReapplyReason.Resume)).Status);
        Assert.Equal(writesBefore, Writes(driver));
        Assert.False(LenovoConservationBackend.IsConservationOn(driver.ModeWord));
    }

    [Theory]
    [InlineData(true, 100, true, false)]
    [InlineData(true, 70, false, true)]
    [InlineData(false, 60, false, true)]
    public async Task Inconsistent_intent_is_never_acted_on(bool limitOn, int limit, bool topUp, bool needsReapply)
    {
        var driver = new FakeEnergyDriver();
        ChargeLimitController controller = New(Lenovo(driver));
        Assert.True((await controller.SetLimitAsync(true)).Succeeded);
        driver.ModeWord = 0;
        new ChargeIntentStore(_folder).TrySave(new ChargeIntent
        {
            BackendId = "lenovo-conservation", Managed = true, LimitOn = limitOn, Limit = limit, TopUpActive = topUp, NeedsReapply = needsReapply,
        });
        string before = File.ReadAllText(Path.Combine(_folder, ChargeIntentStore.FileName));
        int writes = Writes(driver);

        Assert.Equal(IntentFileState.Unusable, controller.IntentState);
        Assert.Null(controller.LoadIntent());
        foreach (ReapplyReason reason in Enum.GetValues<ReapplyReason>())
            Assert.Equal(ChargeActionStatus.StateFileUnavailable, (await controller.ReapplyAsync(reason)).Status);
        Assert.Equal(ChargeActionStatus.StateFileUnavailable, (await controller.StartTopUpAsync(TimeSpan.FromHours(1))).Status);
        Assert.Equal(writes, Writes(driver));
        Assert.Equal(before, File.ReadAllText(Path.Combine(_folder, ChargeIntentStore.FileName)));
    }

    [Fact]
    public async Task Driver_error_before_the_write_reverts_the_intent()
    {
        var driver = new FakeEnergyDriver();
        ChargeLimitController controller = New(Lenovo(driver));
        Assert.True((await controller.SetLimitAsync(true)).Succeeded);

        driver.FailWritesWith = 31;
        ChargeActionResult result = await controller.SetLimitAsync(false);
        Assert.Equal(ChargeActionStatus.Failed, result.Status);
        Assert.True(result.NotAttempted);
        Assert.True(Intent()!.LimitOn);
    }

    [Fact]
    public async Task Pre_read_failure_reverts_the_intent()
    {
        var driver = new FakeEnergyDriver();
        ChargeLimitController controller = New(Lenovo(driver));
        Assert.True((await controller.SetLimitAsync(true)).Succeeded);

        driver.FailAllWith = 31;
        ChargeActionResult result = await controller.SetLimitAsync(false);
        Assert.True(result.NotAttempted, result.Message);
        Assert.True(Intent()!.LimitOn);
    }

    [Fact]
    public async Task Uncertain_failure_keeps_the_new_intent()
    {
        var driver = new FakeEnergyDriver();
        ChargeLimitController controller = New(Lenovo(driver));
        Assert.True((await controller.SetLimitAsync(true)).Succeeded);

        driver.IgnoreWrites = true;
        ChargeActionResult result = await controller.SetLimitAsync(false);
        Assert.Equal(ChargeActionStatus.ReadBackMismatch, result.Status);
        Assert.False(result.NotAttempted);
        Assert.False(Intent()!.LimitOn);
    }

    [Fact]
    public async Task Failed_unattended_reapply_is_never_retried_automatically()
    {
        var driver = new FakeEnergyDriver();
        ChargeLimitController controller = New(Lenovo(driver));
        Assert.True((await controller.SetLimitAsync(true)).Succeeded);
        Assert.True((await controller.StartTopUpAsync(TimeSpan.FromHours(1))).Succeeded);

        driver.IgnoreWrites = true;
        Assert.Equal(ChargeActionStatus.ReadBackMismatch, (await New(Lenovo(driver)).ReapplyAsync(ReapplyReason.Startup)).Status);
        ChargeIntent failed = Intent()!;
        Assert.True(failed.ReapplyFailed);
        Assert.False(failed.TopUpActive);
        Assert.False(failed.NeedsReapply);

        driver.IgnoreWrites = false;
        int writes = Writes(driver);
        foreach (ReapplyReason reason in Enum.GetValues<ReapplyReason>())
        {
            ChargeActionResult again = await New(Lenovo(driver)).ReapplyAsync(reason);
            Assert.Equal(ChargeActionStatus.Failed, again.Status);
            Assert.True(again.NotAttempted);
        }
        controller.ReapplyOnExit(ChargeLimitController.DefaultExitBudget);
        Assert.Equal(writes, Writes(driver));
        Assert.False(Intent()!.NeedsReapply);

        // The user puts it back; that clears the flag.
        Assert.True((await controller.SetLimitAsync(true)).Succeeded);
        Assert.False(Intent()!.ReapplyFailed);
    }

    [Fact]
    public async Task Unreadable_intent_file_does_not_block_restore_and_is_kept_aside()
    {
        var driver = new FakeEnergyDriver();
        ChargeLimitController controller = New(Lenovo(driver));
        Assert.True((await controller.SetLimitAsync(true)).Succeeded);
        File.WriteAllText(Path.Combine(_folder, ChargeIntentStore.FileName), "nope");

        Assert.True((await controller.RestoreDefaultsAsync()).Succeeded);
        Assert.False(LenovoConservationBackend.IsConservationOn(driver.ModeWord));
        Assert.False(Intent()!.Managed);
        string kept = Assert.Single(Directory.GetFiles(_folder, ChargeIntentStore.FileName + ".corrupt-*"));
        Assert.Equal("nope", File.ReadAllText(kept));
    }

    [Fact]
    public void Reset_app_state_renames_the_file_and_never_overwrites()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, ChargeIntentStore.FileName);
        File.WriteAllText(path, "nope");
        string taken = path + ".corrupt-20261116T090000Z";
        File.WriteAllText(taken, "older");
        var driver = new FakeEnergyDriver();
        ChargeLimitController controller = New(Lenovo(driver));
        Assert.Equal(IntentFileState.Unusable, controller.IntentState);

        ChargeActionResult reset = controller.ResetAppState();
        Assert.Equal(ChargeActionStatus.StateReset, reset.Status);
        Assert.False(File.Exists(path));
        Assert.Equal("older", File.ReadAllText(taken));
        Assert.Equal("nope", File.ReadAllText(taken + " (2)"));
        Assert.Equal(IntentFileState.Ok, controller.IntentState);
        Assert.Equal(ChargeActionStatus.NothingToDo, controller.ResetAppState().Status);
        Assert.Equal(2, Directory.GetFiles(_folder).Length);
        Assert.DoesNotContain(LenovoConservationBackend.ConservationOnCode, driver.Sent);
        Assert.DoesNotContain(LenovoConservationBackend.ConservationOffCode, driver.Sent);
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
    [InlineData("[]")]
    [InlineData("{\"LocalSettings\":{\"BatterySettings\":5}}")]
    [InlineData("{\"LocalSettings\":{\"BatterySettings\":\"{ half\"}}")]
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

    [Theory]
    [InlineData("{\"LocalSettings\":{\"Theme\":\"Dark\"}}")]
    [InlineData("{\"LocalSettings\":{\"BatterySettings\":\"{\\\"Limit\\\":80}\"}}")]
    public void Readable_mirror_without_battery_settings_or_enabled_key_is_confirmed_off(string content)
    {
        Install();
        File.WriteAllText(BackupPath(), content);
        var probe = new PowerPlanManagerProbe(_root, () => true);
        PowerPlanManagerStatus s = probe.Check();
        Assert.Equal(PowerPlanManagerChargeControl.Disabled, s.ChargeControl);
        Assert.True(s.ConfirmedOff);
        Assert.Null(probe.OtherOwner());
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
