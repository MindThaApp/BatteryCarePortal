using Portal.Core.Battery;
using Portal.Core.Battery.Backends;
using Xunit;

namespace Portal.Core.Tests;

public class WriteGateTests
{
    public static IEnumerable<object?[]> AllCombinations()
    {
        bool[] tf = { false, true };
        foreach (BackendConfidence confidence in Enum.GetValues<BackendConfidence>())
        foreach (bool verified in tf)
        foreach (bool beta in tf)
        foreach (bool detected in tf)
        foreach (bool reports in tf)
        foreach (bool plan in tf)
        foreach (string? owner in new[] { null, "Power Plan Manager" })
            yield return new object?[] { confidence, verified, beta, detected, reports, plan, owner };
    }

    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void Every_gate_combination(BackendConfidence confidence, bool verified, bool beta, bool detected, bool reports, bool plan, string? owner)
    {
        WriteGateResult r = WriteGate.Evaluate(new WriteGateInput(confidence, verified, beta, detected, reports, plan, owner));

        if (confidence == BackendConfidence.Simulated)
        {
            Assert.Equal(WriteGateDecision.Simulated, r.Decision);
            return;
        }

        bool readable = reports && plan;
        bool allowed = owner is null && detected && readable && (verified || beta);
        Assert.Equal(allowed, r.Allowed);
        if (allowed)
        {
            Assert.Equal(verified ? WriteGateDecision.Verified : WriteGateDecision.BetaOptIn, r.Decision);
            Assert.Equal(WriteRefusal.None, r.Refusal);
            return;
        }

        WriteRefusal expected = owner is not null ? WriteRefusal.OtherOwner
            : !detected ? WriteRefusal.NotDetected
            : !readable ? WriteRefusal.NoReadBack
            : WriteRefusal.BetaOff;
        Assert.Equal(WriteGateDecision.Refused, r.Decision);
        Assert.Equal(expected, r.Refusal);
        Assert.False(string.IsNullOrWhiteSpace(r.Message));
    }

    [Fact]
    public void Beta_never_edits_the_verified_list()
    {
        int before = VerifiedModels.Rules.Count;
        WriteGate.Evaluate(new WriteGateInput(BackendConfidence.Unverified, false, true, true, true, true, null));
        Assert.Equal(before, VerifiedModels.Rules.Count);
        Assert.False(VerifiedModels.IsVerified("dell-bios-custom-charge", new DeviceIdentity("Dell Inc.", "Latitude", "Latitude", "")));
    }

    [Theory]
    [InlineData(80, 80, ReadBackCheck.Matched)]
    [InlineData(80, 100, ReadBackCheck.Mismatch)]
    [InlineData(80, null, ReadBackCheck.Unreadable)]
    public void Read_back_compares_the_limit(int requested, int? read, ReadBackCheck expected) =>
        Assert.Equal(expected, ReadBackVerifier.Compare(new ChargeLimitRequest { Limit = requested }, new ChargeLimitState { Limit = read }));

    [Fact]
    public void Read_back_compares_the_start_threshold()
    {
        var request = new ChargeLimitRequest { Limit = 80, StartThreshold = 75 };
        Assert.Equal(ReadBackCheck.Matched, ReadBackVerifier.Compare(request, new ChargeLimitState { Limit = 80, StartThreshold = 75 }));
        Assert.Equal(ReadBackCheck.Mismatch, ReadBackVerifier.Compare(request, new ChargeLimitState { Limit = 80, StartThreshold = 70 }));
        Assert.Equal(ReadBackCheck.Mismatch, ReadBackVerifier.Compare(request, new ChargeLimitState { Limit = 80 }));
    }
}

public class BackendGateTests
{
    private static FakeWmiReader LenovoWmi(string family)
    {
        var wmi = new FakeWmiReader();
        wmi.Add("Win32_ComputerSystem", new() { ["Manufacturer"] = "LENOVO", ["SystemFamily"] = family, ["Model"] = "82FG" });
        return wmi;
    }

    private static WriteGateContext Beta(bool on, string? owner = null) => new() { BetaOptIn = () => on, OtherOwner = () => owner };

    [Fact]
    public async Task Unverified_lenovo_writes_only_with_beta_and_reads_back()
    {
        var driver = new FakeEnergyDriver();
        var off = new LenovoConservationBackend(LenovoWmi("ThinkPad T14"), new RecordingExecutor(), driver) { GateContext = Beta(false) };
        Assert.False(off.Detect());
        BackendWriteResult refused = await off.SetLimitAsync(60);
        Assert.Equal(WriteRefusal.BetaOff, refused.Refusal);
        Assert.DoesNotContain(LenovoConservationBackend.ConservationOnCode, driver.Sent);

        var on = new LenovoConservationBackend(LenovoWmi("ThinkPad T14"), new RecordingExecutor(), driver) { GateContext = Beta(true) };
        Assert.True(on.Detect());
        Assert.Equal(WriteGateDecision.BetaOptIn, on.EvaluateGate().Decision);
        BackendWriteResult applied = await on.SetLimitAsync(60);
        Assert.True(applied.Success, applied.Message);
        Assert.Equal(ReadBackCheck.Matched, applied.ReadBackCheck);
    }

    [Fact]
    public async Task Other_owner_blocks_writes_and_restores_but_not_detection()
    {
        var driver = new FakeEnergyDriver();
        var backend = new LenovoConservationBackend(LenovoWmi("IdeaPad 5"), new RecordingExecutor(), driver)
        {
            GateContext = Beta(false, owner: "Power Plan Manager"),
        };
        Assert.True(backend.Detect());
        BackendWriteResult write = await backend.SetLimitAsync(60);
        Assert.Equal(WriteRefusal.OtherOwner, write.Refusal);
        BackendWriteResult restore = await backend.RestoreAsync(backend.CaptureOriginal());
        Assert.Equal(WriteRefusal.OtherOwner, restore.Refusal);
        Assert.DoesNotContain(LenovoConservationBackend.ConservationOnCode, driver.Sent);
        Assert.DoesNotContain(LenovoConservationBackend.ConservationOffCode, driver.Sent);
    }

    [Fact]
    public async Task App_read_back_catches_a_change_the_driver_verify_missed()
    {
        var driver = new FlakyEnergyDriver();
        var backend = new LenovoConservationBackend(LenovoWmi("IdeaPad 5"), new RecordingExecutor(), driver);
        Assert.True(backend.Detect());
        BackendWriteResult result = await backend.SetLimitAsync(60);
        Assert.False(result.Success);
        Assert.Equal(ReadBackCheck.Mismatch, result.ReadBackCheck);
        Assert.Equal(1, driver.Writes);
    }

    [Fact]
    public async Task Elevated_lenovo_write_the_app_cannot_read_is_marked_unreadable()
    {
        var driver = new FakeEnergyDriver { FailAllWith = DeviceIoResult.ErrorAccessDenied };
        var executor = new RecordingExecutor();
        var backend = new LenovoConservationBackend(LenovoWmi("IdeaPad 5"), executor, driver);
        Assert.True(backend.Detect());
        BackendWriteResult result = await backend.SetLimitAsync(60);
        Assert.True(result.Success, result.Message);
        Assert.Equal(ReadBackCheck.Unreadable, result.ReadBackCheck);
        Assert.Single(executor.Plans);
    }

    private static FakeWmiReader DellWmi(string mode = "Standard", uint start = 50, uint stop = 100)
    {
        var wmi = new FakeWmiReader();
        SetDell(wmi, mode, start, stop);
        return wmi;
    }

    private static void SetDell(FakeWmiReader wmi, string mode, uint start, uint stop)
    {
        wmi.Rows.Remove("AttributeName='PrimaryBattChargeCfg'");
        wmi.Rows.Remove("AttributeName='CustomChargeStart'");
        wmi.Rows.Remove("AttributeName='CustomChargeStop'");
        wmi.Add("AttributeName='PrimaryBattChargeCfg'", new() { ["CurrentValue"] = new[] { mode }, ["PossibleValue"] = new[] { "Standard", "Express", "Custom" } });
        wmi.Add("AttributeName='CustomChargeStart'", new() { ["CurrentValue"] = start, ["LowerBound"] = 50u, ["UpperBound"] = 95u });
        wmi.Add("AttributeName='CustomChargeStop'", new() { ["CurrentValue"] = stop, ["LowerBound"] = 55u, ["UpperBound"] = 100u });
    }

    [Fact]
    public async Task Dell_beta_off_refuses_and_beta_on_runs_one_plan_with_read_back()
    {
        FakeWmiReader wmi = DellWmi();
        var executor = new CallbackExecutor(_ => SetDell(wmi, "Custom", 75, 80));

        var off = new DellChargeLimitBackend(wmi, executor) { GateContext = Beta(false) };
        Assert.False(off.Detect());
        Assert.Equal(WriteRefusal.BetaOff, (await off.ApplyAsync(new ChargeLimitRequest { Limit = 80, StartThreshold = 75 })).Refusal);
        Assert.Empty(executor.Plans);

        var on = new DellChargeLimitBackend(wmi, executor) { GateContext = Beta(true) };
        Assert.True(on.Detect());
        BackendWriteResult result = await on.ApplyAsync(new ChargeLimitRequest { Limit = 80, StartThreshold = 75 });
        Assert.True(result.Success, result.Message);
        Assert.Equal(ReadBackCheck.Matched, result.ReadBackCheck);
        BatteryWritePlan plan = Assert.Single(executor.Plans);
        Assert.Contains(plan.Steps, s => s.Kind == BatteryWriteStepKind.VerifyWmiProperty);
    }

    [Fact]
    public async Task Dell_helper_ok_but_app_read_back_differs_is_a_failure()
    {
        FakeWmiReader wmi = DellWmi();
        var executor = new CallbackExecutor(_ => { });
        var backend = new DellChargeLimitBackend(wmi, executor) { GateContext = Beta(true) };
        Assert.True(backend.Detect());
        BackendWriteResult result = await backend.ApplyAsync(new ChargeLimitRequest { Limit = 80, StartThreshold = 75 });
        Assert.False(result.Success);
        Assert.Equal(ReadBackCheck.Mismatch, result.ReadBackCheck);
        Assert.Single(executor.Plans);
    }

    [Fact]
    public async Task Asus_stays_read_only_even_with_beta()
    {
        var executor = new RecordingExecutor();
        var backend = new AsusChargeLimitBackend(new AsusWmi(), executor) { GateContext = Beta(true) };
        Assert.True(backend.ProbeForDiagnostics());
        Assert.False(backend.Detect());
        BackendWriteResult result = await backend.SetLimitAsync(80);
        Assert.False(result.Success);
        Assert.Equal(WriteRefusal.NoReadBack, result.Refusal);
        Assert.Empty(executor.Plans);
    }

    [Fact]
    public async Task Undetected_backend_never_writes()
    {
        var executor = new RecordingExecutor();
        var backend = new HpChargeLimitBackend(new FakeWmiReader(), executor) { GateContext = Beta(true) };
        Assert.False(backend.Detect());
        Assert.Equal(WriteRefusal.NotDetected, (await backend.SetLimitAsync(80)).Refusal);
        Assert.Empty(executor.Plans);
    }

    private sealed class FlakyEnergyDriver : ILenovoEnergyDriver
    {
        private uint _word;
        private int _queriesSinceWrite = -1;
        public int Writes { get; private set; }

        public DeviceIoResult Send(uint input)
        {
            if (input == LenovoConservationBackend.QueryCode)
            {
                if (_queriesSinceWrite >= 0 && ++_queriesSinceWrite > 1) _word = 0;
                return new DeviceIoResult(true, _word, 0);
            }
            Writes++;
            _queriesSinceWrite = 0;
            if (input == LenovoConservationBackend.ConservationOnCode) _word = 1u << LenovoConservationBackend.ConservationBit;
            return new DeviceIoResult(true, 0, 0);
        }
    }

    private sealed class AsusWmi : IWmiReader
    {
        public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string wmiNamespace, string wql) =>
            Array.Empty<IReadOnlyDictionary<string, object?>>();

        public IReadOnlyDictionary<string, object?>? InvokeGetter(string wmiNamespace, string className, string methodName,
            IReadOnlyDictionary<string, object> arguments) => new Dictionary<string, object?> { ["device_status"] = 0x00010000L };
    }
}

public sealed class CallbackExecutor(Action<BatteryWritePlan> onExecute) : IBatteryWriteExecutor
{
    public List<BatteryWritePlan> Plans { get; } = new();
    public BatteryWritePlanResult Result { get; set; } = new() { Ok = true };

    public Task<BatteryWritePlanResult> ExecuteAsync(BatteryWritePlan plan, CancellationToken cancellationToken = default)
    {
        Plans.Add(plan);
        if (Result.Ok) onExecute(plan);
        return Task.FromResult(Result);
    }
}

public class BatterySettingsStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bcp-settings-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    [Fact]
    public void Confirmed_beta_opt_in_is_persisted()
    {
        var store = new BatterySettingsStore(_folder);
        Assert.False(store.Load().BetaOtherBrandsOptIn);
        store.Update(s => s.ConfirmBetaOptIn(DateTimeOffset.UnixEpoch));
        Assert.True(new BatterySettingsStore(_folder).Load().BetaOtherBrandsOptIn);
        store.Update(s => s.TurnOffBetaOptIn());
        Assert.False(new BatterySettingsStore(_folder).Load().BetaOtherBrandsOptIn);
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public void Unconfirmed_beta_flag_in_the_file_loads_as_off()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, BatterySettingsStore.FileName), "{\"BetaOtherBrandsOptIn\":true}");
        Assert.False(new BatterySettingsStore(_folder).Load().BetaOtherBrandsOptIn);
    }

    [Fact]
    public void Unreadable_file_loads_defaults()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, BatterySettingsStore.FileName), "{ not json");
        BatterySettings s = new BatterySettingsStore(_folder).Load();
        Assert.False(s.BetaOtherBrandsOptIn);
        Assert.Equal(80, s.Limit);
    }
}
