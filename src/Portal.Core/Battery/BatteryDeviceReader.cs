using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Portal.Core.Battery;

/// <summary>Reads each battery through the Windows battery device interface. Never throws.</summary>
public interface IBatteryDeviceReader
{
    /// <summary>One entry per battery. Includes dynamic values (voltage, rate, temperature); empty when unavailable.</summary>
    IReadOnlyList<BatteryUnitRaw> ReadAll();
}

/// <summary>
/// Query-only access to the battery class driver: IOCTL_BATTERY_QUERY_TAG, IOCTL_BATTERY_QUERY_INFORMATION
/// (BATTERY_INFORMATION, temperature, manufacture date, device name, serial number, unique id) and
/// IOCTL_BATTERY_QUERY_STATUS. Nothing here writes to the battery or the firmware.
/// </summary>
public sealed class BatteryDeviceReader : IBatteryDeviceReader
{
    private const uint IoctlQueryTag = 0x294040;
    private const uint IoctlQueryInformation = 0x294044;
    private const uint IoctlQueryStatus = 0x294048;

    private const int LevelInformation = 0;
    private const int LevelTemperature = 2;
    private const int LevelDeviceName = 4;
    private const int LevelManufactureDate = 5;
    private const int LevelManufactureName = 6;
    private const int LevelUniqueId = 7;
    private const int LevelSerialNumber = 8;

    private const uint UnknownRate = 0x80000000;
    private const uint UnknownValue = 0xFFFFFFFF;

    private static readonly Guid BatteryClass = new("72631e54-78a4-11d0-bcf7-00aa00b7b32a");

    public IReadOnlyList<BatteryUnitRaw> ReadAll()
    {
        var results = new List<BatteryUnitRaw>();
        try
        {
            Guid classGuid = BatteryClass;
            IntPtr set = SetupDiGetClassDevs(ref classGuid, null, IntPtr.Zero, 0x12);
            if (set == new IntPtr(-1)) return results;
            try
            {
                for (uint index = 0; index < 8; index++)
                {
                    var data = new DeviceInterfaceData { Size = (uint)Marshal.SizeOf<DeviceInterfaceData>() };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref classGuid, index, ref data)) break;
                    string? path = GetPath(set, ref data);
                    if (path is null) continue;
                    BatteryUnitRaw? unit = ReadOne(path, (int)index);
                    if (unit is not null) results.Add(unit);
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }
        catch (Exception)
        {
            // Treated as "no data from this source".
        }
        return results;
    }

    private static string? GetPath(IntPtr set, ref DeviceInterfaceData data)
    {
        SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out uint needed, IntPtr.Zero);
        if (needed < 8 || needed > 4096) return null;
        IntPtr buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6); // cbSize of the fixed part
            if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, needed, out _, IntPtr.Zero)) return null;
            return Marshal.PtrToStringUni(buffer + 4);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static BatteryUnitRaw? ReadOne(string path, int index)
    {
        using SafeFileHandle handle = CreateFileW(path, 0xC0000000 /* read + write */, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) return null;

        byte[] wait = new byte[4];
        byte[] tagBuffer = new byte[4];
        if (!DeviceIoControl(handle, IoctlQueryTag, wait, 4, tagBuffer, 4, out uint got, IntPtr.Zero) || got < 4) return null;
        uint tag = BitConverter.ToUInt32(tagBuffer, 0);
        if (tag == 0) return null;

        BatteryUnitRaw unit = new() { InstanceName = $"Battery {index + 1}" };

        byte[]? info = Query(handle, tag, LevelInformation, 36);
        if (info is not null)
        {
            uint chem = BitConverter.ToUInt32(info, 8);
            unit = unit with
            {
                ChemistryCode = chem == 0 ? null : chem,
                DesignMwh = BitConverter.ToUInt32(info, 12),
                FullMwh = BitConverter.ToUInt32(info, 16),
                CycleCount = BitConverter.ToUInt32(info, 32),
            };
        }

        byte[]? temperature = Query(handle, tag, LevelTemperature, 4);
        if (temperature is not null) unit = unit with { TemperatureDeciKelvin = BitConverter.ToUInt32(temperature, 0) };

        byte[]? date = Query(handle, tag, LevelManufactureDate, 4);
        DateOnly? manufactured = date is null ? null : BatteryMath.FromDayMonthYear(date[0], date[1], BitConverter.ToUInt16(date, 2));

        unit = unit with
        {
            ManufactureDate = manufactured,
            DeviceName = QueryString(handle, tag, LevelDeviceName),
            Manufacturer = QueryString(handle, tag, LevelManufactureName),
            SerialNumber = QueryString(handle, tag, LevelSerialNumber) ?? QueryString(handle, tag, LevelUniqueId),
        };

        // BATTERY_WAIT_STATUS with a zero timeout returns the current state at once.
        byte[] waitStatus = new byte[20];
        BitConverter.GetBytes(tag).CopyTo(waitStatus, 0);
        byte[] status = new byte[16];
        if (DeviceIoControl(handle, IoctlQueryStatus, waitStatus, 20, status, 16, out got, IntPtr.Zero) && got >= 16)
        {
            uint capacity = BitConverter.ToUInt32(status, 4);
            uint voltage = BitConverter.ToUInt32(status, 8);
            int rate = BitConverter.ToInt32(status, 12);
            bool rateKnown = (uint)rate != UnknownRate;
            unit = unit with
            {
                RemainingMwh = capacity == UnknownValue ? null : capacity,
                VoltageMv = voltage == UnknownValue ? null : voltage,
                ChargeRateMw = rateKnown && rate >= 0 ? rate : null,
                DischargeRateMw = rateKnown && rate < 0 ? -(long)rate : null,
            };
        }
        return unit;
    }

    private static byte[]? Query(SafeFileHandle handle, uint tag, int level, int outputSize)
    {
        byte[] input = new byte[12];
        BitConverter.GetBytes(tag).CopyTo(input, 0);
        BitConverter.GetBytes(level).CopyTo(input, 4);
        byte[] output = new byte[outputSize];
        return DeviceIoControl(handle, IoctlQueryInformation, input, 12, output, (uint)outputSize, out uint got, IntPtr.Zero) && got >= outputSize
            ? output
            : null;
    }

    private static string? QueryString(SafeFileHandle handle, uint tag, int level)
    {
        byte[] input = new byte[12];
        BitConverter.GetBytes(tag).CopyTo(input, 0);
        BitConverter.GetBytes(level).CopyTo(input, 4);
        byte[] output = new byte[256];
        if (!DeviceIoControl(handle, IoctlQueryInformation, input, 12, output, 256, out uint got, IntPtr.Zero) || got < 2) return null;
        string text = Encoding.Unicode.GetString(output, 0, (int)got & ~1).TrimEnd('\0').Trim();
        return text.Length == 0 ? null : text;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        public uint Size;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public UIntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr deviceInfo, ref Guid classGuid, uint index, ref DeviceInterfaceData data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DeviceInterfaceData data, IntPtr detail, uint size, out uint required, IntPtr deviceInfo);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, uint inputSize,
        byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
}
