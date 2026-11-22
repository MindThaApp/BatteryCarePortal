using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Portal.Core.Battery;
using Windows.ApplicationModel.DataTransfer;

namespace BatteryCarePortal.Pages;

public sealed partial class OverviewPage : Page
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private bool _busy;

    public OverviewPage()
    {
        InitializeComponent();
        LayoutHelper.AlignToStart(Column);
        LayoutHelper.AlignToStart(SimulatedChip);
        RingHost.Child = Ring;
        Loaded += (_, _) =>
        {
            AppServices.Battery.SnapshotChanged += OnSnapshotChanged;
            AppServices.SupportChanged += RefreshSupport;
            AppServices.ChargeStateChanged += RefreshStatus;
            AppServices.SupportChanged += RefreshStatus;
            RefreshBattery();
            RefreshSupport();
            RefreshStatus();
            RefreshToday();
            RefreshPowerPlans();
        };
        Unloaded += (_, _) =>
        {
            AppServices.Battery.SnapshotChanged -= OnSnapshotChanged;
            AppServices.SupportChanged -= RefreshSupport;
            AppServices.ChargeStateChanged -= RefreshStatus;
            AppServices.SupportChanged -= RefreshStatus;
        };
    }

    private int? _limit;
    private readonly BatteryCarePortal.Controls.BatteryRing Ring = new();

    // Reads firmware state and the intent file off the UI thread; the sentence covers every engine state.
    private async void RefreshStatus()
    {
        try
        {
            ChargeStatusView view = await Task.Run(() => ChargeStatusView.Build(null, false));
            _limit = view.Limit;
            LimitSentence.Text = AppServices.Support is null ? string.Empty : view.Sentence;
            LimitSentence.Visibility = LimitSentence.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            BatterySnapshot s = AppServices.Battery.Current;
            Ring.Set(s.Percent, _limit, s.IsCharging);
        }
        catch (Exception) { }
    }

    private async void RefreshToday()
    {
        _todayAt = DateTime.UtcNow;
        try
        {
            DateTimeOffset now = DateTimeOffset.Now;
            IReadOnlyList<TodayEvent> events = await Task.Run(() =>
                TodayLog.Build(AppServices.History.Read(TodayLog.DayStart(now), now)));
            TodayList.ItemsSource = events.Select(e =>
            {
                string pct = e.Percent is int p ? BatteryFormat.Percent(p, format: "0") : BatteryFormat.None;
                return $"{e.Time:t}  {string.Format(Strings.Get("Today" + e.Kind), pct)}";
            }).ToList();
            TodayEmpty.Text = Strings.Get("OverviewTodayEmpty");
            TodayEmpty.Visibility = events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception) { }
    }

    private DateTime _todayAt = DateTime.MinValue;

    private void OnSnapshotChanged(object? sender, BatterySnapshot snapshot) => _dispatcher.TryEnqueue(() =>
    {
        RefreshBattery();
        if (DateTime.UtcNow - _todayAt > TimeSpan.FromSeconds(30)) RefreshToday();
    });

    // Monitor mode: the live reading is shown on every device, whatever the charge-limit state.
    private void RefreshBattery()
    {
        BatterySnapshot s = AppServices.Battery.Current;
        BatteryCard.Visibility = s.Present ? Visibility.Visible : Visibility.Collapsed;
        if (!s.Present) return;

        LevelText.Text = s.Percent is int p ? BatteryFormat.Percent(p, format: "0") : BatteryFormat.None;
        StatusText.Text = Strings.Get(s.IsCharging ? "BatteryCharging" : s.IsOnAc ? "BatteryPluggedIn" : "BatteryOnBattery");
        RateText.Text = s.PowerW is double w && Math.Abs(w) >= 0.05
            ? string.Format(Strings.Get(w > 0 ? "BatteryRateIn" : "BatteryRateOut"), BatteryFormat.Watts(Math.Abs(w)).TrimStart('+'))
            : string.Empty;
        RateText.Visibility = RateText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SimulatedChip.Visibility = s.IsSimulated ? Visibility.Visible : Visibility.Collapsed;
        Ring.Set(s.Percent, _limit, s.IsCharging);
    }

    private void RefreshSupport()
    {
        DeviceSupport? support = AppServices.Support;
        CopyButton.IsEnabled = SaveButton.IsEnabled = CheckAgainButton.IsEnabled = !_busy;
        if (support is null)
        {
            StateText.Text = Strings.Get("SupportChecking");
            SentenceText.Text = Strings.Get("SupportCheckingNote");
            DeviceText.Text = string.Empty;
            LimitText.Visibility = Visibility.Collapsed;
            return;
        }

        string key = support.State.ToString();
        StateText.Text = Strings.Get("SupportState" + key);
        SentenceText.Text = string.Format(Strings.Get(support.BetaActive ? "SupportTextOnTestListBeta" : "SupportText" + key), Brand(support.Device));
        DeviceText.Text = DeviceNameText.Join(support.Device.Manufacturer, support.Device.Family.Length > 0 ? support.Device.Family : support.Device.Model);
        DeviceText.Visibility = DeviceText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Read only here: changes happen on the Charging page.
        if (support.ChargeLimitAvailable && support.CurrentLimit is int limit)
        {
            LimitText.Text = limit >= 100 ? Strings.Get("SupportLimitNone") : string.Format(Strings.Get("SupportLimitValue"), limit);
            LimitText.Visibility = Visibility.Visible;
        }
        else
        {
            LimitText.Visibility = Visibility.Collapsed;
        }
    }

    private static string Brand(DeviceIdentity device)
    {
        string m = device.Manufacturer.Trim();
        if (m.Contains("dell", StringComparison.OrdinalIgnoreCase)) return "Dell";
        if (m.Contains("lenovo", StringComparison.OrdinalIgnoreCase)) return "Lenovo";
        if (m.Contains("asus", StringComparison.OrdinalIgnoreCase)) return "ASUS";
        if (m.Contains("hp", StringComparison.OrdinalIgnoreCase) || m.Contains("hewlett", StringComparison.OrdinalIgnoreCase)) return "HP";
        return Strings.Get("SupportBrandFallback");
    }

    private async void OnCheckAgain(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try { await AppServices.CheckDeviceAsync(); }
        finally { SetBusy(false); }
    }

    private async void OnCopyReport(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            string report = await Task.Run(BatteryDiagnostics.BuildReport);
            var package = new DataPackage();
            package.SetText(report);
            Clipboard.SetContent(package);
            ShowInfo(InfoBarSeverity.Success, Strings.Get("SupportReportCopied"));
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, string.Format(Strings.Get("SupportReportFailed"), ex.Message));
        }
        finally { SetBusy(false); }
    }

    private async void OnSaveReport(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            // WriteReport creates a new file and never overwrites: a taken name gets " (2)", " (3)" and so on.
            // With simulated battery data (tests and demos) the report stays inside the app's simulated folder, never in Documents.
            string folder = BatteryEnvironment.IsFakeBatteryEnabled
                ? Path.Combine(AppServices.StateFolder, "reports")
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "BatteryCarePortal-diagnostics.txt");
            string saved = await Task.Run(() => BatteryDiagnostics.WriteReport(path));
            ShowInfo(InfoBarSeverity.Success, string.Format(Strings.Get("SupportReportSaved"), saved));
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, string.Format(Strings.Get("SupportReportFailed"), ex.Message));
        }
        finally { SetBusy(false); }
    }

    private PowerPlanManagerLaunch? _ppm;

    // Read-only package check off the UI thread: shows Open when Power Plan Manager is installed, otherwise Get.
    private async void RefreshPowerPlans()
    {
        try
        {
            _ppm = await Task.Run(AppServices.ResolvePowerPlanManager);
            bool installed = _ppm.AppsFolderTarget is not null;
            PpmOpenButton.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
            PpmGetButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception) { }
    }

    private async void OnPpmOpen(object sender, RoutedEventArgs e)
    {
        PpmInfo.IsOpen = false;
        try
        {
            if (_ppm is null) return;
            bool launched = _ppm.ProtocolUri is { } protocol && await Windows.System.Launcher.LaunchUriAsync(new Uri(protocol));
            if (!launched && _ppm.AppsFolderTarget is { } target)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PpmInfo.Message = string.Format(Strings.Get("PpmOpenFailed"), ex.Message);
            PpmInfo.IsOpen = true;
        }
    }

    private async void OnPpmGet(object sender, RoutedEventArgs e)
    {
        PpmInfo.IsOpen = false;
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri(PowerPlanManagerLink.StoreUri)); }
        catch (Exception ex)
        {
            PpmInfo.Message = string.Format(Strings.Get("PpmOpenFailed"), ex.Message);
            PpmInfo.IsOpen = true;
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CopyButton.IsEnabled = SaveButton.IsEnabled = CheckAgainButton.IsEnabled = !busy;
    }

    private void ShowInfo(InfoBarSeverity severity, string message)
    {
        ReportInfo.Severity = severity;
        ReportInfo.Message = message;
        ReportInfo.IsOpen = true;
    }
}

