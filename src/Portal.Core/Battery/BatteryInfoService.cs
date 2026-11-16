using System.Runtime.InteropServices;
using Windows.System.Power;

namespace Portal.Core.Battery;

public interface IBatterySnapshotSource
{
    /// <summary>Reads the current battery state. Implementations never throw.</summary>
    BatterySnapshot Read();

    /// <summary>True when Windows power events should trigger an immediate refresh.</summary>
    bool UsesSystemPowerEvents { get; }
}

/// <summary>
/// Merges GetSystemPowerStatus, the WinRT aggregate battery report and the root\wmi battery classes.
/// Anything a source does not report stays null.
/// </summary>
public sealed class WindowsBatterySnapshotSource : IBatterySnapshotSource
{
    private const string WmiNamespace = @"root\wmi";
    private static readonly TimeSpan StaticDataLifetime = TimeSpan.FromMinutes(5);

    private readonly IWmiReader _wmi;
    private readonly IBatteryDeviceReader _device;
    private readonly TimeProvider _time;
    private BatteryRawReadings? _staticCache;
    private List<BatteryUnitRaw> _staticUnits = new();
    private DateTimeOffset _staticCacheTime;

    public WindowsBatterySnapshotSource(IWmiReader? wmi = null, TimeProvider? time = null, IBatteryDeviceReader? device = null)
    {
        _wmi = wmi ?? new SystemManagementWmiReader();
        _device = device ?? new BatteryDeviceReader();
        _time = time ?? TimeProvider.System;
    }

    public bool UsesSystemPowerEvents => true;

    public BatterySnapshot Read()
    {
        try
        {
            BatteryRawReadings raw = ReadSystemPowerStatus();
            raw = MergeAggregateReport(raw);
            raw = MergeWmi(raw);
            return BatterySnapshotMapper.Map(raw, _time.GetUtcNow());
        }
        catch (Exception)
        {
            // Never let a driver or WMI fault reach the UI; report "no battery" instead.
            return BatterySnapshot.NotPresent with { Timestamp = _time.GetUtcNow() };
        }
    }

    private static BatteryRawReadings ReadSystemPowerStatus()
    {
        if (!GetSystemPowerStatus(out SystemPowerStatus status)) return new BatteryRawReadings();
        return new BatteryRawReadings
        {
            AcLineStatus = status.ACLineStatus,
            BatteryFlag = status.BatteryFlag,
            LifePercent = status.BatteryLifePercent,
            LifeTimeSeconds = status.BatteryLifeTime == uint.MaxValue ? null : (int?)Math.Min(status.BatteryLifeTime, int.MaxValue),
        };
    }

    private static BatteryRawReadings MergeAggregateReport(BatteryRawReadings raw)
    {
        try
        {
            Windows.Devices.Power.BatteryReport report = Windows.Devices.Power.Battery.AggregateBattery.GetReport();
            return raw with
            {
                ReportStatus = report.Status.ToString(),
                ReportChargeRateMw = report.ChargeRateInMilliwatts,
                ReportDesignMwh = report.DesignCapacityInMilliwattHours,
                ReportFullMwh = report.FullChargeCapacityInMilliwattHours,
                ReportRemainingMwh = report.RemainingCapacityInMilliwattHours,
            };
        }
        catch (Exception)
        {
            return raw;
        }
    }

    private BatteryRawReadings MergeWmi(BatteryRawReadings raw)
    {
        if (raw.BatteryFlag == 128 || raw.ReportStatus == nameof(BatteryStatus.NotPresent)) return raw;

        DateTimeOffset now = _time.GetUtcNow();
        if (_staticCache is null || now - _staticCacheTime > StaticDataLifetime)
        {
            var staticRows = _wmi.Query(WmiNamespace, "SELECT DesignedCapacity, ManufactureName, Chemistry FROM BatteryStaticData");
            var cycleRows = _wmi.Query(WmiNamespace, "SELECT InstanceName, CycleCount FROM BatteryCycleCount");
            var fullRows = _wmi.Query(WmiNamespace, "SELECT InstanceName, FullChargedCapacity FROM BatteryFullChargedCapacity");
            var detailRows = _wmi.Query(WmiNamespace, "SELECT * FROM BatteryStaticData");
            _staticUnits = BatteryUnitMerger.BuildStaticUnits(detailRows, cycleRows, fullRows);
            _staticCache = new BatteryRawReadings
            {
                WmiDesignMwh = Sum(staticRows, "DesignedCapacity"),
                Manufacturer = staticRows.Select(r => r.AsString("ManufactureName")).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
                ChemistryCode = staticRows.Select(r => r.AsLong("Chemistry")).FirstOrDefault(v => v is > 0) is long c ? (uint)c : null,
                WmiCycleCount = cycleRows.Select(r => r.AsLong("CycleCount")).FirstOrDefault(v => v is > 0),
                WmiFullMwh = Sum(fullRows, "FullChargedCapacity"),
            };
            _staticCacheTime = now;
        }

        var statusRows = _wmi.Query(WmiNamespace,
            "SELECT InstanceName, ChargeRate, DischargeRate, Voltage, RemainingCapacity, PowerOnline, Charging FROM BatteryStatus");
        var temperatureRows = _wmi.Query(WmiNamespace, "SELECT InstanceName, Temperature FROM BatteryTemperature");

        IReadOnlyList<BatteryUnitRaw> wmiUnits = BatteryUnitMerger.AddDynamic(_staticUnits, statusRows, temperatureRows);
        IReadOnlyList<BatteryUnitRaw> deviceUnits = _device.ReadAll();

        return raw with
        {
            Units = BatteryUnitMerger.Merge(wmiUnits, deviceUnits),
            WmiDesignMwh = _staticCache.WmiDesignMwh,
            WmiFullMwh = _staticCache.WmiFullMwh,
            WmiCycleCount = _staticCache.WmiCycleCount,
            Manufacturer = _staticCache.Manufacturer,
            ChemistryCode = _staticCache.ChemistryCode,
            WmiRemainingMwh = Sum(statusRows, "RemainingCapacity"),
            WmiChargeRateMw = Sum(statusRows, "ChargeRate"),
            WmiDischargeRateMw = Sum(statusRows, "DischargeRate"),
            WmiVoltageMv = statusRows.Select(r => r.AsLong("Voltage")).FirstOrDefault(v => v is > 0),
            WmiPowerOnline = statusRows.Select(r => r.AsBool("PowerOnline")).FirstOrDefault(v => v is not null),
            WmiCharging = statusRows.Count == 0 ? null : statusRows.Any(r => r.AsBool("Charging") == true),
            WmiTemperatureDeciKelvin = temperatureRows.Select(r => r.AsLong("Temperature")).FirstOrDefault(v => v is > 0),
        };
    }

    private static long? Sum(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string property)
    {
        long total = 0;
        bool any = false;
        foreach (var row in rows)
        {
            // 0xFFFFFFFF / 0x80000000 are the driver's "unknown" markers; skip them.
            if (row.AsLong(property) is long v && v > 0 && v < 0x80000000) { total += v; any = true; }
        }
        return any ? total : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}

/// <summary>
/// Polls a snapshot source (every 5 s by default, and immediately on Windows power events) and raises
/// <see cref="SnapshotChanged"/> when anything other than the timestamp changes.
/// Events are raised on a thread-pool thread; UI code must marshal to its dispatcher.
/// </summary>
public sealed class BatteryInfoService : IDisposable
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    private readonly IBatterySnapshotSource _source;
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private readonly object _gate = new();
    private ITimer? _timer;
    private bool _subscribed;

    public BatteryInfoService(IBatterySnapshotSource source, TimeProvider? time = null, TimeSpan? pollInterval = null)
    {
        _source = source;
        _time = time ?? TimeProvider.System;
        _interval = pollInterval ?? DefaultPollInterval;
    }

    /// <summary>Creates the service with the simulated battery when PBC_FAKE_BATTERY=1 (or PPM_FAKE_BATTERY=1), otherwise the real one.</summary>
    public static BatteryInfoService CreateDefault() =>
        new(BatteryEnvironment.IsFakeBatteryEnabled ? FakeBattery.Shared.SnapshotSource : new WindowsBatterySnapshotSource());

    public BatterySnapshot Current { get; private set; } = BatterySnapshot.NotPresent;

    public bool IsRunning => _timer is not null;

    public event EventHandler<BatterySnapshot>? SnapshotChanged;

    public void Start()
    {
        lock (_gate)
        {
            if (_timer is not null) return;
            _timer = _time.CreateTimer(_ => Refresh(), null, TimeSpan.Zero, _interval);
            if (_source.UsesSystemPowerEvents && !_subscribed)
            {
                try
                {
                    PowerManager.PowerSupplyStatusChanged += OnPowerEvent;
                    PowerManager.BatteryStatusChanged += OnPowerEvent;
                    PowerManager.RemainingChargePercentChanged += OnPowerEvent;
                    _subscribed = true;
                }
                catch (Exception)
                {
                    // Polling still works without the events.
                }
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            if (_subscribed)
            {
                try
                {
                    PowerManager.PowerSupplyStatusChanged -= OnPowerEvent;
                    PowerManager.BatteryStatusChanged -= OnPowerEvent;
                    PowerManager.RemainingChargePercentChanged -= OnPowerEvent;
                }
                catch (Exception) { }
                _subscribed = false;
            }
        }
    }

    /// <summary>Reads the source now. Never throws.</summary>
    public BatterySnapshot Refresh()
    {
        BatterySnapshot snapshot;
        try { snapshot = _source.Read(); }
        catch (Exception) { snapshot = BatterySnapshot.NotPresent with { Timestamp = _time.GetUtcNow() }; }

        bool changed;
        lock (_gate)
        {
            changed = Current with { Timestamp = default } != snapshot with { Timestamp = default };
            Current = snapshot;
        }

        if (changed)
        {
            try { SnapshotChanged?.Invoke(this, snapshot); }
            catch (Exception) { }
        }
        return snapshot;
    }

    private void OnPowerEvent(object? sender, object e) => Refresh();

    public void Dispose() => Stop();
}
