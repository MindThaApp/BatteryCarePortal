using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using Portal.Core.Battery;

namespace BatteryCarePortal;

/// <summary>
/// The battery reader, the device check and the charge-limit controller, shared by the window, the tray and the flyout.
/// Every firmware write goes through <see cref="Charge"/>.
/// </summary>
internal static class AppServices
{
    private static readonly PowerPlanManagerProbe Ppm = new();
    private static DispatcherQueue? _dispatcher;
    private static bool _startupReapplyDone;
    private static bool _checking;

    public static BatteryInfoService Battery { get; } = BatteryInfoService.CreateDefault();

    private static string StateFolder => Windows.Storage.ApplicationData.Current.LocalFolder.Path;

    public static BatterySettingsStore Settings { get; } = new(StateFolder);

    /// <summary>Beta opt-in from the settings file; another owner from the Power Plan Manager check (never in test mode).</summary>
    public static WriteGateContext Gate { get; } = new()
    {
        BetaOptIn = () => Settings.Load().BetaOtherBrandsOptIn,
        OtherOwner = () => BatteryEnvironment.IsFakeBatteryEnabled ? null : Ppm.OtherOwner(),
    };

    /// <summary>Null until the first device check has finished.</summary>
    public static DeviceSupport? Support { get; private set; }

    /// <summary>Null when no backend can write on this laptop.</summary>
    public static ChargeLimitController? Charge { get; private set; }

    /// <summary>The last unattended re-apply (start, wake), for the Charging page.</summary>
    public static ChargeActionResult? LastReapply { get; private set; }

    /// <summary>Raised on the UI thread when a device check finishes.</summary>
    public static event Action? SupportChanged;

    /// <summary>Raised on the UI thread after an unattended re-apply.</summary>
    public static event Action? ChargeStateChanged;

    public static PowerPlanManagerStatus CheckPowerPlanManager() =>
        BatteryEnvironment.IsFakeBatteryEnabled ? PowerPlanManagerStatus.None : Ppm.Check();

    public static void Start()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Battery.Start();
        try { SystemEvents.PowerModeChanged += OnPowerModeChanged; } catch (Exception) { }
        _ = CheckDeviceAsync();
    }

    public static async Task CheckDeviceAsync()
    {
        // A new controller while a write is in flight could allow a second write; the pages disable "Check again" meanwhile.
        if (_checking || Charge is { IsBusy: true }) return;
        _checking = true;
        try
        {
            (DeviceSupport result, ChargeLimitController? controller) = await Task.Run(() =>
            {
                DeviceSupport support = DeviceSupportService.Check(Battery.Refresh(), Gate, out IChargeLimitBackend? backend);
                return (support, backend is null ? null
                    : new ChargeLimitController(backend, new OriginalStateStore(StateFolder), new ChargeIntentStore(StateFolder)));
            });
            Support = result;
            Charge = controller;
            SupportChanged?.Invoke();

            if (!_startupReapplyDone && controller is not null)
            {
                _startupReapplyDone = true;
                LastReapply = await Task.Run(() => controller.ReapplyAsync(ReapplyReason.Startup));
                ChargeStateChanged?.Invoke();
            }
        }
        finally { _checking = false; }
    }

    public static void ClearReapplyNotice() => LastReapply = null;

    public static void SetBetaOptIn(bool on)
    {
        Settings.Update(s =>
        {
            if (on) s.ConfirmBetaOptIn(DateTimeOffset.UtcNow);
            else s.TurnOffBetaOptIn();
        });
    }

    private static void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume || Charge is not { } controller) return;
        _ = Task.Run(async () =>
        {
            // Give the firmware and drivers a moment after wake before reading.
            await Task.Delay(TimeSpan.FromSeconds(3));
            ChargeActionResult result = await controller.ReapplyAsync(ReapplyReason.Resume);
            _dispatcher?.TryEnqueue(() =>
            {
                LastReapply = result;
                ChargeStateChanged?.Invoke();
            });
        });
    }

    /// <summary>Runs once on quit, on the UI thread. Bounded: never holds up the exit for longer than the budget.</summary>
    public static void OnExit()
    {
        try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch (Exception) { }
        try { Charge?.ReapplyOnExit(ChargeLimitController.DefaultExitBudget); } catch (Exception) { }
    }
}
