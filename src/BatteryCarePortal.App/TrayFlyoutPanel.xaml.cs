using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Portal.Core.Battery;

namespace BatteryCarePortal;

/// <summary>
/// The flyout content: level, status sentence, and the charge-limit switch. The switch calls the same
/// <see cref="ChargeLimitController"/> method as the Charging page and is gated the same way; nothing else is written here.
/// </summary>
public sealed partial class TrayFlyoutPanel : UserControl
{
    private readonly BatteryCarePortal.Controls.BatteryRing _ring = new();
    private bool _busy;
    private bool _updating;
    private bool _gateAllowed;
    private int _refreshing;
    private bool _again;

    /// <summary>Raised on the UI thread when the content may have changed size.</summary>
    public event Action? ContentChanged;

    /// <summary>Raised when the user asks for the main window.</summary>
    public event Action? OpenRequested;

    /// <summary>Only a visible flyout refreshes on its own.</summary>
    public bool IsShown { get; set; }

    public TrayFlyoutPanel()
    {
        InitializeComponent();
        RingHost.Child = _ring;
        LimitTitle.Text = Strings.Get("FlyoutLimitTitle");
        OpenButton.Content = Strings.Get("FlyoutOpen");
        AutomationProperties.SetName(LimitSwitch, Strings.Get("FlyoutLimitTitle"));
        AppServices.Battery.SnapshotChanged += (_, _) => DispatcherQueue.TryEnqueue(RefreshIfShown);
        AppServices.SupportChanged += RefreshIfShown;
        AppServices.ChargeStateChanged += RefreshIfShown;
    }

    private void RefreshIfShown()
    {
        if (IsShown) Refresh();
    }

    public async void Refresh()
    {
        // One refresh at a time; a change that lands meanwhile is picked up by the next event.
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) { _again = true; return; }
        try
        {
            BatterySnapshot s = AppServices.Battery.Current;
            DeviceSupport? support = AppServices.Support;
            ChargeLimitController? controller = AppServices.Charge;
            bool busy = _busy;

            ChargeStatusView? view = null;
            ChargeLimitState? state = null;
            WriteGateResult? gate = null;
            if (s.Present && support is not null)
            {
                (view, state, gate) = await Task.Run(() => (
                    ChargeStatusView.Build(null, busy),
                    controller?.ReadState(),
                    controller?.EvaluateGate()));
            }

            DeviceText.Text = support is null ? string.Empty : DeviceNameText.Join(support.Device.Manufacturer,
                support.Device.Family.Length > 0 ? support.Device.Family : support.Device.Model);
            DeviceText.Visibility = DeviceText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            RingHost.Visibility = s.Present ? Visibility.Visible : Visibility.Collapsed;
            LevelText.Text = !s.Present ? Strings.Get("FlyoutNoBattery")
                : s.Percent is int p ? BatteryFormat.Percent(p, format: "0") : BatteryFormat.None;
            StatusText.Text = !s.Present ? string.Empty
                : view is not null && support is not null ? view.Sentence
                : Strings.Get(s.IsCharging ? "BatteryCharging" : s.IsOnAc ? "BatteryPluggedIn" : "BatteryOnBattery");
            StatusText.Visibility = StatusText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _ring.Set(s.Percent, view?.Limit, s.IsCharging);

            bool showLimit = s.Present && controller is not null && state is not null && ReferenceEquals(controller, AppServices.Charge);
            LimitCard.Visibility = showLimit ? Visibility.Visible : Visibility.Collapsed;
            if (showLimit)
            {
                _gateAllowed = gate?.Allowed == true;
                int? limit = state!.Limit;
                _updating = true;
                LimitSwitch.IsOn = limit is int l && l < 100;
                _updating = false;
                LimitHint.Text = limit is int on && on < 100 ? string.Format(Strings.Get("FlyoutLimitOn"), on) : Strings.Get("FlyoutLimitOff");
                BusyRing.IsActive = _busy;
                UpdateEnabled();
            }
        }
        catch (Exception) { }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
            ContentChanged?.Invoke();
            if (_again) { _again = false; Refresh(); }
        }
    }

    private void UpdateEnabled() =>
        LimitSwitch.IsEnabled = !_busy && _gateAllowed && AppServices.Charge is { IsBusy: false };

    private async void OnLimitToggled(object sender, RoutedEventArgs e)
    {
        if (_updating || _busy || AppServices.Charge is not { } controller) return;
        bool on = LimitSwitch.IsOn;
        _busy = true;
        FailedText.Visibility = Visibility.Collapsed;
        BusyRing.IsActive = true;
        UpdateEnabled();
        try
        {
            ChargeActionResult result = await Task.Run(async () =>
            {
                int? value = ChargeLimitChoice.PreferredValue(controller.Capabilities, controller.ReadState(), controller.LoadIntent());
                return await controller.SetLimitAsync(on, value);
            });
            if (result.Succeeded) AppServices.ClearReapplyNotice();
            else if (result.Status != ChargeActionStatus.Cancelled)
            {
                FailedText.Text = Strings.Get("FlyoutLimitFailed");
                FailedText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception)
        {
            FailedText.Text = Strings.Get("FlyoutLimitFailed");
            FailedText.Visibility = Visibility.Visible;
        }
        finally
        {
            _busy = false;
            BusyRing.IsActive = false;
            UpdateEnabled();
            AppServices.NotifyChargeChanged();
            Refresh();
        }
    }

    private void OnOpen(object sender, RoutedEventArgs e) => OpenRequested?.Invoke();
}
