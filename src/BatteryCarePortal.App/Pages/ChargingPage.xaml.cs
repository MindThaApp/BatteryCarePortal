using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Portal.Core.Battery;

namespace BatteryCarePortal.Pages;

public sealed partial class ChargingPage : Page
{
    private enum Notice { None, NeedsReapply, ChangedElsewhere }

    private bool _busy;
    private bool _updating;
    private bool _gateAllowed;
    private Notice _notice;
    private ChargeActionStatus? _lastAction;

    public ChargingPage()
    {
        InitializeComponent();
        LayoutHelper.AlignToStart(Column);
        Loaded += (_, _) =>
        {
            AppServices.SupportChanged += Refresh;
            AppServices.ChargeStateChanged += Refresh;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            AppServices.SupportChanged -= Refresh;
            AppServices.ChargeStateChanged -= Refresh;
        };
    }

    // Async void handlers on this page never let an exception out: it would end the app.
    private async void Refresh()
    {
        try { await RefreshAsync(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ShowError(Exception ex)
    {
        try { ShowStatus(InfoBarSeverity.Error, string.Format(Strings.Get("ChargeResultFailed"), ex.Message)); }
        catch (Exception) { }
    }

    private async Task RefreshAsync()
    {
        DeviceSupport? support = AppServices.Support;
        ChargeLimitController? controller = AppServices.Charge;
        if (support is null || controller is null)
        {
            ShowUnavailable(support is null ? Strings.Get("SupportCheckingNote") : (await Task.Run(() => ChargeStatusView.Build(null, false))).Sentence);
            return;
        }

        (ChargeLimitState state, WriteGateResult gate, ChargeIntent? intent, IntentFileState intentState, ChargeStatusView view) =
            await Task.Run(() => (controller.ReadState(), controller.EvaluateGate(), controller.LoadIntent(), controller.IntentState,
                ChargeStatusView.Build(_lastAction, _busy)));
        if (!ReferenceEquals(controller, AppServices.Charge)) return;

        LimitCard.Visibility = RestoreCard.Visibility = Visibility.Visible;
        ResetPanel.Visibility = intentState == IntentFileState.Unusable ? Visibility.Visible : Visibility.Collapsed;
        _gateAllowed = gate.Allowed;
        UnavailableText.Text = view.Sentence;
        UnavailableText.Visibility = Visibility.Visible;
        BetaChip.Visibility = gate.Decision == WriteGateDecision.BetaOptIn ? Visibility.Visible : Visibility.Collapsed;

        ConfigureCapabilities(controller.Capabilities, state, intent);
        ShowState(controller.Capabilities, state);
        ShowNotice(intent);
        UpdateEnabled();
    }

    private void ShowUnavailable(string message)
    {
        UnavailableText.Text = message;
        UnavailableText.Visibility = Visibility.Visible;
        LimitCard.Visibility = RestoreCard.Visibility = Visibility.Collapsed;
        _notice = Notice.None;
        NoticeInfo.IsOpen = false;
    }

    private static string RefusalText(WriteRefusal refusal, string fallback) => refusal switch
    {
        WriteRefusal.OtherOwner => Strings.Get("ChargeStatusPpmOwns"),
        WriteRefusal.OtherOwnerUnconfirmed => Strings.Get("ChargeStatusPpmUnconfirmed"),
        WriteRefusal.BetaOff => Strings.Get("ChargeStatusBetaOff"),
        WriteRefusal.NoReadBack => Strings.Get("ChargeStatusNotVerified"),
        WriteRefusal.NotDetected => Strings.Get("ChargeStatusNotDetected"),
        _ => fallback,
    };

    // The value control appears only when the firmware takes a value; conservation mode is on/off at a fixed level.
    private void ConfigureCapabilities(ChargeLimitCapabilities caps, ChargeLimitState state, ChargeIntent? intent)
    {
        int preferred = intent is { LimitOn: true } ? intent.Limit : state.Limit is int l && l < 100 ? l : 80;
        ValueSlider.Visibility = ValueCombo.Visibility = Visibility.Collapsed;
        ValuePanel.Visibility = Visibility.Collapsed;
        switch (caps.Kind)
        {
            case ChargeLimitKind.OnOffConservation:
                CapabilityText.Text = string.Format(Strings.Get("ChargeCapConservation"), caps.ConservationPercent);
                break;
            case ChargeLimitKind.ContinuousRange:
                CapabilityText.Text = string.Format(Strings.Get("ChargeCapRange"), caps.MinLimit, caps.MaxLimit);
                ValueSlider.Minimum = caps.MinLimit;
                ValueSlider.Maximum = caps.MaxLimit;
                ValueSlider.Value = Math.Clamp(preferred, caps.MinLimit, caps.MaxLimit);
                ValueSlider.Visibility = ValuePanel.Visibility = Visibility.Visible;
                break;
            case ChargeLimitKind.FixedValues:
                CapabilityText.Text = Strings.Get("ChargeCapFixed");
                ValueCombo.Items.Clear();
                foreach (int v in caps.DiscreteValues.Where(v => v < 100))
                    ValueCombo.Items.Add(new ComboBoxItem { Content = $"{v}%", Tag = v });
                ValueCombo.SelectedItem = ValueCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == preferred)
                    ?? ValueCombo.Items.OfType<ComboBoxItem>().LastOrDefault();
                ValueCombo.Visibility = ValuePanel.Visibility = Visibility.Visible;
                break;
            default:
                CapabilityText.Text = Strings.Get("ChargeStatusNotDetected");
                break;
        }
    }

    private void ShowState(ChargeLimitCapabilities caps, ChargeLimitState state)
    {
        _updating = true;
        LimitSwitch.IsOn = state.Limit is int l && l < 100;
        _updating = false;
        ReadBackText.Text = state.Limit switch
        {
            null => string.Format(Strings.Get("ChargeReadBackUnknown"), state.Message ?? string.Empty),
            >= 100 => Strings.Get("ChargeReadBackOff"),
            int limit when caps.Kind == ChargeLimitKind.OnOffConservation => string.Format(Strings.Get("ChargeReadBackConservationOn"), limit),
            int limit => string.Format(Strings.Get("ChargeReadBackOn"), limit),
        };
    }

    private void ShowNotice(ChargeIntent? intent)
    {
        _notice = intent is { Managed: true, LimitOn: true } && (intent.NeedsReapply || intent.ReapplyFailed) ? Notice.NeedsReapply
            : AppServices.LastReapply?.Status == ChargeActionStatus.ChangedElsewhere && intent is { Managed: true } ? Notice.ChangedElsewhere
            : Notice.None;
        switch (_notice)
        {
            case Notice.NeedsReapply:
                NoticeInfo.Title = Strings.Get("ChargeNoticeReapplyTitle");
                NoticeInfo.Message = Strings.Get(intent!.ReapplyFailed ? "ChargeNoticeReapplyFailedText" : "ChargeNoticeReapplyText");
                NoticePrimaryButton.Content = Strings.Get("ChargeNoticePutBack");
                NoticeSecondaryButton.Visibility = Visibility.Collapsed;
                break;
            case Notice.ChangedElsewhere:
                NoticeInfo.Title = Strings.Get("ChargeNoticeChangedTitle");
                NoticeInfo.Message = AppServices.LastReapply?.Message ?? string.Empty;
                NoticePrimaryButton.Content = Strings.Get("ChargeNoticeUseApp");
                NoticeSecondaryButton.Content = Strings.Get("ChargeNoticeKeepLaptop");
                NoticeSecondaryButton.Visibility = Visibility.Visible;
                break;
        }
        NoticeInfo.IsOpen = _notice != Notice.None;
    }

    private int? CurrentValue() => AppServices.Charge?.Capabilities.Kind switch
    {
        ChargeLimitKind.ContinuousRange => (int)Math.Round(ValueSlider.Value),
        ChargeLimitKind.FixedValues => (ValueCombo.SelectedItem as ComboBoxItem)?.Tag as int?,
        _ => null,
    };

    private async Task Guarded(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void OnLimitToggled(object sender, RoutedEventArgs e) => await Guarded(async () =>
    {
        if (_updating || _busy) return;
        bool on = LimitSwitch.IsOn;
        int? value = CurrentValue();
        await RunAsync(c => c.SetLimitAsync(on, value));
    });

    private async void OnApplyValue(object sender, RoutedEventArgs e) => await Guarded(async () =>
    {
        int? value = CurrentValue();
        await RunAsync(c => c.SetLimitAsync(true, value));
    });

    private async void OnRestore(object sender, RoutedEventArgs e) => await Guarded(async () =>
    {
        if (_busy) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Strings.Get("ChargeRestoreConfirmTitle"),
            Content = Strings.Get("ChargeRestoreConfirmText"),
            PrimaryButtonText = Strings.Get("ChargeRestoreConfirmYes"),
            CloseButtonText = Strings.Get("DialogCancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await RunAsync(c => c.RestoreDefaultsAsync());
    });

    private async void OnResetState(object sender, RoutedEventArgs e) =>
        await Guarded(() => RunAsync(c => Task.FromResult(c.ResetAppState())));

    private async void OnNoticePrimary(object sender, RoutedEventArgs e) => await Guarded(async () =>
    {
        ChargeLimitController? controller = AppServices.Charge;
        ChargeIntent? saved = controller is null ? null : await Task.Run(controller.LoadIntent);
        int? value = saved is { LimitOn: true } intent ? intent.Limit : CurrentValue();
        await RunAsync(c => c.SetLimitAsync(true, value));
    });

    private async void OnNoticeSecondary(object sender, RoutedEventArgs e) =>
        await Guarded(() => RunAsync(c => Task.FromResult(c.KeepFirmwareValue())));

    /// <summary>One change at a time: the controls stay disabled until the write and its read-back finish.</summary>
    private async Task RunAsync(Func<ChargeLimitController, Task<ChargeActionResult>> action)
    {
        if (_busy || AppServices.Charge is not { } controller) return;
        SetBusy(true);
        try
        {
            ChargeActionResult result = await Task.Run(() => action(controller));
            ShowResult(result);
            _lastAction = result.Status;
            if (result.Succeeded) AppServices.ClearReapplyNotice();
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, string.Format(Strings.Get("ChargeResultFailed"), ex.Message));
        }
        finally
        {
            SetBusy(false);
            Refresh();
        }
    }

    private void ShowResult(ChargeActionResult r)
    {
        switch (r.Status)
        {
            case ChargeActionStatus.Applied:
            case ChargeActionStatus.AlreadySet:
                ShowStatus(InfoBarSeverity.Success, Strings.Get("ChargeResultApplied"));
                break;
            case ChargeActionStatus.AppliedNotConfirmed:
                ShowStatus(InfoBarSeverity.Warning, Strings.Get("ChargeResultNotConfirmed"));
                break;
            case ChargeActionStatus.NeedsElevation:
                ShowStatus(InfoBarSeverity.Warning, Strings.Get("ChargeResultNeedsElevation"));
                break;
            case ChargeActionStatus.Cancelled:
                ShowStatus(InfoBarSeverity.Informational, Strings.Get("ChargeResultCancelled"));
                break;
            case ChargeActionStatus.Refused:
                ShowStatus(InfoBarSeverity.Warning, RefusalText(r.Refusal, r.Message));
                break;
            case ChargeActionStatus.ReadBackMismatch:
                ShowStatus(InfoBarSeverity.Error, string.Format(Strings.Get("ChargeResultMismatch"), r.Message));
                break;
            case ChargeActionStatus.Busy:
                ShowStatus(InfoBarSeverity.Informational, Strings.Get("ChargeResultBusy"));
                break;
            case ChargeActionStatus.NothingToRestore:
                ShowStatus(InfoBarSeverity.Informational, Strings.Get("ChargeResultNothingToRestore"));
                break;
            case ChargeActionStatus.StateReset:
                ShowStatus(InfoBarSeverity.Success, r.Message);
                break;
            case ChargeActionStatus.NothingToDo:
                ShowStatus(InfoBarSeverity.Informational, r.Message);
                break;
            default:
                ShowStatus(InfoBarSeverity.Error, string.Format(Strings.Get("ChargeResultFailed"), r.Message));
                break;
        }
    }

    private void ShowStatus(InfoBarSeverity severity, string message)
    {
        StatusInfo.Severity = severity;
        StatusInfo.Message = message;
        StatusInfo.IsOpen = true;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        if (busy) UnavailableText.Text = ChargeStatusView.SentenceFor(ChargeStatusKind.Writing);
        UpdateEnabled();
    }

    private void UpdateEnabled()
    {
        bool canWrite = !_busy && _gateAllowed;
        LimitSwitch.IsEnabled = canWrite;
        ApplyValueButton.IsEnabled = canWrite && LimitSwitch.IsOn;
        ValueSlider.IsEnabled = ValueCombo.IsEnabled = canWrite;
        RestoreButton.IsEnabled = canWrite;
        ResetStateButton.IsEnabled = !_busy;
        NoticePrimaryButton.IsEnabled = canWrite;
        NoticeSecondaryButton.IsEnabled = !_busy;
    }
}
