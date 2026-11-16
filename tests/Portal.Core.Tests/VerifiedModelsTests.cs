using Portal.Core.Battery;
using Portal.Core.Battery.Backends;

using Xunit;

namespace Portal.Core.Tests;

public class VerifiedModelsTests
{
    private static FakeWmiReader Wmi(string maker, string family, string version = "")
    {
        var wmi = new FakeWmiReader();
        wmi.Add("Win32_ComputerSystem", new() { ["Manufacturer"] = maker, ["SystemFamily"] = family, ["Model"] = "82FG", ["Version"] = version });
        if (version.Length > 0) wmi.Add("Win32_ComputerSystemProduct", new() { ["Version"] = version });
        return wmi;
    }

    [Theory]
    [InlineData("LENOVO", "IdeaPad 5 15ITL05", "", true)]
    [InlineData("LENOVO", "Yoga Slim 7 14ITL05", "", true)]
    [InlineData("LENOVO", "", "Lenovo Yoga 7 14ACN6", true)]
    [InlineData("LENOVO", "ThinkPad X1 Yoga Gen 6", "", false)]
    [InlineData("LENOVO", "Legion 5 15ACH6", "", false)]
    [InlineData("LENOVO", "", "", false)]
    [InlineData("Dell Inc.", "IdeaPad", "", false)]
    public void Lenovo_conservation_is_allowed_only_on_ideapad_and_yoga(string maker, string family, string version, bool expected)
    {
        DeviceIdentity device = DeviceIdentity.Read(Wmi(maker, family, version));
        Assert.Equal(expected, VerifiedModels.IsVerified("lenovo-conservation", device));
    }

    [Fact]
    public void Dell_hp_and_asus_backends_have_no_rule()
    {
        var device = new DeviceIdentity("Dell Inc.", "Latitude 7420", "Latitude", "");
        foreach (string id in new[] { "dell-bios-custom-charge", "hp-bios-battery-care", "asus-atk-charge-limit" })
            Assert.DoesNotContain(VerifiedModels.Rules, r => r.BackendId == id);
        Assert.False(VerifiedModels.IsVerified("dell-bios-custom-charge", device));
    }

    [Fact]
    public async Task Lenovo_off_the_list_detects_nothing_and_never_writes()
    {
        var driver = new FakeEnergyDriver();
        var executor = new RecordingExecutor();
        var backend = new LenovoConservationBackend(Wmi("LENOVO", "ThinkPad T14"), executor, driver);

        Assert.False(backend.Detect());
        BackendWriteResult result = await backend.SetLimitAsync(60);
        Assert.False(result.Success);
        Assert.Empty(executor.Plans);
        Assert.DoesNotContain(LenovoConservationBackend.ConservationOnCode, driver.Sent);
    }

    [Fact]
    public async Task A_plan_that_cannot_be_read_back_is_refused()
    {
        var plan = new BatteryWritePlan { BackendId = "lenovo-conservation", Description = "no verify", Steps = { new BatteryWriteStep { Kind = BatteryWriteStepKind.WmiMethod, Label = "w" } } };
        var executor = new RecordingExecutor();
        var backend = new NoReadBackBackend(Wmi("LENOVO", "IdeaPad 3"), executor, plan);

        Assert.True(backend.Detect());
        BackendWriteResult result = await backend.SetLimitAsync(60);

        Assert.False(result.Success);
        Assert.Contains("read back", result.Message);
        Assert.Empty(executor.Plans);
    }

    private sealed class NoReadBackBackend(IWmiReader wmi, IBatteryWriteExecutor executor, BatteryWritePlan plan)
        : ChargeLimitBackendBase(wmi, executor, null)
    {
        public override string Id => "lenovo-conservation";
        public override string Name => "t";
        public override BackendConfidence Confidence => BackendConfidence.Verified;
        protected override bool DetectCore() { Capabilities = new ChargeLimitCapabilities { Kind = ChargeLimitKind.OnOffConservation }; return true; }
        protected override ChargeLimitState GetStateCore() => new() { Limit = 100 };
        public override BackendOriginalState CaptureOriginal() => NewOriginal(new()); protected override ChargeLimitState? ExpectedAfterRestoreCore(BackendOriginalState original) => new() { Limit = 100 };
        protected override BatteryWritePlan? BuildPlan(ChargeLimitRequest request, ChargeLimitState current, out string? error) { error = null; return plan; }
        protected override BatteryWritePlan? BuildRestorePlan(BackendOriginalState original, ChargeLimitState current, out string? error) { error = null; return plan; }
    }
}
