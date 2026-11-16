using Portal.Core.Battery;
using Portal.Core.Battery.Backends;

namespace Portal.Core.Tests;

public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;
    public ManualTimeProvider(DateTimeOffset start) => _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public void Advance(TimeSpan by) => _now += by;
    public void Set(DateTimeOffset now) => _now = now;
}

public sealed class TestBackend : IChargeLimitBackend
{
    public TestBackend(ChargeLimitCapabilities caps, bool requiresElevation = false)
    {
        Capabilities = caps;
        RequiresElevation = requiresElevation;
    }

    public string Id => "test";
    public string Name => "Test backend";
    public BackendConfidence Confidence { get; init; } = BackendConfidence.Simulated;
    public bool RequiresElevation { get; }
    public ChargeLimitCapabilities Capabilities { get; }
    public List<ChargeLimitRequest> Applied { get; } = new();
    public int? Limit { get; set; } = 100;
    public int? Start { get; set; }
    public bool Paused { get; set; }

    public bool Detect() => true;

    public ChargeLimitState GetState() => new() { Limit = Limit, StartThreshold = Start, ChargingPaused = Paused };

    public BackendOriginalState CaptureOriginal() => new()
    {
        BackendId = Id,
        Values = new Dictionary<string, string> { ["Limit"] = (Limit ?? 100).ToString() },
    };

    public Task<BackendWriteResult> ApplyAsync(ChargeLimitRequest request, CancellationToken cancellationToken = default)
    {
        if (RequiresElevation && !request.AllowElevationPrompt) return Task.FromResult(BackendWriteResult.ElevationNeeded());
        Applied.Add(request);
        if (request.Limit is int l) Limit = l;
        if (request.ResetStartThreshold) Start = null;
        else if (request.StartThreshold is int s) Start = s;
        if (request.ChargingPaused is bool p) Paused = p;
        return Task.FromResult(BackendWriteResult.Ok());
    }

    public Task<BackendWriteResult> RestoreAsync(BackendOriginalState original, CancellationToken cancellationToken = default)
    {
        Limit = int.Parse(original.Values["Limit"]);
        Start = null;
        Paused = false;
        return Task.FromResult(BackendWriteResult.Ok());
    }
}

public sealed class FakeWmiReader : IWmiReader
{
    public Dictionary<string, List<Dictionary<string, object?>>> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string queryFragment, Dictionary<string, object?> row)
    {
        if (!Rows.TryGetValue(queryFragment, out var list)) Rows[queryFragment] = list = new();
        list.Add(new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase));
    }

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string wmiNamespace, string wql) =>
        Rows.Where(kv => wql.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)).SelectMany(kv => kv.Value).ToList();

    public IReadOnlyDictionary<string, object?>? InvokeGetter(string wmiNamespace, string className, string methodName,
        IReadOnlyDictionary<string, object> arguments) => null;
}

public sealed class RecordingExecutor : IBatteryWriteExecutor
{
    public List<BatteryWritePlan> Plans { get; } = new();
    public BatteryWritePlanResult Result { get; set; } = new() { Ok = true };

    public Task<BatteryWritePlanResult> ExecuteAsync(BatteryWritePlan plan, CancellationToken cancellationToken = default)
    {
        Plans.Add(plan);
        return Task.FromResult(Result);
    }
}

public sealed class FakeEnergyDriver : ILenovoEnergyDriver
{
    public uint ModeWord { get; set; }
    public bool IgnoreWrites { get; set; }
    public int? FailWritesWith { get; set; }
    public int? FailAllWith { get; set; }
    public List<uint> Sent { get; } = new();

    public DeviceIoResult Send(uint input)
    {
        Sent.Add(input);
        if (FailAllWith is int all) return new DeviceIoResult(false, 0, all);
        if (input == LenovoConservationBackend.QueryCode) return new DeviceIoResult(true, ModeWord, 0);
        if (FailWritesWith is int err) return new DeviceIoResult(false, 0, err);
        if (!IgnoreWrites)
        {
            if (input == LenovoConservationBackend.ConservationOnCode) ModeWord |= 1u << LenovoConservationBackend.ConservationBit;
            if (input == LenovoConservationBackend.ConservationOffCode) ModeWord &= ~(1u << LenovoConservationBackend.ConservationBit);
        }
        return new DeviceIoResult(true, 0, 0);
    }
}

public static class Snap
{
    public static BatterySnapshot At(int percent, bool onAc, bool charging = false, double? tempC = null, DateTimeOffset? time = null) => new()
    {
        Timestamp = time ?? DateTimeOffset.UnixEpoch,
        Present = true,
        Percent = percent,
        IsOnAc = onAc,
        IsCharging = charging,
        TemperatureC = tempC,
        FullChargeCapacityMwh = 50000,
        RemainingCapacityMwh = percent * 500,
        ChargeRateMw = charging ? 25000 : null,
    };
}
