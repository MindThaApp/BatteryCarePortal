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
        Loaded += (_, _) =>
        {
            AppServices.Battery.SnapshotChanged += OnSnapshotChanged;
            AppServices.SupportChanged += RefreshSupport;
            RefreshBattery();
            RefreshSupport();
        };
        Unloaded += (_, _) =>
        {
            AppServices.Battery.SnapshotChanged -= OnSnapshotChanged;
            AppServices.SupportChanged -= RefreshSupport;
        };
    }

    private void OnSnapshotChanged(object? sender, BatterySnapshot snapshot) => _dispatcher.TryEnqueue(RefreshBattery);

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
        SimulatedText.Visibility = s.IsSimulated ? Visibility.Visible : Visibility.Collapsed;
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
        DeviceText.Text = string.Join(" ", new[] { support.Device.Manufacturer, support.Device.Family.Length > 0 ? support.Device.Family : support.Device.Model }
            .Where(t => !string.IsNullOrWhiteSpace(t))).Trim();
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
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BatteryCarePortal-diagnostics.txt");
            string saved = await Task.Run(() => BatteryDiagnostics.WriteReport(path));
            ShowInfo(InfoBarSeverity.Success, string.Format(Strings.Get("SupportReportSaved"), saved));
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, string.Format(Strings.Get("SupportReportFailed"), ex.Message));
        }
        finally { SetBusy(false); }
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

