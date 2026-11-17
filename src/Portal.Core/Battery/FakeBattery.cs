namespace Portal.Core.Battery;

public static class BatteryEnvironment
{
    /// <summary>PBC_FAKE_BATTERY=1 (or the older PPM_FAKE_BATTERY=1) turns on the simulated battery and backend (development and UI testing only).</summary>
    public const string FlagFileName = "fake-battery.flag";

    private static volatile bool _flagFilePresent;

    /// <summary>
    /// The environment variables, or a <see cref="FlagFileName"/> file in the app's local folder (checked once by
    /// <see cref="ConfigureFlagFile"/>; a packaged start does not inherit environment variables). The file is only ever read.
    /// </summary>
    public static bool IsFakeBatteryEnabled =>
        _flagFilePresent || IsSet("PBC_FAKE_BATTERY") || IsSet("BCP_FAKE_BATTERY") || IsSet("PPM_FAKE_BATTERY");

    /// <summary>Call once at startup, before anything reads <see cref="IsFakeBatteryEnabled"/>.</summary>
    public static void ConfigureFlagFile(string localFolder) => _flagFilePresent = HasFlagFile(localFolder);

    public static bool HasFlagFile(string localFolder)
    {
        try { return !string.IsNullOrEmpty(localFolder) && File.Exists(Path.Combine(localFolder, FlagFileName)); }
        catch (Exception) { return false; }
    }

    private static bool IsSet(string name) =>
        Environment.GetEnvironmentVariable(name) is "1" or "true" or "TRUE" or "True";
}

/// <summary>
/// A simulated laptop battery whose charging responds to the fake backend's limit, start threshold and pause,
/// so every battery screen can be exercised on a desktop.
/// </summary>
public sealed class FakeBattery
{
    private static readonly Lazy<FakeBattery> SharedInstance = new(() => new FakeBattery());
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private DateTimeOffset _last;
    private double _percent;
    private bool _charging;

    public FakeBattery(TimeProvider? time = null, double percent = 64)
    {
        _time = time ?? TimeProvider.System;
        _last = _time.GetUtcNow();
        _percent = percent;
        SnapshotSource = new FakeBatterySnapshotSource(this);
        Backend = new FakeChargeLimitBackend(this);
    }

    public static FakeBattery Shared => SharedInstance.Value;

    public IBatterySnapshotSource SnapshotSource { get; }
    public FakeChargeLimitBackend Backend { get; }

    public bool IsOnAc { get; set; } = true;
    public double TemperatureC { get; set; } = 33.5;
    public int Limit { get; internal set; } = 100;
    public int? StartThreshold { get; internal set; }
    public bool ChargingPaused { get; internal set; }
    public double PercentPerMinuteCharging { get; set; } = 10;
    public double PercentPerMinuteDischarging { get; set; } = 3;

    public const int DesignCapacityMwh = 57000;
    public const int FullChargeCapacityMwh = 51870;

    public double Percent
    {
        get { lock (_gate) { Advance(); return _percent; } }
        set { lock (_gate) { Advance(); _percent = Math.Clamp(value, 0, 100); } }
    }

    internal BatterySnapshot Read()
    {
        lock (_gate)
        {
            Advance();
            int remaining = (int)Math.Round(FullChargeCapacityMwh * _percent / 100);
            int chargeRate = _charging ? 24500 : 0;
            int dischargeRate = IsOnAc ? 0 : 9800;
            return new BatterySnapshot
            {
                Timestamp = _time.GetUtcNow(),
                Present = true,
                Percent = (int)Math.Floor(_percent),
                IsOnAc = IsOnAc,
                IsCharging = _charging,
                ChargeRateMw = _charging ? chargeRate : null,
                DischargeRateMw = IsOnAc ? null : dischargeRate,
                VoltageMv = 12400 + (int)(_percent * 6),
                RemainingCapacityMwh = remaining,
                DesignCapacityMwh = DesignCapacityMwh,
                FullChargeCapacityMwh = FullChargeCapacityMwh,
                HealthPercent = Math.Round(FullChargeCapacityMwh * 100.0 / DesignCapacityMwh, 1),
                CycleCount = 214,
                TemperatureC = TemperatureC,
                TimeRemaining = IsOnAc ? null : TimeSpan.FromHours(remaining / (double)dischargeRate),
                TimeToFull = _charging ? TimeSpan.FromHours((FullChargeCapacityMwh * Limit / 100.0 - remaining) / chargeRate) : null,
                Manufacturer = "Simulated",
                Chemistry = "LiP",
                DeviceName = "SIM-BAT-1",
                SerialNumber = "SIM00012345",
                ManufactureDate = new DateOnly(2023, 5, 17),
                Batteries = new BatteryUnitList(new[]
                {
                    new BatteryUnit
                    {
                        Name = "Simulated battery",
                        Manufacturer = "Simulated",
                        DeviceName = "SIM-BAT-1",
                        SerialNumber = "SIM00012345",
                        ManufactureDate = new DateOnly(2023, 5, 17),
                        Chemistry = "LiP",
                        DesignCapacityMwh = DesignCapacityMwh,
                        FullChargeCapacityMwh = FullChargeCapacityMwh,
                        RemainingCapacityMwh = remaining,
                        VoltageMv = 12400 + (int)(_percent * 6),
                        CycleCount = 214,
                        TemperatureC = TemperatureC,
                        ChargeRateMw = _charging ? chargeRate : null,
                        DischargeRateMw = IsOnAc ? null : dischargeRate,
                    },
                }),
                IsSimulated = true,
            };
        }
    }

    private void Advance()
    {
        DateTimeOffset now = _time.GetUtcNow();
        double minutes = Math.Max(0, (now - _last).TotalMinutes);
        _last = now;

        // Firmware-like hysteresis: stop at the limit, resume only below the start threshold (or the limit).
        int resumeBelow = StartThreshold ?? Limit;
        if (!IsOnAc || ChargingPaused || _percent >= Limit) _charging = false;
        else if (!_charging && _percent < resumeBelow) _charging = true;

        if (_charging) _percent = Math.Min(Limit, _percent + minutes * PercentPerMinuteCharging);
        else if (!IsOnAc) _percent = Math.Max(0, _percent - minutes * PercentPerMinuteDischarging);
        if (_charging && _percent >= Limit) _charging = false;
    }
}

public sealed class FakeBatterySnapshotSource : IBatterySnapshotSource
{
    private readonly FakeBattery _battery;
    internal FakeBatterySnapshotSource(FakeBattery battery) => _battery = battery;
    public bool UsesSystemPowerEvents => false;
    public BatterySnapshot Read() => _battery.Read();
}

/// <summary>Simulated backend with a continuous 50–100 % limit, a start threshold and pause. Writes need no elevation.</summary>
public sealed class FakeChargeLimitBackend : IChargeLimitBackend
{
    private readonly FakeBattery _battery;

    internal FakeChargeLimitBackend(FakeBattery battery) => _battery = battery;

    public string Id => "fake";
    public string Name => "Simulated battery";
    public BackendConfidence Confidence => BackendConfidence.Simulated;
    public bool RequiresElevation => false;
    public bool Detect() => true;
    public int WriteCount { get; private set; }

    public ChargeLimitCapabilities Capabilities { get; } = new()
    {
        Kind = ChargeLimitKind.ContinuousRange,
        MinLimit = 50,
        MaxLimit = 100,
        SupportsStartThreshold = true,
        MinStartThreshold = 40,
        MaxStartThreshold = 95,
        MinStartStopGap = 5,
        CanPauseCharging = true,
    };

    public ChargeLimitState GetState() => new()
    {
        Limit = _battery.Limit,
        StartThreshold = _battery.StartThreshold,
        ChargingPaused = _battery.ChargingPaused,
        RawValue = $"limit {_battery.Limit}; start {_battery.StartThreshold?.ToString() ?? "-"}; paused {_battery.ChargingPaused}",
    };

    public BackendOriginalState CaptureOriginal() => new()
    {
        BackendId = Id,
        CapturedUtc = DateTimeOffset.UtcNow,
        Values = new Dictionary<string, string>
        {
            ["Limit"] = _battery.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Start"] = _battery.StartThreshold?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        },
    };

    public Task<BackendWriteResult> ApplyAsync(ChargeLimitRequest request, CancellationToken cancellationToken = default)
    {
        string? error = ChargeLimitRequestValidator.Validate(request, Capabilities, _battery.Limit);
        if (error is not null) return Task.FromResult(BackendWriteResult.Fail(error));
        WriteCount++;
        if (request.Limit is int limit) _battery.Limit = limit;
        if (request.ResetStartThreshold) _battery.StartThreshold = null;
        else if (request.StartThreshold is int start) _battery.StartThreshold = start;
        if (_battery.StartThreshold is int s && s > _battery.Limit - Capabilities.MinStartStopGap) _battery.StartThreshold = null;
        if (request.ChargingPaused is bool paused) _battery.ChargingPaused = paused;
        return Task.FromResult(BackendWriteResult.Ok("Applied (simulated).", GetState()));
    }

    public Task<BackendWriteResult> SetLimitAsync(int limit, CancellationToken cancellationToken = default) =>
        ApplyAsync(new ChargeLimitRequest { Limit = limit }, cancellationToken);

    public Task<BackendWriteResult> SetStartThresholdAsync(int? threshold, CancellationToken cancellationToken = default) =>
        ApplyAsync(threshold is int t ? new ChargeLimitRequest { StartThreshold = t } : new ChargeLimitRequest { ResetStartThreshold = true }, cancellationToken);

    public Task<BackendWriteResult> SetChargingPausedAsync(bool paused, CancellationToken cancellationToken = default) =>
        ApplyAsync(new ChargeLimitRequest { ChargingPaused = paused }, cancellationToken);

    public Task<BackendWriteResult> RestoreAsync(BackendOriginalState original, CancellationToken cancellationToken = default)
    {
        _ = int.TryParse(original.Values.GetValueOrDefault("Limit"), out int limit);
        int? start = int.TryParse(original.Values.GetValueOrDefault("Start"), out int s) ? s : null;
        _battery.Limit = limit is >= 50 and <= 100 ? limit : 100;
        _battery.StartThreshold = start;
        _battery.ChargingPaused = false;
        WriteCount++;
        return Task.FromResult(BackendWriteResult.Ok("Restored (simulated).", GetState()));
    }
}

/// <summary>Picks the backend for this device. Returns null when only software reminders are possible.</summary>
public static class ChargeLimitBackendRegistry
{
    public static IReadOnlyList<IChargeLimitBackend> CreateCandidates(IWmiReader? wmi = null, IBatteryWriteExecutor? executor = null,
        WriteGateContext? gate = null)
    {
        wmi ??= new SystemManagementWmiReader();
        executor ??= new ElevatedBatteryWriteExecutor();
        gate ??= WriteGateContext.Default;
        return new IChargeLimitBackend[]
        {
            new Backends.LenovoConservationBackend(wmi, executor) { GateContext = gate },
            new Backends.LenovoThinkPadThresholdBackend(wmi, executor) { GateContext = gate },
            new Backends.AsusChargeLimitBackend(wmi, executor) { GateContext = gate },
            new Backends.DellChargeLimitBackend(wmi, executor) { GateContext = gate },
            new Backends.HpChargeLimitBackend(wmi, executor) { GateContext = gate },
        };
    }

    /// <summary>Read-only. The fake backend wins when PBC_FAKE_BATTERY=1 or PPM_FAKE_BATTERY=1.</summary>
    public static IChargeLimitBackend? Detect(IWmiReader? wmi = null, IBatteryWriteExecutor? executor = null, WriteGateContext? gate = null)
    {
        if (BatteryEnvironment.IsFakeBatteryEnabled) return FakeBattery.Shared.Backend;
        foreach (IChargeLimitBackend backend in CreateCandidates(wmi, executor, gate))
        {
            try { if (backend.Detect()) return backend; }
            catch (Exception) { }
        }
        return null;
    }
}
