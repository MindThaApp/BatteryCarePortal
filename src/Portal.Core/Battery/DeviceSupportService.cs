using Portal.Core.Battery.Backends;

namespace Portal.Core.Battery;

/// <summary>The five device support states of the design (section 3).</summary>
public enum DeviceSupportState
{
    /// <summary>A verified model whose backend detected: the charge limit works.</summary>
    Supported,
    /// <summary>The laptop exposes a charge-limit interface, but the model is not verified: monitor features only.</summary>
    OnTestList,
    /// <summary>No usable charge-limit interface: monitor features only.</summary>
    MonitorMode,
    /// <summary>A verified model whose maker's driver or interface was not found: monitor features until fixed.</summary>
    OneStepMissing,
    /// <summary>No battery at all.</summary>
    NoBattery,
}

/// <summary>What one backend reported during the read-only check.</summary>
/// <param name="Verified">The device matches a <see cref="VerifiedModels"/> rule for this backend.</param>
/// <param name="Detected">The gated <see cref="IChargeLimitBackend.Detect"/> result: false for an unverified device unless the user opted into beta.</param>
/// <param name="Probed">The ungated read-only probe: the interface exists, whether or not the model is verified.</param>
/// <param name="Beta">Detected only because of the user's beta opt-in (not verified).</param>
public sealed record BackendProbe(
    string BackendId,
    string Name,
    bool Verified,
    bool Detected,
    bool Probed,
    ChargeLimitCapabilities Capabilities,
    int? CurrentLimit,
    bool Beta = false);

public sealed record DeviceSupportInputs(bool BatteryPresent, DeviceIdentity Device, IReadOnlyList<BackendProbe> Backends);

public sealed record DeviceSupport
{
    public DeviceSupportState State { get; init; }
    public DeviceIdentity Device { get; init; } = new(string.Empty, string.Empty, string.Empty, string.Empty);
    /// <summary>The backend that detected: in the Supported state, or in OnTestList while the beta opt-in is active.</summary>
    public BackendProbe? Backend { get; init; }
    /// <summary>For OnTestList: the backends whose interface exists on this unverified model.</summary>
    public IReadOnlyList<BackendProbe> UnverifiedFinds { get; init; } = Array.Empty<BackendProbe>();
    /// <summary>The stored beta opt-in, for display.</summary>
    public bool BetaOptIn { get; init; }

    /// <summary>An unverified model whose backend detected only because of the user's beta opt-in.</summary>
    public bool BetaActive => State == DeviceSupportState.OnTestList && Backend is { Beta: true };

    /// <summary>True when a backend detected through the write gate: a verified model, or beta testing on a test-list model.</summary>
    public bool ChargeLimitAvailable => State == DeviceSupportState.Supported || BetaActive;

    /// <summary>Read-only current charge limit (100 = none) when the backend could read it.</summary>
    public int? CurrentLimit => Backend?.CurrentLimit;
}

/// <summary>
/// Puts the machine into one of the <see cref="DeviceSupportState"/>s. <see cref="Classify"/> is pure; <see cref="Gather"/>
/// only reads: it calls Detect, the diagnostics probe and GetState, and never a write method.
/// </summary>
public static class DeviceSupportService
{
    public static DeviceSupport Classify(DeviceSupportInputs inputs, bool betaOptIn = false)
    {
        // betaOptIn is carried through for display only; the probes (gated by WriteGate) decide every state.
        var result = new DeviceSupport { Device = inputs.Device, BetaOptIn = betaOptIn };
        if (!inputs.BatteryPresent) return result with { State = DeviceSupportState.NoBattery };

        BackendProbe? detected = inputs.Backends.FirstOrDefault(b => b.Detected && !b.Beta);
        if (detected is not null) return result with { State = DeviceSupportState.Supported, Backend = detected };

        if (inputs.Backends.Any(b => b.Verified)) return result with { State = DeviceSupportState.OneStepMissing };

        List<BackendProbe> finds = inputs.Backends.Where(b => b.Probed).ToList();
        if (finds.Count == 0) return result with { State = DeviceSupportState.MonitorMode };
        BackendProbe? beta = inputs.Backends.FirstOrDefault(b => b.Detected && b.Beta);
        return result with { State = DeviceSupportState.OnTestList, UnverifiedFinds = finds, Backend = beta };
    }

    /// <summary>Read-only. Never throws for device errors.</summary>
    public static IReadOnlyList<BackendProbe> Gather(IReadOnlyList<IChargeLimitBackend> candidates, DeviceIdentity device)
    {
        var probes = new List<BackendProbe>();
        foreach (IChargeLimitBackend backend in candidates)
        {
            try
            {
                bool simulated = backend.Confidence == BackendConfidence.Simulated;
                bool verified = !simulated && VerifiedModels.IsVerified(backend.Id, device);
                bool detected = backend.Detect();
                bool probed = detected || (backend is ChargeLimitBackendBase b ? b.ProbeForDiagnostics() : false);
                int? limit = detected ? backend.GetState().Limit : null;
                bool beta = detected && !simulated && !verified;
                probes.Add(new BackendProbe(backend.Id, backend.Name, verified || simulated, detected, probed, backend.Capabilities, limit, beta));
            }
            catch (Exception)
            {
                probes.Add(new BackendProbe(backend.Id, backend.Name, false, false, false, ChargeLimitCapabilities.Unsupported, null));
            }
        }
        return probes;
    }

    /// <summary>Checks this machine. With the simulated battery on, the simulated backend is the only candidate.</summary>
    public static DeviceSupport Check(BatterySnapshot snapshot, bool betaOptIn = false, IWmiReader? wmi = null) =>
        Check(snapshot, new WriteGateContext { BetaOptIn = () => betaOptIn }, out _, wmi);

    /// <summary>
    /// Checks this machine and returns the backend instance that detected (null when none can write).
    /// Read-only. With the simulated battery on, the simulated backend is the only candidate and nothing real is touched.
    /// </summary>
    public static DeviceSupport Check(BatterySnapshot snapshot, WriteGateContext gate, out IChargeLimitBackend? backend, IWmiReader? wmi = null)
    {
        bool betaOptIn = SafeBeta(gate);
        backend = null;
        if (BatteryEnvironment.IsFakeBatteryEnabled)
        {
            var fakeDevice = new DeviceIdentity("Simulated", "Simulated laptop", string.Empty, string.Empty);
            DeviceSupport fake = Classify(new DeviceSupportInputs(snapshot.Present, fakeDevice, Gather([FakeBattery.Shared.Backend], fakeDevice)), betaOptIn);
            backend = fake.ChargeLimitAvailable ? FakeBattery.Shared.Backend : null;
            return fake;
        }

        wmi ??= new SystemManagementWmiReader();
        DeviceIdentity device;
        try { device = DeviceIdentity.Read(wmi); }
        catch (Exception) { device = new DeviceIdentity(string.Empty, string.Empty, string.Empty, string.Empty); }

        IReadOnlyList<IChargeLimitBackend> candidates = snapshot.Present
            ? ChargeLimitBackendRegistry.CreateCandidates(wmi, gate: gate)
            : Array.Empty<IChargeLimitBackend>();
        DeviceSupport support = Classify(new DeviceSupportInputs(snapshot.Present, device, Gather(candidates, device)), betaOptIn);
        if (support.ChargeLimitAvailable && support.Backend is { } chosen)
            backend = candidates.FirstOrDefault(c => c.Id == chosen.BackendId);
        return support;
    }

    private static bool SafeBeta(WriteGateContext gate)
    {
        try { return gate.BetaOptIn(); }
        catch (Exception) { return false; }
    }
}
