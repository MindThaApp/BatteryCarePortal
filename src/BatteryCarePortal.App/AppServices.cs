using Portal.Core.Battery;

namespace BatteryCarePortal;

/// <summary>The battery reader and the device check, shared by the window, the tray and the flyout. Read-only: nothing here writes to the firmware.</summary>
internal static class AppServices
{
    public static BatteryInfoService Battery { get; } = BatteryInfoService.CreateDefault();

    /// <summary>Null until the first device check has finished.</summary>
    public static DeviceSupport? Support { get; private set; }

    /// <summary>Raised on the UI thread when a device check finishes.</summary>
    public static event Action? SupportChanged;

    public static void Start()
    {
        Battery.Start();
        _ = CheckDeviceAsync();
    }

    public static async Task CheckDeviceAsync()
    {
        // The beta opt-in stays off; it never changes what the check reports.
        DeviceSupport result = await Task.Run(() => DeviceSupportService.Check(Battery.Refresh(), betaOptIn: false));
        Support = result;
        SupportChanged?.Invoke();
    }
}
