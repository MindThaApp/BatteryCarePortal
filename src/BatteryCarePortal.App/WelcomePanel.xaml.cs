using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Portal.Core.Battery;
using Windows.ApplicationModel;

namespace BatteryCarePortal;

/// <summary>
/// The welcome flow, three pages: your laptop (and the charge limit offer), your choices, and where the app lives. Choices
/// apply as they are made, so Skip keeps them. The charge limit goes only through
/// <see cref="ChargeLimitController.SetLimitAsync"/> from the user's click on its button; Start with recommended settings
/// and Skip never write to the firmware.
/// </summary>
public sealed partial class WelcomePanel : UserControl
{
    private const string StartupTaskId = "BatteryCarePortalStartup";
    private const int PageCount = 3;

    private FirstRunPlan _plan = new(FirstRunNextAction.NothingToDo, Array.Empty<FirstRunStep>());
    private int _index;
    private bool _busy;
    private bool _limitDone;
    private bool _loading = true;
    private StartupTaskState? _startupState;

    public event Action? Finished;

    public WelcomePanel()
    {
        InitializeComponent();
        LayoutHelper.AlignToStart(BusyRing);
        BackButton.Content = Strings.Get("FirstRunBack");
        SkipButton.Content = Strings.Get("FirstRunSkip");
        RecommendedButton.Content = Strings.Get("FirstRunRecommended");
        StartupSwitch.Header = Strings.Get("FirstRunStartupSwitch");
        NotifyLimitSwitch.Header = Strings.Get("FirstRunNotifyLimit");
        NotifyPlugInSwitch.Header = Strings.Get("FirstRunNotifyPlugIn");
        NotifyTopUpSwitch.Header = Strings.Get("FirstRunNotifyTopUp");
        CrashSwitch.Header = Strings.Get("FirstRunCrashSwitch");
        StartupTitle.Text = Strings.Get("FirstRunStartupTitle");
        StartupText.Text = Strings.Get("FirstRunStartupText");
        NotifyTitle.Text = Strings.Get("FirstRunNotifyTitle");
        NotifyText.Text = Strings.Get("FirstRunNotifyText");
        CrashTitle.Text = Strings.Get("FirstRunCrashTitle");
        CrashText.Text = Strings.Get("FirstRunCrashText");
        PrefsTitle.Text = Strings.Get("FirstRunPrefsTitle");
        TitleText.Text = Strings.Get("FirstRunWelcomeTitle");
        BodyText.Text = Strings.Get("FirstRunWelcomeText");
        DeviceTitle.Text = Strings.Get("FirstRunCheckTitle");
        LimitTitle.Text = Strings.Get("FirstRunLimitTitle");
        LimitText.Text = Strings.Get("FirstRunLimitText");
        ActionButton.Content = Strings.Get("FirstRunLimitTurnOn");
        PpmTitle.Text = Strings.Get("FirstRunPpmTitle");
        PpmText.Text = Strings.Get("FirstRunPpmText");
        TrayTitle.Text = Strings.Get("FirstRunFinishTitle");
        TrayText.Text = Strings.Get("FirstRunTrayText");
        FreeText.Text = Strings.Get("FirstRunFreeText");
        FinishText.Text = Strings.Get("FirstRunFinishText");
        BuildDots();
        AddKey(Windows.System.VirtualKey.Enter, () => { if (!_busy) OnNext(this, new RoutedEventArgs()); }, skipWhenControlFocused: true);
        AddKey(Windows.System.VirtualKey.Escape, () => OnSkip(this, new RoutedEventArgs()), skipWhenControlFocused: false);
    }

    // Enter on a focused button or switch keeps its own meaning; elsewhere it is Next.
    private void AddKey(Windows.System.VirtualKey key, Action action, bool skipWhenControlFocused)
    {
        var accelerator = new KeyboardAccelerator { Key = key };
        accelerator.Invoked += (_, args) =>
        {
            if (skipWhenControlFocused && XamlRoot is not null
                && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase or ToggleSwitch) return;
            args.Handled = true;
            action();
        };
        KeyboardAccelerators.Add(accelerator);
    }

    private void BuildDots()
    {
        for (int i = 0; i < PageCount; i++)
            Dots.Children.Add(new Ellipse { Width = 8, Height = 8 });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(Dots, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
    }

    private void UpdateDots()
    {
        for (int i = 0; i < Dots.Children.Count; i++)
            if (Dots.Children[i] is Ellipse dot)
                dot.Fill = (Brush)Application.Current.Resources[i == _index ? "AccentFillColorDefaultBrush" : "ControlStrongFillColorDefaultBrush"];
    }

    /// <summary>Call once the device check has finished (<see cref="AppServices.Support"/> is not null).</summary>
    public async Task StartAsync()
    {
        DeviceSupport support = AppServices.Support!;
        PowerPlanManagerStatus ppm = await Task.Run(AppServices.CheckPowerPlanManager);
        _plan = FirstRunLogic.For(support.State, ppm);
        _index = 0;
        _limitDone = false;
        _loading = true;
        BatterySettings settings = AppServices.Settings.Load();
        NotifyLimitSwitch.IsOn = settings.NotifyWhenLimitReached;
        NotifyPlugInSwitch.IsOn = settings.NotifyPlugIn;
        NotifyTopUpSwitch.IsOn = settings.NotifyTopUpFinished;
        CrashSwitch.IsOn = settings.SendCrashReports;
        await LoadStartupStateAsync();
        _loading = false;

        TitleText.Text = Strings.Get("FirstRunWelcomeTitle");
        DeviceText.Text = DeviceNameText.Join(support.Device.Manufacturer, support.Device.Family.Length > 0 ? support.Device.Family : support.Device.Model);
        DeviceText.Visibility = DeviceText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailText.Text = Strings.Get("SupportState" + support.State) + ". " + Strings.Get("FirstRunAction" + _plan.Action);
        Show();
    }

    private async Task LoadStartupStateAsync()
    {
        try { _startupState = (await StartupTask.GetAsync(StartupTaskId)).State; }
        catch (Exception) { _startupState = null; }
        // Already on: show it as on; the page never turns it off.
        StartupSwitch.IsOn = _startupState is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
    }

    private void Show()
    {
        StepText.Text = string.Format(Strings.Get("FirstRunStepOf"), _index + 1, PageCount);
        Page0.Visibility = _index == 0 ? Visibility.Visible : Visibility.Collapsed;
        Page1.Visibility = _index == 1 ? Visibility.Visible : Visibility.Collapsed;
        Page2.Visibility = _index == 2 ? Visibility.Visible : Visibility.Collapsed;
        bool last = _index == PageCount - 1;
        BackButton.Visibility = _index > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = Strings.Get(last ? "FirstRunDone" : "FirstRunNext");
        SkipButton.Visibility = RecommendedButton.Visibility = last ? Visibility.Collapsed : Visibility.Visible;

        LimitPanel.Visibility = _plan.OffersChargeLimit ? Visibility.Visible : Visibility.Collapsed;
        PpmPanel.Visibility = _plan.Steps.Contains(FirstRunStep.PowerPlanManagerNote) ? Visibility.Visible : Visibility.Collapsed;
        ActionButton.Visibility = _limitDone ? Visibility.Collapsed : Visibility.Visible;
        if (_limitDone) ShowResult(InfoBarSeverity.Success, Strings.Get("FirstRunLimitDone"));

        bool startupOffered = _plan.Steps.Contains(FirstRunStep.StartWithWindows);
        StartupPanel.Visibility = NotifyPanel.Visibility = startupOffered ? Visibility.Visible : Visibility.Collapsed;
        bool blocked = _startupState is StartupTaskState.DisabledByUser or StartupTaskState.DisabledByPolicy;
        StartupSwitch.IsEnabled = !blocked && _startupState is not null;
        StartupDetail.Text = blocked ? Strings.Get("FirstRunStartupBlocked") : string.Empty;
        StartupDetail.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
        UpdateDots();
        UpdateEnabled();
    }

    private void UpdateEnabled() => BackButton.IsEnabled = NextButton.IsEnabled = SkipButton.IsEnabled = RecommendedButton.IsEnabled = ActionButton.IsEnabled = !_busy;

    private void ShowResult(InfoBarSeverity severity, string message)
    {
        ResultInfo.Severity = severity;
        ResultInfo.Message = message;
        ResultInfo.IsOpen = true;
    }

    private void ShowPrefsResult(InfoBarSeverity severity, string message)
    {
        PrefsInfo.Severity = severity;
        PrefsInfo.Message = message;
        PrefsInfo.IsOpen = true;
    }
    // The only place the first-run flow can start a firmware change: user pressed the button, the controller gates and reads back.
    private async void OnAction(object sender, RoutedEventArgs e)
    {
        if (_busy || AppServices.Charge is not { } controller) return;
        _busy = true;
        BusyRing.IsActive = true;
        UpdateEnabled();
        try
        {
            ChargeActionResult result = await Task.Run(() =>
            {
                int? value = ChargeLimitChoice.PreferredValue(controller.Capabilities, controller.ReadState(), controller.LoadIntent());
                return controller.SetLimitAsync(true, value);
            });
            if (result.Succeeded)
            {
                _limitDone = true;
                AppServices.ClearReapplyNotice();
                ActionButton.Visibility = Visibility.Collapsed;
                ShowResult(InfoBarSeverity.Success, Strings.Get("FirstRunLimitDone"));
            }
            else
            {
                string reason = result.Status == ChargeActionStatus.Refused ? ChargeStatusView.RefusalText(result.Refusal, result.Message) : result.Message ?? string.Empty;
                ShowResult(InfoBarSeverity.Warning, reason.Length > 0
                    ? string.Format(Strings.Get("FirstRunLimitNotDone"), reason)
                    : Strings.Get("FirstRunLimitNotDoneNoReason"));
            }
        }
        catch (Exception ex)
        {
            ShowResult(InfoBarSeverity.Error, string.Format(Strings.Get("FirstRunLimitNotDone"), ex.Message));
        }
        finally
        {
            _busy = false;
            BusyRing.IsActive = false;
            UpdateEnabled();
            AppServices.NotifyChargeChanged();
        }
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_index == PageCount - 1) { Finished?.Invoke(); return; }
        _index++;
        Show();
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_busy || _index == 0) return;
        _index--;
        Show();
    }

    // Skip only closes the window: what was chosen so far stays.
    private void OnSkip(object sender, RoutedEventArgs e)
    {
        if (!_busy) Finished?.Invoke();
    }

    // Only non-firmware recommendations: the three reminders on. The charge limit and Start with Windows stay the user's own choice.
    private void OnRecommended(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        NotifyLimitSwitch.IsOn = NotifyPlugInSwitch.IsOn = NotifyTopUpSwitch.IsOn = true;
        SaveNotifications();
        Finished?.Invoke();
    }

    // Only an explicit switch on opts in; an already-on task is left alone.
    private async void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || !StartupSwitch.IsOn) return;
        bool wasOn = _startupState is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        if (wasOn || !StartupSwitch.IsEnabled) return;
        PrefsInfo.IsOpen = false;
        try
        {
            StartupTask task = await StartupTask.GetAsync(StartupTaskId);
            StartupTaskState state = await task.RequestEnableAsync();
            _startupState = state;
            if (state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy) return;
            SetSwitchQuietly(StartupSwitch, false);
            ShowPrefsResult(InfoBarSeverity.Warning, Strings.Get("FirstRunStartupBlocked"));
        }
        catch (Exception ex)
        {
            SetSwitchQuietly(StartupSwitch, false);
            ShowPrefsResult(InfoBarSeverity.Warning, string.Format(Strings.Get("FirstRunStartupFailed"), ex.Message));
        }
    }

    private void SetSwitchQuietly(ToggleSwitch toggle, bool on)
    {
        _loading = true;
        try { toggle.IsOn = on; } finally { _loading = false; }
    }

    private void OnNotifyToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try { SaveNotifications(); }
        catch (Exception ex) { ShowPrefsResult(InfoBarSeverity.Error, ex.Message); }
    }

    private void OnCrashToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try { SaveCrashReports(); }
        catch (Exception ex) { ShowPrefsResult(InfoBarSeverity.Error, ex.Message); }
    }

    private void SaveNotifications()
    {
        bool limit = NotifyLimitSwitch.IsOn, plugIn = NotifyPlugInSwitch.IsOn, topUp = NotifyTopUpSwitch.IsOn;
        BatterySettings current = AppServices.Settings.Load();
        if (current.NotifyWhenLimitReached == limit && current.NotifyPlugIn == plugIn && current.NotifyTopUpFinished == topUp) return;
        AppServices.Settings.Update(s =>
        {
            s.NotifyWhenLimitReached = limit;
            s.NotifyPlugIn = plugIn;
            s.NotifyTopUpFinished = topUp;
        });
    }

    private void SaveCrashReports()
    {
        bool on = CrashSwitch.IsOn;
        if (AppServices.Settings.Load().SendCrashReports == on) return;
        AppServices.Settings.Update(s => s.SendCrashReports = on);
        CrashReporting.Apply(on);
    }
}