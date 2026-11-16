using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Portal.Core.Battery;

public enum PowerDataQuality
{
    /// <summary>Not reported by this device; show "—".</summary>
    Unavailable,
    /// <summary>Derived from other measured values; the UI should say so.</summary>
    Estimated,
    /// <summary>Reported by the battery, the firmware or an energy meter.</summary>
    Measured,
}

public readonly record struct PowerReading(double? Watts, PowerDataQuality Quality)
{
    public static PowerReading Unavailable => new(null, PowerDataQuality.Unavailable);
    public static PowerReading Measured(double watts) => new(Math.Round(watts, 2), PowerDataQuality.Measured);
    public bool HasValue => Watts is not null && Quality != PowerDataQuality.Unavailable;
}

/// <summary>Charger → system / battery flows for the power-flow visual. Never contains invented numbers.</summary>
public sealed record PowerFlowSnapshot
{
    public DateTimeOffset Timestamp { get; init; }
    /// <summary>Null when there is no battery to report the power source.</summary>
    public bool? AdapterConnected { get; init; }
    public PowerReading AdapterRatedW { get; init; } = PowerReading.Unavailable;
    /// <summary>Positive while charging, negative while discharging.</summary>
    public PowerReading BatteryW { get; init; } = PowerReading.Unavailable;
    public PowerReading SystemW { get; init; } = PowerReading.Unavailable;
    public double? CpuPercent { get; init; }
    public bool EnergyMeterPresent { get; init; }
}

/// <summary>Source of the charger's rated wattage. No generally available Windows interface reports it.</summary>
public interface IAdapterRatingSource
{
    double? ReadRatedWatts();
}

public sealed class NoAdapterRatingSource : IAdapterRatingSource
{
    public double? ReadRatedWatts() => null;
}

public static class PowerFlowCalculator
{
    public static PowerFlowSnapshot Compute(BatterySnapshot battery, double? adapterRatedW, EnergyMeterSample? meter, double? cpuPercent)
    {
        PowerReading batteryW = PowerReading.Unavailable;
        if (battery.Present)
        {
            if (battery.IsCharging && battery.ChargeRateMw is int charge) batteryW = PowerReading.Measured(charge / 1000.0);
            else if (!battery.IsOnAc && battery.DischargeRateMw is int discharge) batteryW = PowerReading.Measured(-discharge / 1000.0);
            else if (battery.ChargeRateMw == 0 && battery.DischargeRateMw == 0) batteryW = PowerReading.Measured(0);
        }

        PowerReading systemW = PowerReading.Unavailable;
        if (battery.Present && !battery.IsOnAc && battery.DischargeRateMw is int draw)
            systemW = PowerReading.Measured(draw / 1000.0);
        else if (meter?.SystemW is double metered)
            systemW = PowerReading.Measured(metered);
        // The adapter rating is only an upper bound for the system draw, so it is never turned into a system estimate.

        return new PowerFlowSnapshot
        {
            Timestamp = battery.Timestamp,
            AdapterConnected = battery.Present ? battery.IsOnAc : null,
            AdapterRatedW = adapterRatedW is double rated && rated > 0 ? PowerReading.Measured(rated) : PowerReading.Unavailable,
            BatteryW = batteryW,
            SystemW = systemW,
            CpuPercent = cpuPercent,
            EnergyMeterPresent = meter is not null,
        };
    }
}

public sealed record EmiChannelPower(string DevicePath, string Name, double? Watts);

public sealed record EnergyMeterSample(IReadOnlyList<EmiChannelPower> Channels, double? SystemW);

/// <summary>
/// Read-only Energy Metering Interface (EMI) reader. Opens every present GUID_DEVICE_ENERGY_METER interface,
/// reads its channel names, and turns two consecutive energy readings into watts. Only a channel that meters
/// the whole platform (PSYS / SYS / SYSTEM) is reported as system power; CPU-package channels are listed but
/// never presented as system draw.
/// </summary>
public sealed class EnergyMeterReader
{
    private static Guid _energyMeterInterface = new("45BD8344-7ED6-49CF-A440-C276C933B053");
    private const uint IoctlGetVersion = 0x224000;
    private const uint IoctlGetMetadataSize = 0x224004;
    private const uint IoctlGetMetadata = 0x224008;
    private const uint IoctlGetMeasurement = 0x22400C;
    private const uint GenericRead = 0x80000000;

    private readonly Dictionary<string, (ulong Energy, ulong Time)> _previous = new();
    private List<Meter>? _meters;

    private sealed record Meter(string Path, int Version, string[] Channels);

    public bool IsPresent => (_meters ??= Discover()).Count > 0;

    /// <summary>Returns null when no energy meter is present or readable. The first sample has no watts yet.</summary>
    public EnergyMeterSample? Sample()
    {
        try
        {
            _meters ??= Discover();
            if (_meters.Count == 0) return null;
            var channels = new List<EmiChannelPower>();
            foreach (Meter meter in _meters)
            {
                using SafeFileHandle handle = Open(meter.Path);
                if (handle.IsInvalid) continue;
                byte[] buffer = new byte[16 * Math.Max(1, meter.Channels.Length)];
                if (!Ioctl(handle, IoctlGetMeasurement, buffer, out int returned)) continue;
                for (int i = 0; i < meter.Channels.Length && (i + 1) * 16 <= returned; i++)
                {
                    ulong energy = BitConverter.ToUInt64(buffer, i * 16);
                    ulong time = BitConverter.ToUInt64(buffer, i * 16 + 8);
                    string key = meter.Path + "|" + i;
                    double? watts = null;
                    if (_previous.TryGetValue(key, out var prev) && time > prev.Time && energy >= prev.Energy)
                        watts = ToWatts(energy - prev.Energy, time - prev.Time);
                    _previous[key] = (energy, time);
                    channels.Add(new EmiChannelPower(meter.Path, meter.Channels[i], watts));
                }
            }
            double? system = channels.FirstOrDefault(c => IsSystemChannel(c.Name) && c.Watts is not null)?.Watts;
            return new EnergyMeterSample(channels, system is double s ? Math.Round(s, 2) : null);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Energy in picowatt-hours over time in 100 ns units → watts.</summary>
    public static double ToWatts(ulong picowattHours, ulong hundredNanoseconds) =>
        hundredNanoseconds == 0 ? 0 : picowattHours * 1e-12 * 3600.0 / (hundredNanoseconds * 1e-7);

    public static bool IsSystemChannel(string name)
    {
        string n = name.Trim().ToUpperInvariant();
        return n is "SYS" or "SYSTEM" || n.EndsWith("PSYS", StringComparison.Ordinal);
    }

    /// <summary>Channel names per meter, for diagnostics.</summary>
    public IReadOnlyList<string> DescribeMeters() =>
        (_meters ??= Discover()).Select(m => $"{m.Path} (EMI v{m.Version}): {string.Join(", ", m.Channels)}").ToArray();

    private static List<Meter> Discover()
    {
        var meters = new List<Meter>();
        try
        {
            if (CM_Get_Device_Interface_List_SizeW(out uint length, ref _energyMeterInterface, null, 0) != 0 || length <= 1) return meters;
            char[] list = new char[length];
            if (CM_Get_Device_Interface_ListW(ref _energyMeterInterface, null, list, length, 0) != 0) return meters;
            foreach (string path in new string(list).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                using SafeFileHandle handle = Open(path);
                if (handle.IsInvalid) continue;
                byte[] version = new byte[2];
                if (!Ioctl(handle, IoctlGetVersion, version, out _)) continue;
                int emiVersion = BitConverter.ToUInt16(version, 0);
                byte[] sizeBuffer = new byte[4];
                if (!Ioctl(handle, IoctlGetMetadataSize, sizeBuffer, out _)) continue;
                int size = (int)Math.Min(BitConverter.ToUInt32(sizeBuffer, 0), 64 * 1024);
                byte[] metadata = new byte[Math.Max(size, 72)];
                if (!Ioctl(handle, IoctlGetMetadata, metadata, out int got)) continue;
                string[] channels = emiVersion >= 2 ? ParseV2Channels(metadata, got) : new[] { ParseV1Name(metadata, got) };
                if (channels.Length > 0) meters.Add(new Meter(path, emiVersion, channels));
            }
        }
        catch (Exception) { }
        return meters;
    }

    // EMI_METADATA_V1: unit(4) OEM[16 wchar] model[16 wchar] revision(2) nameSize(2) name[]
    internal static string ParseV1Name(byte[] data, int length)
    {
        if (length < 72) return "Meter";
        int nameSize = BitConverter.ToUInt16(data, 70);
        nameSize = Math.Clamp(nameSize, 0, length - 72);
        string name = System.Text.Encoding.Unicode.GetString(data, 72, nameSize).TrimEnd('\0').Trim();
        return name.Length == 0 ? "Meter" : name;
    }

    // EMI_METADATA_V2: OEM[16 wchar] model[16 wchar] revision(2) channelCount(2) then EMI_CHANNEL_V2 { unit(4) nameSize(2) name[] }
    internal static string[] ParseV2Channels(byte[] data, int length)
    {
        if (length < 68) return Array.Empty<string>();
        int count = BitConverter.ToUInt16(data, 66);
        var names = new List<string>();
        int offset = 68;
        for (int i = 0; i < count && offset + 6 <= length; i++)
        {
            int nameSize = BitConverter.ToUInt16(data, offset + 4);
            if (offset + 6 + nameSize > length) break;
            names.Add(System.Text.Encoding.Unicode.GetString(data, offset + 6, nameSize).TrimEnd('\0').Trim());
            offset += 6 + nameSize;
        }
        return names.ToArray();
    }

    private static SafeFileHandle Open(string path) =>
        CreateFileW(path, GenericRead, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);

    private static bool Ioctl(SafeFileHandle handle, uint code, byte[] output, out int returned)
    {
        bool ok = DeviceIoControl(handle, code, IntPtr.Zero, 0, output, (uint)output.Length, out uint got, IntPtr.Zero);
        returned = (int)got;
        return ok;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid interfaceClass, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClass, string? deviceId, [Out] char[] buffer, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, uint inputSize,
        [Out] byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
}
