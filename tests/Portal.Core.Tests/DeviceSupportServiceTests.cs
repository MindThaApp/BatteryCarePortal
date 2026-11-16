using Portal.Core.Battery;
using Portal.Core.Battery.Backends;
using Xunit;

namespace Portal.Core.Tests;

public class DeviceSupportServiceTests
{
    private static readonly DeviceIdentity Yoga = new("LENOVO", "82FG", "Yoga Slim 7 14ITL05", "");
    private static readonly DeviceIdentity Dell = new("Dell Inc.", "Latitude 7420", "Latitude", "");
    private static readonly DeviceIdentity Desktop = new("ASUSTeK", "Z790", "", "");

    private static FakeWmiReader Wmi(DeviceIdentity d)
    {
        var wmi = new FakeWmiReader();
        wmi.Add("Win32_ComputerSystem", new() { ["Manufacturer"] = d.Manufacturer, ["SystemFamily"] = d.Family, ["Model"] = d.Model });
        return wmi;
    }

    private static BackendProbe Probe(bool verified, bool detected, bool probed, int? limit = null) =>
        new("id", "name", verified, detected, probed, ChargeLimitCapabilities.Unsupported, limit);

    private static DeviceSupport Classify(bool battery, DeviceIdentity device, bool beta = false, params BackendProbe[] probes) =>
        DeviceSupportService.Classify(new DeviceSupportInputs(battery, device, probes), beta);

    [Fact]
    public void NoBattery_WinsOverEverything() =>
        Assert.Equal(DeviceSupportState.NoBattery, Classify(false, Yoga, false, Probe(true, true, true)).State);

    [Fact]
    public void DetectedVerifiedBackend_IsSupported_WithReadOnlyLimit()
    {
        DeviceSupport s = Classify(true, Yoga, false, Probe(true, false, false), Probe(true, true, true, limit: 60));
        Assert.Equal(DeviceSupportState.Supported, s.State);
        Assert.True(s.ChargeLimitAvailable);
        Assert.Equal(60, s.CurrentLimit);
    }

    [Fact]
    public void VerifiedModelWithoutDriver_IsOneStepMissing()
    {
        DeviceSupport s = Classify(true, Yoga, false, Probe(true, false, false));
        Assert.Equal(DeviceSupportState.OneStepMissing, s.State);
        Assert.False(s.ChargeLimitAvailable);
    }

    [Fact]
    public void UnverifiedModelWithInterface_IsOnTestList()
    {
        DeviceSupport s = Classify(true, Dell, false, Probe(false, false, true));
        Assert.Equal(DeviceSupportState.OnTestList, s.State);
        Assert.Single(s.UnverifiedFinds);
        Assert.False(s.ChargeLimitAvailable);
    }

    [Fact]
    public void NothingFound_IsMonitorMode()
    {
        Assert.Equal(DeviceSupportState.MonitorMode, Classify(true, Desktop, false, Probe(false, false, false)).State);
        Assert.Equal(DeviceSupportState.MonitorMode, Classify(true, Desktop).State);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void BetaFlagAlone_NeverChangesStateOrEnablesWrites(bool verified, bool detected, bool probed)
    {
        BackendProbe p = Probe(verified, detected, probed);
        DeviceSupport off = Classify(true, Dell, false, p);
        DeviceSupport on = Classify(true, Dell, true, p);
        Assert.Equal(off.State, on.State);
        Assert.Equal(off.ChargeLimitAvailable, on.ChargeLimitAvailable);
        Assert.True(on.BetaOptIn);
    }

    [Fact]
    public void BetaDetectedBackend_StaysOnTestList_ButIsAvailable()
    {
        BackendProbe beta = Probe(false, true, true) with { Beta = true };
        DeviceSupport s = Classify(true, Dell, true, beta);
        Assert.Equal(DeviceSupportState.OnTestList, s.State);
        Assert.True(s.BetaActive);
        Assert.True(s.ChargeLimitAvailable);
        Assert.Same(beta, s.Backend);
    }

    [Fact]
    public void BetaSetting_IsOffByDefault_AndNormalizeResetsAnUnconfirmedOptIn()
    {
        var settings = new BatterySettings();
        Assert.False(settings.BetaOtherBrandsOptIn);
        settings.BetaOtherBrandsOptIn = true;
        Assert.False(settings.Normalize().BetaOtherBrandsOptIn);
    }

    [Fact]
    public void BetaSetting_ConfirmedOptIn_SurvivesNormalize_AndTurnsOffCleanly()
    {
        var settings = new BatterySettings();
        settings.ConfirmBetaOptIn(DateTimeOffset.UnixEpoch);
        Assert.True(settings.Normalize().BetaOtherBrandsOptIn);
        settings.TurnOffBetaOptIn();
        Assert.False(settings.Normalize().BetaOtherBrandsOptIn);
        Assert.Null(settings.BetaOptInConfirmedUtc);
    }

    [Fact]
    public void Gather_UnverifiedDell_WithBetaOptIn_DetectsAsBeta()
    {
        var backend = new StubBackend("dell-bios-custom-charge", BackendConfidence.Unverified, Wmi(Dell), detectCore: true)
        {
            GateContext = new WriteGateContext { BetaOptIn = () => true },
        };
        IReadOnlyList<BackendProbe> probes = DeviceSupportService.Gather([backend], Dell);
        Assert.True(probes[0].Detected);
        Assert.True(probes[0].Beta);
        DeviceSupport s = DeviceSupportService.Classify(new(true, Dell, probes), betaOptIn: true);
        Assert.Equal(DeviceSupportState.OnTestList, s.State);
        Assert.True(s.ChargeLimitAvailable);
    }

    [Fact]
    public void Gather_UnverifiedDell_ProbesButIsNotDetected()
    {
        var backend = new StubBackend("dell-bios-custom-charge", BackendConfidence.Unverified, Wmi(Dell), detectCore: true);
        IReadOnlyList<BackendProbe> probes = DeviceSupportService.Gather([backend], Dell);
        Assert.False(probes[0].Verified);
        Assert.False(probes[0].Detected);
        Assert.True(probes[0].Probed);
        Assert.Equal(DeviceSupportState.OnTestList, DeviceSupportService.Classify(new(true, Dell, probes), betaOptIn: true).State);
    }

    [Fact]
    public void Gather_VerifiedYoga_DetectsAndReadsLimit()
    {
        var backend = new StubBackend("lenovo-conservation", BackendConfidence.Verified, Wmi(Yoga), detectCore: true);
        DeviceSupport s = DeviceSupportService.Classify(new(true, Yoga, DeviceSupportService.Gather([backend], Yoga)));
        Assert.Equal(DeviceSupportState.Supported, s.State);
        Assert.Equal(100, s.CurrentLimit);
    }

    [Fact]
    public void Gather_VerifiedYogaWithoutDriver_IsOneStepMissing()
    {
        var backend = new StubBackend("lenovo-conservation", BackendConfidence.Verified, Wmi(Yoga), detectCore: false);
        Assert.Equal(DeviceSupportState.OneStepMissing,
            DeviceSupportService.Classify(new(true, Yoga, DeviceSupportService.Gather([backend], Yoga))).State);
    }

    [Fact]
    public void Gather_ThrowingBackend_ReadsAsNotFound()
    {
        IReadOnlyList<BackendProbe> probes = DeviceSupportService.Gather([new ThrowingBackend()], Desktop);
        Assert.Equal(DeviceSupportState.MonitorMode, DeviceSupportService.Classify(new(true, Desktop, probes)).State);
    }

    [Fact]
    public void FakeBackend_IsSupported_AndGatheringDoesNotWrite()
    {
        var battery = new FakeBattery();
        var device = new DeviceIdentity("Simulated", "Simulated laptop", "", "");
        DeviceSupport s = DeviceSupportService.Classify(new(true, device, DeviceSupportService.Gather([battery.Backend], device)));
        Assert.Equal(DeviceSupportState.Supported, s.State);
        Assert.Equal(100, s.CurrentLimit);
        Assert.Equal(0, battery.Backend.WriteCount);
    }

    private sealed class StubBackend(string id, BackendConfidence confidence, IWmiReader wmi, bool detectCore)
        : ChargeLimitBackendBase(wmi, new RecordingExecutor(), null)
    {
        public override string Id => id;
        public override string Name => id;
        public override BackendConfidence Confidence => confidence;
        protected override bool DetectCore() { Capabilities = new ChargeLimitCapabilities { Kind = ChargeLimitKind.OnOffConservation }; return detectCore; }
        protected override ChargeLimitState GetStateCore() => new() { Limit = 100 };
        public override BackendOriginalState CaptureOriginal() => NewOriginal(new()); protected override ChargeLimitState? ExpectedAfterRestoreCore(BackendOriginalState original) => new() { Limit = 100 };
        protected override BatteryWritePlan? BuildPlan(ChargeLimitRequest request, ChargeLimitState current, out string? error) { error = "stub"; return null; }
        protected override BatteryWritePlan? BuildRestorePlan(BackendOriginalState original, ChargeLimitState current, out string? error) { error = "stub"; return null; }
    }

    private sealed class ThrowingBackend : IChargeLimitBackend
    {
        public string Id => "x";
        public string Name => "x";
        public BackendConfidence Confidence => BackendConfidence.Unverified;
        public bool RequiresElevation => true;
        public bool Detect() => throw new InvalidOperationException();
        public ChargeLimitCapabilities Capabilities => ChargeLimitCapabilities.Unsupported;
        public ChargeLimitState GetState() => throw new InvalidOperationException();
        public BackendOriginalState CaptureOriginal() => throw new InvalidOperationException();
        public Task<BackendWriteResult> ApplyAsync(ChargeLimitRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<BackendWriteResult> RestoreAsync(BackendOriginalState original, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
