using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Portal.Core.Battery;
using Portal.Core.UI;
using Windows.ApplicationModel;

namespace BatteryCarePortal;

/// <summary>
/// The welcome flow, three steps: why a charge limit helps and your laptop, picking the limit, and where the tray is plus your
/// choices. Choices apply as they are made, so Skip keeps them. The charge limit goes only through
/// <see cref="ChargeLimitController.SetLimitAsync"/> from the user's click on its button; Start with recommended settings
/// and Skip never write to the firmware.
/// </summary>
public sealed partial class WelcomePanel : UserControl
{
    private const string StartupTaskId = "BatteryCarePortalStartup";
    private const int PageCount = 3;
    private const float SlideDistance = 24f;

    private FirstRunPlan _plan = new(FirstRunNextAction.NothingToDo, Array.Empty<FirstRunStep>());
    private int _index;
    private int _visiblePage = -1;
    private bool _busy;
    private bool _limitDone;
    private bool _loading = true;
    private int? _preferredLimit;
    private StartupTaskState? _startupState;
    private readonly ScrollViewer[] _pages;

    public event Action? Finished;

    public WelcomePanel()
    {
        InitializeComponent();
        _pages = new[] { Page0, Page1, Page2 };
        foreach (ScrollViewer page in _pages) ElementCompositionPreview.SetIsTranslationEnabled(page, true);
        try
        {
            if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
                Tint0.Visibility = Tint1.Visibility = Tint2.Visibility = Visibility.Collapsed;
        }
        catch (Exception) { }
        LayoutHelper.AlignToStart(BusyRing);
        BackButton.Content = Strings.Get("FirstRunBack");
        SkipButton.Content = Strings.Get("FirstRunSkip");
        RecommendedButton.Content = Strings.Get("FirstRunRecommended");
        StartupTitle.Text = Strings.Get("FirstRunStartupSwitch");
        AutomationProperties.SetName(StartupSwitch, StartupTitle.Text);
        AutomationProperties.SetName(NotifySwitch, Strings.Get("WelcomeRemindersTitle"));
        CrashLine.Text = Strings.Get("WelcomeCrashLine");
        AutomationProperties.SetName(CrashSwitch, CrashLine.Text);
        CrashLink.Content = Strings.Get("CrashWhatsSent");
        CrashFlyoutTitle.Text = Strings.Get("CrashWhatsSent");
        CrashSentText.Text = Strings.Get("WelcomeCrashSent");
        CrashNotSentText.Text = Strings.Get("WelcomeCrashNotSent");
        CrashRetentionText.Text = Strings.Get("WelcomeCrashRetention");
        CrashPrivacyLink.Content = Strings.Get("WelcomeCrashPrivacy");
        AutomationProperties.SetName(CrashPrivacyLink, Strings.Get("WelcomeCrashPrivacy"));
        CrashLink.Click += (_, _) => FlyoutBase.ShowAttachedFlyout(CrashLink);
        StartupText.Text = Strings.Get("WelcomeStartupText");
        NotifyTitle.Text = Strings.Get("WelcomeRemindersTitle");
        NotifyText.Text = Strings.Get("WelcomeRemindersText");
        TitleText.Text = Strings.Get("WelcomeHelloTitle");
        BodyText.Text = Strings.Get("WelcomeWhyBody");
        DeviceTitle.Text = Strings.Get("FirstRunCheckTitle");
        LimitValueCaption.Text = Strings.Get("WelcomeLimitValueCaption");
        LimitText.Text = Strings.Get("FirstRunLimitText");
        ActionButton.Content = Strings.Get("FirstRunLimitTurnOn");
        UnavailableTitle.Text = Strings.Get("WelcomeLimitUnavailableTitle");
        PpmTitle.Text = Strings.Get("FirstRunPpmTitle");
        PpmText.Text = Strings.Get("FirstRunPpmText");
        TrayTitle.Text = Strings.Get("WelcomeTrayTitle");
        TrayText.Text = Strings.Get("WelcomeTrayBody");
        AutomationProperties.SetName(TrayIllustration, Strings.Get("WelcomeTrayIllustration"));
        FreeText.Text = Strings.Get("WelcomeFreeLine");
        BuildDots();
        Footer.LayoutUpdated += (_, _) => UpdateFooterLayout();
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
            Dots.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4) });
    }

    private void UpdateDots()
    {
        for (int i = 0; i < Dots.Children.Count; i++)
            if (Dots.Children[i] is Border dot)
            {
                dot.Width = i == _index ? 20 : 8;
                dot.Background = (Brush)Application.Current.Resources[i == _index ? "AccentFillColorDefaultBrush" : "ControlStrongFillColorDefaultBrush"];
            }
        AutomationProperties.SetName(Dots, string.Format(Strings.Get("FirstRunStepOf"), _index + 1, PageCount));
    }

    /// <summary>Call once the device check has finished (<see cref="AppServices.Support"/> is not null). Step is zero-based.</summary>
    public async Task StartAsync(int initialStep = 0)
    {
        DeviceSupport support = AppServices.Support!;
        PowerPlanManagerStatus ppm = await Task.Run(AppServices.CheckPowerPlanManager);
        _plan = FirstRunLogic.For(support.State, ppm);
        _index = Math.Clamp(initialStep, 0, PageCount - 1);
        _limitDone = false;
        _loading = true;
        BatterySettings settings = AppServices.Settings.Load();
        NotifySwitch.IsOn = settings.NotifyWhenLimitReached || settings.NotifyPlugIn || settings.NotifyTopUpFinished;
        CrashSwitch.IsChecked = settings.SendCrashReports;
        await LoadStartupStateAsync();
        _loading = false;

        // Read only: the value the button would use. Nothing is written here.
        _preferredLimit = null;
        if (_plan.OffersChargeLimit && AppServices.Charge is { } controller)
            _preferredLimit = await Task.Run<int?>(() =>
            {
                try { return ChargeLimitChoice.PreferredValue(controller.Capabilities, controller.ReadState(), controller.LoadIntent()); }
                catch (Exception) { return null; }
            });

        DeviceText.Text = DeviceNameText.Join(support.Device.Manufacturer, support.Device.Family.Length > 0 ? support.Device.Family : support.Device.Model);
        DeviceText.Visibility = DeviceText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailText.Text = Strings.Get("SupportState" + support.State) + ". " + Strings.Get("FirstRunAction" + _plan.Action);
        UnavailableText.Text = Strings.Get("FirstRunAction" + _plan.Action);
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
        bool last = _index == PageCount - 1;
        BackButton.Visibility = _index > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = Strings.Get(last ? "FirstRunDone" : "FirstRunNext");
        SkipButton.Visibility = RecommendedButton.Visibility = last ? Visibility.Collapsed : Visibility.Visible;

        bool offers = _plan.OffersChargeLimit;
        bool ppmNote = _plan.Steps.Contains(FirstRunStep.PowerPlanManagerNote);
        LimitHeading.Text = Strings.Get(offers ? "WelcomeLimitTitle" : "WelcomeLimitTitleOff");
        LimitBody.Text = Strings.Get(offers ? "WelcomeLimitBody" : "WelcomeLimitBodyOff");
        LimitPanel.Visibility = offers ? Visibility.Visible : Visibility.Collapsed;
        PpmPanel.Visibility = ppmNote ? Visibility.Visible : Visibility.Collapsed;
        UnavailablePanel.Visibility = !offers && !ppmNote ? Visibility.Visible : Visibility.Collapsed;
        bool hasValue = _preferredLimit is not null;
        LimitValue.Text = hasValue ? string.Format(Strings.Get("WelcomeLimitValue"), _preferredLimit) : string.Empty;
        LimitValue.Visibility = LimitValueCaption.Visibility = hasValue ? Visibility.Visible : Visibility.Collapsed;
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
        int previous = _visiblePage;
        _visiblePage = _index;
        SwitchPages(previous, _index);
        UpdateFooterLayout();
        ApplyCaptureScale();
    }

    private enum PageMotion { None, FadeQuick, Fade, SlideAndFade }

    // Motion Off (or a safe start) is instant; Windows animations off is a very short fade; Reduced is fade only.
    private static PageMotion CurrentMotion()
    {
        if (UiSafeMode.Enabled || UiMotion.Mode == UiMotionMode.Off) return PageMotion.None;
        bool windowsAnimations = true;
        try { windowsAnimations = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled; } catch (Exception) { }
        if (!windowsAnimations) return PageMotion.FadeQuick;
        return UiMotion.Mode == UiMotionMode.Reduced ? PageMotion.Fade : PageMotion.SlideAndFade;
    }

    private static void ResetVisual(UIElement page)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(page);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");
        visual.Opacity = 1f;
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
    }

    // Arriving takes 333 ms (slide 24 px + fade in, decelerating), leaving 222 ms; both start together and the arriving
    // fade holds back until the leaving page has mostly gone.
    private void SwitchPages(int from, int to)
    {
        for (int i = 0; i < _pages.Length; i++)
        {
            if (i == to) continue;
            if (i == from && from >= 0 && CurrentMotion() != PageMotion.None) continue;
            _pages[i].Visibility = Visibility.Collapsed;
            ResetVisual(_pages[i]);
        }
        ScrollViewer target = _pages[to];
        target.Visibility = Visibility.Visible;
        target.ChangeView(null, 0, null, disableAnimation: true);
        ResetVisual(target);

        PageMotion motion = CurrentMotion();
        if (from < 0 || from == to || motion == PageMotion.None) return;
        try
        {
            ScrollViewer leaving = _pages[from];
            Visual outVisual = ElementCompositionPreview.GetElementVisual(leaving);
            Visual inVisual = ElementCompositionPreview.GetElementVisual(target);
            Compositor compositor = inVisual.Compositor;
            float dir = to > from ? 1f : -1f;
            bool slide = motion == PageMotion.SlideAndFade;
            TimeSpan arrive = motion == PageMotion.FadeQuick ? TimeSpan.FromMilliseconds(83) : TimeSpan.FromMilliseconds(333);
            TimeSpan leave = motion == PageMotion.FadeQuick ? TimeSpan.FromMilliseconds(55) : TimeSpan.FromMilliseconds(222);
            CubicBezierEasingFunction easeIn = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
            CubicBezierEasingFunction easeOut = compositor.CreateCubicBezierEasingFunction(new Vector2(0.7f, 0f), new Vector2(1f, 0.5f));

            CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

            ScalarKeyFrameAnimation fadeOut = compositor.CreateScalarKeyFrameAnimation();
            fadeOut.InsertKeyFrame(0f, 1f);
            fadeOut.InsertKeyFrame(1f, 0f, easeOut);
            fadeOut.Duration = leave;
            outVisual.StartAnimation("Opacity", fadeOut);

            ScalarKeyFrameAnimation fadeIn = compositor.CreateScalarKeyFrameAnimation();
            fadeIn.InsertKeyFrame(0f, 0f);
            fadeIn.InsertKeyFrame(1f, 1f, easeIn);
            fadeIn.Duration = arrive - TimeSpan.FromMilliseconds(motion == PageMotion.FadeQuick ? 0 : 111);
            fadeIn.DelayTime = TimeSpan.FromMilliseconds(motion == PageMotion.FadeQuick ? 0 : 111);
            inVisual.Opacity = 0f;
            inVisual.StartAnimation("Opacity", fadeIn);

            if (slide)
            {
                Vector3KeyFrameAnimation moveOut = compositor.CreateVector3KeyFrameAnimation();
                moveOut.InsertKeyFrame(0f, Vector3.Zero);
                moveOut.InsertKeyFrame(1f, new Vector3(-dir * SlideDistance, 0f, 0f), easeOut);
                moveOut.Duration = leave;
                outVisual.StartAnimation("Translation", moveOut);

                Vector3KeyFrameAnimation moveIn = compositor.CreateVector3KeyFrameAnimation();
                moveIn.InsertKeyFrame(0f, new Vector3(dir * SlideDistance, 0f, 0f));
                moveIn.InsertKeyFrame(1f, Vector3.Zero, easeIn);
                moveIn.Duration = arrive;
                inVisual.StartAnimation("Translation", moveIn);
            }
            batch.End();
            int leftIndex = from;
            batch.Completed += (_, _) =>
            {
                if (_visiblePage == leftIndex) return;
                leaving.Visibility = Visibility.Collapsed;
                ResetVisual(leaving);
            };
        }
        catch (Exception)
        {
            // Motion is decoration only: show the new page as it is.
            for (int i = 0; i < _pages.Length; i++)
            {
                _pages[i].Visibility = i == to ? Visibility.Visible : Visibility.Collapsed;
                ResetVisual(_pages[i]);
            }
        }
    }

    // Step 1 is short: its content sits in the middle of the page instead of leaving a gap above the footer.
    private void OnPage0SizeChanged(object sender, SizeChangedEventArgs e) => Page0Content.MinHeight = e.NewSize.Height;

    // Screenshot runs only: scale the text of the open step through the same walker the Text size setting uses.
    public double? CaptureTextFactor { get; set; }

    private void ApplyCaptureScale()
    {
        if (CaptureTextFactor is not double f) return;
        AppAppearance.ScaleText(this, f);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => AppAppearance.ScaleText(this, f));
    }

    private void OnFooterSizeChanged(object sender, SizeChangedEventArgs e) => UpdateFooterLayout();

    private string _footerSignature = string.Empty;
    private bool _leftVertical;

    // Re-checked on every layout pass, so it follows text scaling and step changes: when both button groups no longer fit
    // in one row, Back/Next get their own right-aligned row above Skip/Recommended.
    private void UpdateFooterLayout()
    {
        double available = Footer.ActualWidth - Footer.Padding.Left - Footer.Padding.Right;
        if (available <= 0) return;
        FooterLeft.Orientation = Orientation.Horizontal;
        FooterLeft.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        FooterRight.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double left = FooterLeft.DesiredSize.Width, right = FooterRight.DesiredSize.Width;
        string signature = $"{available:F0}|{left:F0}|{right:F0}";
        if (signature == _footerSignature)
        {
            if (_leftVertical) FooterLeft.Orientation = Orientation.Vertical;
            return;
        }
        _footerSignature = signature;
        bool stack = left + right + Footer.ColumnSpacing > available;
        Grid.SetColumn(FooterLeft, 0);
        Grid.SetColumnSpan(FooterLeft, stack ? 3 : 1);
        Grid.SetRow(FooterLeft, stack ? 1 : 0);
        Grid.SetRow(FooterRight, 0);
        Grid.SetColumn(FooterRight, stack ? 0 : 2);
        Grid.SetColumnSpan(FooterRight, stack ? 3 : 1);
        _leftVertical = stack && left > available;
        if (_leftVertical) FooterLeft.Orientation = Orientation.Vertical;
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

    // Only non-firmware recommendations: the three reminders on. Crash reports keep the tick box as the user left it
    // (on by default), and the charge limit and Start with Windows stay the user's own choice.
    private void OnRecommended(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        NotifySwitch.IsOn = true;
        SaveNotifications();
        Finished?.Invoke();
    }

    private void SetCrashTickQuietly(bool on)
    {
        _loading = true;
        try { CrashSwitch.IsChecked = on; } finally { _loading = false; }
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

    private async void OnCrashWhatsSent(object sender, RoutedEventArgs e)
    {
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/MindThaApp/BatteryCarePortal/blob/main/docs/privacy-policy.md#crash-reports-on-by-default")); }
        catch (Exception) { }
    }

    private void OnCrashToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try { SaveCrashReports(); }
        catch (Exception ex) { ShowPrefsResult(InfoBarSeverity.Error, ex.Message); }
    }

    private void SaveNotifications()
    {
        bool limit = NotifySwitch.IsOn, plugIn = limit, topUp = limit;
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
        bool on = CrashSwitch.IsChecked == true;
        if (AppServices.Settings.Load().SendCrashReports == on) return;
        AppServices.Settings.Update(s => s.SendCrashReports = on);
        CrashReporting.Apply(on);
    }
}
