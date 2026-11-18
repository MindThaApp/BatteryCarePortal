using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Portal.Core.Battery;
using Windows.ApplicationModel;

namespace BatteryCarePortal;

/// <summary>
/// The welcome flow. Nothing changes unless the user presses a button on a page: the charge limit goes only through
/// <see cref="ChargeLimitController.SetLimitAsync"/>, Start with Windows only when its switch is turned on, and the
/// notification switches are saved only on Next. Skip leaves the current page unchanged.
/// </summary>
public sealed partial class WelcomePanel : UserControl
{
    private const string StartupTaskId = "BatteryCarePortalStartup";

    /// <summary>The crash-report slot has no behind-the-scenes setting yet; keep it hidden until it is wired.</summary>
    private const bool CrashSlotWired = false;

    private FirstRunPlan _plan = new(FirstRunNextAction.NothingToDo, Array.Empty<FirstRunStep>());
    private List<FirstRunStep> _steps = new();
    private int _index;
    private bool _busy;
    private bool _limitDone;
    private StartupTaskState? _startupState;

    public event Action? Finished;

    public WelcomePanel()
    {
        InitializeComponent();
        LayoutHelper.AlignToStart(BusyRing);
        BackButton.Content = Strings.Get("FirstRunBack");
        SkipButton.Content = Strings.Get("FirstRunSkip");
        StartupSwitch.Header = Strings.Get("FirstRunStartupSwitch");
        NotifyLimitSwitch.Header = Strings.Get("FirstRunNotifyLimit");
        NotifyPlugInSwitch.Header = Strings.Get("FirstRunNotifyPlugIn");
        NotifyTopUpSwitch.Header = Strings.Get("FirstRunNotifyTopUp");
        CrashSwitch.Header = Strings.Get("FirstRunCrashSwitch");
    }

    /// <summary>Call once the device check has finished (<see cref="AppServices.Support"/> is not null).</summary>
    public async Task StartAsync()
    {
        DeviceSupport support = AppServices.Support!;
        PowerPlanManagerStatus ppm = await Task.Run(AppServices.CheckPowerPlanManager);
        _plan = FirstRunLogic.For(support.State, ppm);
        _steps = _plan.Steps.Where(s => s != FirstRunStep.CrashReports || CrashSlotWired).ToList();
        _index = 0;
        _limitDone = false;
        BatterySettings settings = AppServices.Settings.Load();
        NotifyLimitSwitch.IsOn = settings.NotifyWhenLimitReached;
        NotifyPlugInSwitch.IsOn = settings.NotifyPlugIn;
        NotifyTopUpSwitch.IsOn = settings.NotifyTopUpFinished;
        StartupSwitch.IsOn = false;
        CrashSwitch.IsOn = false;
        await LoadStartupStateAsync();
        Show();
    }

    private async Task LoadStartupStateAsync()
    {
        try { _startupState = (await StartupTask.GetAsync(StartupTaskId)).State; }
        catch (Exception) { _startupState = null; }
        // Already on: show it as on; the page never turns it off.
        StartupSwitch.IsOn = _startupState is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
    }

    private FirstRunStep Current => _steps[_index];

    private void Show()
    {
        FirstRunStep step = Current;
        StepText.Text = string.Format(Strings.Get("FirstRunStepOf"), _index + 1, _steps.Count);
        DetailText.Text = string.Empty;
        ActionButton.Visibility = StartupSwitch.Visibility = NotifyPanel.Visibility = CrashSlot.Visibility = Visibility.Collapsed;
        ResultInfo.IsOpen = false;
        BackButton.Visibility = _index > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = Strings.Get(step == FirstRunStep.Finish ? "FirstRunDone" : "FirstRunNext");
        SkipButton.Visibility = step == FirstRunStep.Finish ? Visibility.Collapsed : Visibility.Visible;

        switch (step)
        {
            case FirstRunStep.Welcome:
                TitleText.Text = Strings.Get("FirstRunWelcomeTitle");
                BodyText.Text = Strings.Get("FirstRunWelcomeText");
                break;
            case FirstRunStep.DeviceCheck:
                DeviceSupport support = AppServices.Support!;
                TitleText.Text = Strings.Get("FirstRunCheckTitle");
                BodyText.Text = DeviceNameText.Join(support.Device.Manufacturer, support.Device.Family.Length > 0 ? support.Device.Family : support.Device.Model);
                DetailText.Text = Strings.Get("SupportState" + support.State) + ". " + Strings.Get("FirstRunAction" + _plan.Action);
                BodyText.Visibility = BodyText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                break;
            case FirstRunStep.ChargeLimit:
                TitleText.Text = Strings.Get("FirstRunLimitTitle");
                BodyText.Text = Strings.Get("FirstRunLimitText");
                ActionButton.Content = Strings.Get("FirstRunLimitTurnOn");
                ActionButton.Visibility = _limitDone ? Visibility.Collapsed : Visibility.Visible;
                if (_limitDone) ShowResult(InfoBarSeverity.Success, Strings.Get("FirstRunLimitDone"));
                break;
            case FirstRunStep.PowerPlanManagerNote:
                TitleText.Text = Strings.Get("FirstRunPpmTitle");
                BodyText.Text = Strings.Get("FirstRunPpmText");
                break;
            case FirstRunStep.StartWithWindows:
                TitleText.Text = Strings.Get("FirstRunStartupTitle");
                BodyText.Text = Strings.Get("FirstRunStartupText");
                StartupSwitch.Visibility = Visibility.Visible;
                bool blocked = _startupState is StartupTaskState.DisabledByUser or StartupTaskState.DisabledByPolicy;
                StartupSwitch.IsEnabled = !blocked && _startupState is not null;
                if (blocked) DetailText.Text = Strings.Get("FirstRunStartupBlocked");
                break;
            case FirstRunStep.Notifications:
                TitleText.Text = Strings.Get("FirstRunNotifyTitle");
                BodyText.Text = Strings.Get("FirstRunNotifyText");
                NotifyPanel.Visibility = Visibility.Visible;
                break;
            case FirstRunStep.CrashReports:
                TitleText.Text = Strings.Get("FirstRunCrashTitle");
                BodyText.Text = Strings.Get("FirstRunCrashText");
                CrashSlot.Visibility = Visibility.Visible;
                break;
            default:
                TitleText.Text = Strings.Get("FirstRunFinishTitle");
                BodyText.Text = Strings.Get("FirstRunFinishText");
                break;
        }
        if (step != FirstRunStep.DeviceCheck) BodyText.Visibility = Visibility.Visible;
        UpdateEnabled();
    }

    private void UpdateEnabled() => BackButton.IsEnabled = NextButton.IsEnabled = SkipButton.IsEnabled = ActionButton.IsEnabled = !_busy;

    private void ShowResult(InfoBarSeverity severity, string message)
    {
        ResultInfo.Severity = severity;
        ResultInfo.Message = message;
        ResultInfo.IsOpen = true;
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

    private async void OnNext(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            if (Current == FirstRunStep.StartWithWindows && !await ApplyStartupAsync()) return;
            if (Current == FirstRunStep.Notifications) SaveNotifications();
            if (Current == FirstRunStep.Finish) { Complete(); return; }
            _index++;
            Show();
        }
        catch (Exception ex)
        {
            ShowResult(InfoBarSeverity.Error, ex.Message);
        }
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_busy || _index == 0) return;
        _index--;
        Show();
    }

    // Skip changes nothing on the current page and ends the flow.
    private void OnSkip(object sender, RoutedEventArgs e)
    {
        if (!_busy) Complete();
    }

    /// <summary>Only an explicit opt-in turns it on; an already-on task is left alone. False keeps the user on the page.</summary>
    private async Task<bool> ApplyStartupAsync()
    {
        bool wasOn = _startupState is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        if (!StartupSwitch.IsOn || wasOn || !StartupSwitch.IsEnabled) return true;
        try
        {
            StartupTask task = await StartupTask.GetAsync(StartupTaskId);
            StartupTaskState state = await task.RequestEnableAsync();
            _startupState = state;
            if (state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy) return true;
            StartupSwitch.IsOn = false;
            ShowResult(InfoBarSeverity.Warning, Strings.Get("FirstRunStartupBlocked"));
            return false;
        }
        catch (Exception ex)
        {
            StartupSwitch.IsOn = false;
            ShowResult(InfoBarSeverity.Warning, string.Format(Strings.Get("FirstRunStartupFailed"), ex.Message));
            return false;
        }
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

    private void Complete()
    {
        try { AppServices.Settings.Update(s => s.FirstRunCompletedUtc = DateTimeOffset.UtcNow); }
        catch (Exception) { }
        Finished?.Invoke();
    }
}
