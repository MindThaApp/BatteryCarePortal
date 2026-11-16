using Portal.Core.Battery;
using Portal.Core.Battery.Backends;
using Xunit;

namespace Portal.Core.Tests;

public class ChargeLimitControllerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bcp-controller-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    private static FakeWmiReader LenovoWmi(string family = "IdeaPad 5 15ITL05")
    {
        var wmi = new FakeWmiReader();
        wmi.Add("Win32_ComputerSystem", new() { ["Manufacturer"] = "LENOVO", ["SystemFamily"] = family, ["Model"] = "82FG" });
        return wmi;
    }

    private (ChargeLimitController Controller, FakeEnergyDriver Driver, RecordingExecutor Executor) Lenovo(
        FakeEnergyDriver? driver = null, WriteGateContext? gate = null, string family = "IdeaPad 5 15ITL05")
    {
        driver ??= new FakeEnergyDriver();
        var executor = new RecordingExecutor();
        var backend = new LenovoConservationBackend(LenovoWmi(family), executor, driver) { GateContext = gate ?? WriteGateContext.Default };
        Assert.True(backend.Detect());
        return (new ChargeLimitController(backend, new OriginalStateStore(_folder)), driver, executor);
    }

    private string OriginalsPath => Path.Combine(_folder, OriginalStateStore.FileName);

    private const uint On = 1u << LenovoConservationBackend.ConservationBit;

    [Fact]
    public async Task First_write_saves_originals_and_later_writes_never_overwrite_them()
    {
        var (controller, driver, _) = Lenovo();

        ChargeActionResult on = await controller.SetLimitAsync(true);
        Assert.Equal(ChargeActionStatus.Applied, on.Status);
        Assert.Equal(60, on.State!.Limit);
        BackendOriginalState? saved = new OriginalStateStore(_folder).Get("lenovo-conservation", out _);
        Assert.Equal("Off", saved!.Values["Conservation"]);

        string before = File.ReadAllText(OriginalsPath);
        driver.ModeWord = On;
        Assert.True((await controller.SetLimitAsync(false)).Succeeded);
        Assert.True((await controller.SetLimitAsync(true)).Succeeded);
        Assert.Equal(before, File.ReadAllText(OriginalsPath));
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Unreadable_firmware_means_no_originals_and_no_write()
    {
        var (controller, driver, executor) = Lenovo(new FakeEnergyDriver { FailAllWith = DeviceIoResult.ErrorAccessDenied });
        ChargeActionResult result = await controller.SetLimitAsync(true);
        Assert.Equal(ChargeActionStatus.OriginalsUnavailable, result.Status);
        Assert.Empty(executor.Plans);
        Assert.DoesNotContain(LenovoConservationBackend.ConservationOnCode, driver.Sent);
        Assert.False(File.Exists(OriginalsPath));
    }

    [Fact]
    public async Task Unreadable_originals_file_is_left_alone_and_blocks_writes_and_restore()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(OriginalsPath, "{ broken");
        var (controller, driver, _) = Lenovo();

        Assert.Equal(ChargeActionStatus.OriginalsUnavailable, (await controller.SetLimitAsync(true)).Status);
        Assert.Equal(ChargeActionStatus.OriginalsUnavailable, (await controller.RestoreDefaultsAsync()).Status);
        Assert.Equal("{ broken", File.ReadAllText(OriginalsPath));
        Assert.DoesNotContain(LenovoConservationBackend.ConservationOnCode, driver.Sent);
    }

    [Fact]
    public async Task Restore_puts_the_originals_back_and_reads_back()
    {
        var (controller, driver, _) = Lenovo();
        Assert.True((await controller.SetLimitAsync(true)).Succeeded);
        Assert.True(LenovoConservationBackend.IsConservationOn(driver.ModeWord));

        ChargeActionResult restored = await controller.RestoreDefaultsAsync();
        Assert.Equal(ChargeActionStatus.Applied, restored.Status);
        Assert.Equal(100, restored.State!.Limit);
        Assert.False(LenovoConservationBackend.IsConservationOn(driver.ModeWord));
        Assert.True(File.Exists(OriginalsPath));
    }

    [Fact]
    public async Task Restore_keeps_an_original_that_was_on()
    {
        var (controller, driver, _) = Lenovo(new FakeEnergyDriver { ModeWord = On });
        Assert.True((await controller.SetLimitAsync(false)).Succeeded);
        Assert.False(LenovoConservationBackend.IsConservationOn(driver.ModeWord));
        Assert.True((await controller.RestoreDefaultsAsync()).Succeeded);
        Assert.True(LenovoConservationBackend.IsConservationOn(driver.ModeWord));
    }

    [Fact]
    public async Task Restore_without_originals_changes_nothing()
    {
        var (controller, driver, _) = Lenovo();
        Assert.Equal(ChargeActionStatus.NothingToRestore, (await controller.RestoreDefaultsAsync()).Status);
        Assert.All(driver.Sent, c => Assert.Equal(LenovoConservationBackend.QueryCode, c));
    }

    [Fact]
    public async Task Restore_that_does_not_read_back_is_a_mismatch()
    {
        var (controller, driver, _) = Lenovo();
        Assert.True((await controller.SetLimitAsync(true)).Succeeded);
        driver.IgnoreWrites = true;
        ChargeActionResult result = await controller.RestoreDefaultsAsync();
        Assert.Equal(ChargeActionStatus.ReadBackMismatch, result.Status);
    }

    [Fact]
    public async Task Refused_gate_creates_no_files_and_writes_nothing()
    {
        var driver = new FakeEnergyDriver();
        var backend = new LenovoConservationBackend(LenovoWmi("ThinkPad T14"), new RecordingExecutor(), driver);
        backend.ProbeForDiagnostics();
        var controller = new ChargeLimitController(backend, new OriginalStateStore(_folder));

        ChargeActionResult result = await controller.SetLimitAsync(true);
        Assert.Equal(ChargeActionStatus.Refused, result.Status);
        Assert.Equal(WriteRefusal.BetaOff, result.Refusal);
        Assert.False(Directory.Exists(_folder));
        Assert.DoesNotContain(LenovoConservationBackend.ConservationOnCode, driver.Sent);
    }

    [Fact]
    public async Task Cancelled_administrator_prompt_is_reported()
    {
        var driver = new FakeEnergyDriver { FailWritesWith = DeviceIoResult.ErrorAccessDenied };
        var (controller, _, executor) = Lenovo(driver);
        executor.Result = new BatteryWritePlanResult { Ok = false, Cancelled = true };
        Assert.Equal(ChargeActionStatus.Cancelled, (await controller.SetLimitAsync(true)).Status);
    }

    [Fact]
    public async Task Second_write_while_one_is_in_flight_is_rejected()
    {
        var backend = new BlockingBackend();
        var controller = new ChargeLimitController(backend, new OriginalStateStore(_folder));
        Task<ChargeActionResult> first = controller.SetLimitAsync(true, 80);
        Assert.True(controller.IsBusy);
        Assert.Equal(ChargeActionStatus.Busy, (await controller.SetLimitAsync(false)).Status);
        Assert.Equal(ChargeActionStatus.Busy, (await controller.RestoreDefaultsAsync()).Status);
        backend.Release.SetResult();
        Assert.True((await first).Succeeded);
        Assert.Equal(1, backend.Writes);
        Assert.False(controller.IsBusy);
    }

    [Fact]
    public async Task Fake_battery_round_trip_never_needs_real_hardware()
    {
        var battery = new FakeBattery();
        var controller = new ChargeLimitController(battery.Backend, new OriginalStateStore(_folder));
        Assert.Equal(WriteGateDecision.Simulated, controller.EvaluateGate().Decision);
        Assert.True((await controller.SetLimitAsync(true, 75)).Succeeded);
        Assert.Equal(75, battery.Limit);
        Assert.True((await controller.RestoreDefaultsAsync()).Succeeded);
        Assert.Equal(100, battery.Limit);
    }

    [Theory]
    [InlineData(true, null, 60)]
    [InlineData(true, 85, 60)]
    [InlineData(false, null, 100)]
    public void Conservation_target_is_what_the_firmware_does(bool on, int? value, int expected)
    {
        var (controller, _, _) = Lenovo();
        Assert.Equal(expected, controller.TargetLimit(on, value));
    }

    internal sealed class BlockingBackend : IChargeLimitBackend
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Writes { get; private set; }
        public int Limit { get; private set; } = 100;
        public string Id => "blocking";
        public string Name => "Blocking";
        public BackendConfidence Confidence => BackendConfidence.Simulated;
        public bool RequiresElevation => false;
        public bool Detect() => true;
        public ChargeLimitCapabilities Capabilities { get; } = new() { Kind = ChargeLimitKind.ContinuousRange, MinLimit = 50, MaxLimit = 100 };
        public ChargeLimitState GetState() => new() { Limit = Limit };
        public BackendOriginalState CaptureOriginal() => new() { BackendId = Id, Values = new() { ["Limit"] = Limit.ToString() } };

        public async Task<BackendWriteResult> ApplyAsync(ChargeLimitRequest request, CancellationToken cancellationToken = default)
        {
            await Release.Task;
            Writes++;
            Limit = request.Limit ?? Limit;
            return BackendWriteResult.Ok();
        }

        public Task<BackendWriteResult> RestoreAsync(BackendOriginalState original, CancellationToken cancellationToken = default) =>
            Task.FromResult(BackendWriteResult.Ok());
    }
}
