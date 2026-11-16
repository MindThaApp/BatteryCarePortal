using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Portal.Core.Battery.Backends;

public readonly record struct DeviceIoResult(bool Success, uint Output, int Win32Error)
{
    public const int ErrorFileNotFound = 2;
    public const int ErrorPathNotFound = 3;
    public const int ErrorAccessDenied = 5;
    /// <summary>The IOCTL itself succeeded but returned too few bytes.</summary>
    public const int ErrorBadLength = 24;
    public bool AccessDenied => !Success && Win32Error == ErrorAccessDenied;
    public bool DeviceMissing => !Success && Win32Error is ErrorFileNotFound or ErrorPathNotFound;
}

/// <summary>32-bit in / 32-bit out DeviceIoControl access to the Lenovo energy-management driver.</summary>
public interface ILenovoEnergyDriver
{
    DeviceIoResult Send(uint input);
}

/// <summary>
/// The "Lenovo ACPI-Compliant Virtual Power Controller" / energy-management driver that Lenovo installs on
/// IdeaPad and Yoga notebooks exposes <c>\\.\EnergyDrv</c>. Lenovo's own battery settings on those models use
/// its battery-mode IOCTL, which forwards to the firmware's battery-mode-control (SBMC) and
/// battery-mode-query (GBMD) methods.
/// </summary>
public sealed class LenovoEnergyDriver : ILenovoEnergyDriver
{
    public const string DevicePath = @"\\.\EnergyDrv";
    /// <summary>CTL_CODE(0x8310, 0x83E, METHOD_BUFFERED, FILE_ANY_ACCESS).</summary>
    public const uint BatteryModeIoctl = 0x831020F8;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 3;
    private const uint OpenExisting = 3;

    public DeviceIoResult Send(uint input)
    {
        try
        {
            using SafeFileHandle handle = CreateFileW(DevicePath, GenericRead | GenericWrite, FileShareReadWrite,
                IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid) return new DeviceIoResult(false, 0, Marshal.GetLastWin32Error());
            bool ok = DeviceIoControl(handle, BatteryModeIoctl, ref input, 4, out uint output, 4, out uint returned, IntPtr.Zero);
            if (!ok) return new DeviceIoResult(false, 0, Marshal.GetLastWin32Error());
            return returned >= 4 ? new DeviceIoResult(true, output, 0) : new DeviceIoResult(false, 0, DeviceIoResult.ErrorBadLength);
        }
        catch (Exception)
        {
            return new DeviceIoResult(false, 0, -1);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr securityAttributes,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref uint input, uint inputSize,
        out uint output, uint outputSize, out uint returned, IntPtr overlapped);
}

/// <summary>
/// IdeaPad / Yoga "conservation mode": an on/off firmware feature that holds the battery near a fixed level
/// (about 60 % on most models, 80 % on some). Mechanism: the energy-management driver's battery-mode IOCTL.
/// Input 0x03 turns conservation on, 0x05 turns it off, 0xFF queries the battery mode word; bit 5 of that
/// word is the conservation flag (the same layout the firmware's GBMD method documents for the Linux
/// ideapad-laptop driver). Every write is followed by a query and must read back the requested state.
/// </summary>
public sealed class LenovoConservationBackend : ChargeLimitBackendBase
{
    public const uint QueryCode = 0xFF;
    public const uint ConservationOnCode = 0x03;
    public const uint ConservationOffCode = 0x05;
    public const int ConservationBit = 5;
    private const string OriginalKey = "Conservation";
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(300);

    private readonly ILenovoEnergyDriver _driver;
    private readonly int _conservationPercent;

    public LenovoConservationBackend(IWmiReader wmi, IBatteryWriteExecutor executor, ILenovoEnergyDriver? driver = null,
        TimeProvider? time = null, int conservationPercent = 60)
        : base(wmi, executor, time)
    {
        _driver = driver ?? new LenovoEnergyDriver();
        _conservationPercent = conservationPercent is 60 or 80 ? conservationPercent : 60;
    }

    public override string Id => "lenovo-conservation";
    public override string Name => "Lenovo conservation mode";
    public override BackendConfidence Confidence => BackendConfidence.Verified;
    /// <summary>The driver usually accepts writes from a standard user; the elevated helper is used only on access denied.</summary>
    public override bool RequiresElevation => false;

    /// <summary>Last raw battery-mode word, for diagnostics.</summary>
    public string? LastRawValue { get; private set; }

    /// <summary>Why detection failed, for diagnostics.</summary>
    public string? DetectionDetail { get; private set; }

    protected override bool DetectCore()
    {
        bool isLenovo = Wmi.Query(@"root\cimv2", "SELECT Manufacturer FROM Win32_ComputerSystem")
            .Any(r => r.AsString("Manufacturer")?.Contains("LENOVO", StringComparison.OrdinalIgnoreCase) == true);
        if (!isLenovo) { DetectionDetail = "Not a Lenovo computer."; return false; }

        DeviceIoResult query = _driver.Send(QueryCode);
        if (query.DeviceMissing) { DetectionDetail = "The Lenovo energy-management driver is not installed."; return false; }
        if (!query.Success && !query.AccessDenied) { DetectionDetail = $"The energy-management driver did not answer (error {query.Win32Error})."; return false; }

        DetectionDetail = query.AccessDenied ? "Driver present; reading needs administrator rights." : null;
        if (query.Success) LastRawValue = $"0x{query.Output:X8}";
        Capabilities = new ChargeLimitCapabilities
        {
            Kind = ChargeLimitKind.OnOffConservation,
            ConservationPercent = _conservationPercent,
        };
        return true;
    }

    protected override ChargeLimitState GetStateCore()
    {
        DeviceIoResult query = _driver.Send(QueryCode);
        if (!query.Success)
        {
            return new ChargeLimitState
            {
                Message = query.AccessDenied
                    ? "Reading conservation mode needs administrator rights on this device."
                    : $"Could not read conservation mode (error {query.Win32Error}).",
            };
        }
        LastRawValue = $"0x{query.Output:X8}";
        return new ChargeLimitState
        {
            Limit = IsConservationOn(query.Output) ? _conservationPercent : 100,
            RawValue = LastRawValue,
        };
    }

    public static bool IsConservationOn(uint modeWord) => ((modeWord >> ConservationBit) & 1) == 1;

    public override BackendOriginalState CaptureOriginal()
    {
        ChargeLimitState state = GetState();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (state.Limit is int limit) values[OriginalKey] = limit < 100 ? "On" : "Off";
        if (state.RawValue is not null) values["Raw"] = state.RawValue;
        return NewOriginal(values);
    }

    protected override ChargeLimitState? ExpectedAfterRestoreCore(BackendOriginalState original) =>
        original.Values.TryGetValue(OriginalKey, out string? value) && value is "On" or "Off"
            ? new ChargeLimitState { Limit = value == "On" ? _conservationPercent : 100 }
            : null;

    // Access denied on the read is acceptable: the elevated helper then reads back itself.
    protected override bool AllowWriteWithoutReadBack => true;

    protected override BatteryWritePlan? BuildPlan(ChargeLimitRequest request, ChargeLimitState current, out string? error)
    {
        error = null;
        if (request.Limit is not int limit) { error = "Conservation mode only supports a charge limit."; return null; }
        bool on = limit < 100;
        if (current.Limit is int now && (now < 100) == on) return null;
        return BuildTogglePlan(on);
    }

    protected override BatteryWritePlan? BuildRestorePlan(BackendOriginalState original, ChargeLimitState current, out string? error)
    {
        error = null;
        if (!original.Values.TryGetValue(OriginalKey, out string? value)) { error = "The original conservation state was never read."; return null; }
        bool on = value == "On";
        if (current.Limit is int now && (now < 100) == on) return null;
        return BuildTogglePlan(on);
    }

    private BatteryWritePlan BuildTogglePlan(bool on) => new()
    {
        BackendId = Id,
        Description = on ? "Turn on Lenovo conservation mode" : "Turn off Lenovo conservation mode",
        Steps =
        {
            new BatteryWriteStep
            {
                Kind = BatteryWriteStepKind.DeviceIoControl,
                Label = "Set conservation mode",
                DevicePath = LenovoEnergyDriver.DevicePath,
                IoControlCode = LenovoEnergyDriver.BatteryModeIoctl,
                InputValue = on ? ConservationOnCode : ConservationOffCode,
            },
            new BatteryWriteStep
            {
                Kind = BatteryWriteStepKind.VerifyDeviceIoControlBit,
                Label = "Read back conservation mode",
                DevicePath = LenovoEnergyDriver.DevicePath,
                IoControlCode = LenovoEnergyDriver.BatteryModeIoctl,
                InputValue = QueryCode,
                Bit = ConservationBit,
                ExpectedBitValue = on,
                DelayMs = (int)SettleDelay.TotalMilliseconds,
            },
        },
    };

    /// <summary>Runs the toggle in-process; only when the driver denies access does it go through the elevated helper.</summary>
    protected override async Task<BackendWriteResult> RunPlanAsync(BatteryWritePlan plan, bool allowPrompt, CancellationToken cancellationToken)
    {
        BatteryWriteStep write = plan.Steps[0];
        BatteryWriteStep verify = plan.Steps[1];
        DeviceIoResult result = _driver.Send(write.InputValue);
        if (result.AccessDenied) return await base.RunPlanAsync(plan, allowPrompt, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            string message = $"The energy-management driver rejected the change (error {result.Win32Error}). Nothing was retried.";
            // Bad length means the IOCTL ran, so the firmware may have changed.
            return result.Win32Error == DeviceIoResult.ErrorBadLength ? BackendWriteResult.Fail(message) : BackendWriteResult.FailBeforeWrite(message);
        }

        await Task.Delay(SettleDelay, cancellationToken).ConfigureAwait(false);
        DeviceIoResult readBack = _driver.Send(QueryCode);
        if (!readBack.Success)
            return BackendWriteResult.Fail($"The change was sent but could not be read back (error {readBack.Win32Error}).");
        LastRawValue = $"0x{readBack.Output:X8}";
        var state = new ChargeLimitState { Limit = IsConservationOn(readBack.Output) ? _conservationPercent : 100, RawValue = LastRawValue };
        return IsConservationOn(readBack.Output) == verify.ExpectedBitValue
            ? BackendWriteResult.Ok("Applied and read back.", state)
            : new BackendWriteResult
            {
                Success = false,
                Message = $"The firmware did not take the change (read back {LastRawValue}).",
                ReadBack = state,
                ReadBackCheck = ReadBackCheck.Mismatch,
            };
    }
}

/// <summary>
/// ThinkPad start/stop charge thresholds. ThinkPad firmware does not list the thresholds among the settings
/// that Lenovo_SetBiosSetting accepts on the models checked so far; they are handled by Lenovo's power
/// management driver through an interface that has to be confirmed on a ThinkPad first. Until then this
/// backend never reports itself as detected, so ThinkPads fall back to software reminders.
/// </summary>
public sealed class LenovoThinkPadThresholdBackend : ChargeLimitBackendBase
{
    public LenovoThinkPadThresholdBackend(IWmiReader wmi, IBatteryWriteExecutor executor, TimeProvider? time = null)
        : base(wmi, executor, time) { }

    public override string Id => "lenovo-thinkpad-thresholds";
    public override string Name => "ThinkPad charge thresholds";
    public override BackendConfidence Confidence => BackendConfidence.Unverified;
    protected override bool DetectCore() => false;
    protected override ChargeLimitState GetStateCore() => new() { Message = "Not available." };
    public override BackendOriginalState CaptureOriginal() => NewOriginal(new());
    protected override ChargeLimitState? ExpectedAfterRestoreCore(BackendOriginalState original) => null;

    protected override BatteryWritePlan? BuildPlan(ChargeLimitRequest request, ChargeLimitState current, out string? error)
    { error = "ThinkPad thresholds are not supported yet."; return null; }

    protected override BatteryWritePlan? BuildRestorePlan(BackendOriginalState original, ChargeLimitState current, out string? error)
    { error = "ThinkPad thresholds are not supported yet."; return null; }
}
