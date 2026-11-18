using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using Portal.Core.Battery;
using Portal.Core.Services;

namespace BatteryCarePortal;

/// <summary>
/// The battery reader, the device check and the charge-limit controller, shared by the window, the tray and the flyout.
/// Every firmware write goes through <see cref="Charge"/>.
/// </summary>
internal static class AppServices
{
    // Must stay the first initializer: the battery service below reads the fake-battery switch.
    private static readonly bool FlagChecked = CheckFlagFile();
    private static readonly PowerPlanManagerProbe Ppm = new();
    private static DispatcherQueue? _dispatcher;
    private static bool _startupReapplyDone;
    private static bool _checking;
    private static readonly AppLifetime Lifetime = new();
    private static TopUpMonitor? _topUpMonitor;
    private static readonly BatteryToastPlanner Toasts = new();

    /// <summary>How often the running app checks for a finished top-up.</summary>
    private static readonly TimeSpan CareInterval = TimeSpan.FromSeconds(30);

    private static bool CheckFlagFile()
    {
        BatteryEnvironment.ConfigureFlagFile(Windows.Storage.ApplicationData.Current.LocalFolder.Path);
        return true;
    }

    public static BatteryInfoService Battery { get; } = BatteryInfoService.CreateDefault();

    private static string StateFolder =>
        AppStateFolder.Resolve(Windows.Storage.ApplicationData.Current.LocalFolder.Path, BatteryEnvironment.IsFakeBatteryEnabled);

    /// <summary>Samples go into the state folder (the simulated one in fake mode), kept for the history window of the tier.</summary>
    public static BatteryHistoryLogger History { get; } = new(StateFolder, retentionDays: FeatureTiers.HistoryRetentionDays(hasPro: false));

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

    /// <summary>Raised on the UI thread when an unattended re-apply left the limit off and the user has to put it back.</summary>
    public static event Action<ChargeActionResult>? ReapplyNeedsUser;

    /// <summary>Raised on the UI thread when a battery notification is due (already rate-limited and allowed by the settings).</summary>
    public static event Action<BatteryToast>? ToastDue;

    /// <summary>Raised on the UI thread when a charge-limit write starts or ends, whoever started it; read <see cref="ChargeLimitController.IsBusy"/>.</summary>
    public static event Action? ChargeWriteChanged;

    public static PowerPlanManagerStatus CheckPowerPlanManager() =>
        BatteryEnvironment.IsFakeBatteryEnabled ? PowerPlanManagerStatus.None : Ppm.Check();

    /// <summary>Read-only: how to open Power Plan Manager (or its Store page when it is not installed).</summary>
    public static PowerPlanManagerLaunch ResolvePowerPlanManager() =>
        PowerPlanManagerLink.Resolve(BatteryEnvironment.IsFakeBatteryEnabled ? Array.Empty<string>() : Ppm.InstalledFamilies());

    public static void Start()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        ChargeWriteLock.Process.HeldChanged += _ => _dispatcher?.TryEnqueue(() => ChargeWriteChanged?.Invoke());
        Battery.SnapshotChanged += OnSnapshotForHistory;
        Battery.Start();
        try { SystemEvents.PowerModeChanged += OnPowerModeChanged; } catch (Exception) { }
        try { SystemEvents.SessionEnding += OnSessionEnding; } catch (Exception) { }
        _ = StartupCheckAsync();
        _ = CareLoopAsync();
    }

    /// <summary>On the UI thread; each check runs off it. Ends when the app quits.</summary>
    private static async Task CareLoopAsync()
    {
        using var timer = new PeriodicTimer(CareInterval);
        while (!Lifetime.Stopping && await timer.WaitForNextTickAsync())
        {
            if (Lifetime.Stopping) return;
            try { await CareTickAsync(); } catch (Exception) { }
        }
    }

    private static async Task CareTickAsync()
    {
        ChargeLimitController? controller = Charge;
        DeviceSupport? support = Support;
        BatterySnapshot snapshot = Battery.Current;
        if (controller is not null && _topUpMonitor?.Controller != controller) _topUpMonitor = new TopUpMonitor(controller);
        TopUpMonitor? monitor = controller is null ? null : _topUpMonitor;

        (TopUpCheckResult? ended, BatteryToastInput? input, BatterySettings settings) = await Task.Run(async () =>
        {
            BatterySettings loaded = Settings.LoadLastGood();
            double? hotAbove = loaded.HeatProtection.EndTopUpWhenHot ? loaded.HeatProtection.MaxTempC : null;
            TopUpCheckResult? result = monitor is null ? null : await monitor.CheckAsync(snapshot.Percent, snapshot.TemperatureC, hotAbove);
            return (result, ToastInputFor(snapshot, support, controller, loaded), loaded);
        });
        if (Lifetime.Stopping) return;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (ended is not null)
        {
            ReportTopUpEnded(ended);
            if (Toasts.TopUpEnded(ended, snapshot.Percent, settings, now) is { } finished) ToastDue?.Invoke(finished);
        }
        if (input is not null)
            foreach (BatteryToast toast in Toasts.Evaluate(input, settings, now)) ToastDue?.Invoke(toast);
    }

    /// <summary>Off the UI thread. Read-only; null when there is nothing to remind about.</summary>
    private static BatteryToastInput? ToastInputFor(BatterySnapshot s, DeviceSupport? support, ChargeLimitController? controller, BatterySettings settings)
    {
        if (!s.Present || s.Percent is null || support is null || support.State == DeviceSupportState.NoBattery) return null;
        try
        {
            int? hold = null, unplugAt = null;
            if (controller is not null)
            {
                if (settings.NotifyWhenLimitReached && controller.LoadIntent() is not { TopUpActive: true } &&
                    controller.ReadState().Limit is int limit && limit < 100)
                    hold = limit;
            }
            else if (support.CurrentLimit is int set && set < 100) hold = set;
            else unplugAt = settings.Limit;
            return new BatteryToastInput { Percent = s.Percent, OnAc = s.IsOnAc, Charging = s.IsCharging, HoldLimit = hold, UnplugAt = unplugAt };
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// On the UI thread. A limit that could not be put back without a prompt is left to the user, like a failed re-apply;
    /// a limit another tool set during the top-up is shown on the Charging page like one found at start or wake.
    /// </summary>
    private static void ReportTopUpEnded(TopUpCheckResult ended)
    {
        if (ended.Result.Status == ChargeActionStatus.ChangedElsewhere) LastReapply = ended.Result;
        ChargeStateChanged?.Invoke();
        if (ended.NeedsUser) ReapplyNeedsUser?.Invoke(ended.Result);
    }

    /// <summary>
    /// The first device check. When it finds no controller (at logon the driver may not be ready yet) and the saved
    /// intent has an unfinished top-up or a pending re-apply, the check is tried again with growing pauses, then stops.
    /// </summary>
    private static async Task StartupCheckAsync()
    {
        if (await TryCheckDeviceAsync()) return;
        foreach (TimeSpan delay in StartupRecovery.RetryDelays)
        {
            if (!SafeShouldRetry()) return;
            await Task.Delay(delay);
            if (Lifetime.Stopping) return;
            if (await TryCheckDeviceAsync()) return;
        }
    }

    private static bool SafeShouldRetry()
    {
        try { return StartupRecovery.ShouldRetryCheck(new ChargeIntentStore(StateFolder)); }
        catch (Exception) { return false; }
    }

    /// <summary>True when the check finished with a controller.</summary>
    private static async Task<bool> TryCheckDeviceAsync()
    {
        try
        {
            await CheckDeviceAsync();
            return Charge is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static async Task CheckDeviceAsync()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            (DeviceSupport result, ChargeLimitController? controller) = await Task.Run(() =>
            {
                DeviceSupport support = DeviceSupportService.Check(Battery.Refresh(), Gate, out IChargeLimitBackend? backend);
                return (support, backend is null ? null
                    : new ChargeLimitController(backend, new OriginalStateStore(StateFolder), new ChargeIntentStore(StateFolder)));
            });

            // Every controller shares the process write lock; holding it here means the swap never happens mid-write.
            await ChargeWriteLock.Process.EnterAsync();
            try
            {
                Support = result;
                Charge = controller;
            }
            finally { ChargeWriteLock.Process.Exit(); }
            SupportChanged?.Invoke();

            if (!_startupReapplyDone && controller is not null)
            {
                _startupReapplyDone = true;
                ChargeActionResult reapply = await Task.Run(() => controller.ReapplyAsync(ReapplyReason.Startup));
                ReportReapply(reapply);
            }
        }
        finally { _checking = false; }
    }

    // The logger throttles itself (on change, otherwise once a minute); the file write stays off the event thread.
    private static void OnSnapshotForHistory(object? sender, BatterySnapshot snapshot)
    {
        if (Lifetime.Stopping) return;
        _ = Task.Run(() => { try { History.Record(snapshot); } catch (Exception) { } });
    }

    /// <summary>Tells the open pages and the flyout that the charge state changed (call on the UI thread).</summary>
    public static void NotifyChargeChanged() => ChargeStateChanged?.Invoke();

    public static void ClearReapplyNotice() => LastReapply = null;

    public static void SetBetaOptIn(bool on)
    {
        Settings.Update(s =>
        {
            if (on) s.ConfirmBetaOptIn(DateTimeOffset.UtcNow);
            else s.TurnOffBetaOptIn();
        });
    }

    /// <summary>On the UI thread.</summary>
    private static void ReportReapply(ChargeActionResult result)
    {
        LastReapply = result;
        ChargeStateChanged?.Invoke();
        if (StartupRecovery.NeedsUserNotice(result)) ReapplyNeedsUser?.Invoke(result);
    }

    private static void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume || Charge is not { } controller) return;
        _ = Task.Run(async () =>
        {
            // Give the firmware and drivers a moment after wake before reading.
            await Task.Delay(TimeSpan.FromSeconds(3));
            ChargeActionResult result;
            try { result = await controller.ReapplyAsync(ReapplyReason.Resume); }
            catch (Exception ex) { result = new ChargeActionResult(ChargeActionStatus.Failed, ex.Message); }
            _dispatcher?.TryEnqueue(() => ReportReapply(result));
        });
    }

    /// <summary>Runs once on quit, on the UI thread. Bounded: never holds up the exit for longer than the budget.</summary>
    public static void OnExit()
    {
        bool first = Lifetime.BeginQuit();
        try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch (Exception) { }
        try { SystemEvents.SessionEnding -= OnSessionEnding; } catch (Exception) { }
        if (!first) return;
        try { Charge?.ReapplyOnExit(ChargeLimitController.DefaultExitBudget); } catch (Exception) { }
    }

    // Sign-out or shutdown: bounded, in-process only (no administrator helper), never prompts. The user can still cancel
    // it, so the care loop, the history and a later quit re-apply all carry on.
    private static void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        if (!Lifetime.BeginSessionEnd()) return;
        try { Charge?.ReapplyOnSessionEnd(ChargeLimitController.DefaultExitBudget); } catch (Exception) { }
        finally { Lifetime.EndSessionEnd(); }
    }
}
