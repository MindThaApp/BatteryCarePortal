using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BatteryCarePortal.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _updating;
    private bool _busy;

    public SettingsPage()
    {
        InitializeComponent();
        LayoutHelper.AlignToStart(Column);
        Loaded += (_, _) => ShowStoredValue();
    }

    private void ShowStoredValue()
    {
        _updating = true;
        Portal.Core.Battery.BatterySettings settings = AppServices.Settings.Load();
        BetaSwitch.IsOn = settings.BetaOtherBrandsOptIn;
        NotifyLimitSwitch.IsOn = settings.NotifyWhenLimitReached;
        NotifyPlugInSwitch.IsOn = settings.NotifyPlugIn;
        NotifyPlugInSwitch.Header = string.Format(Strings.Get("SettingsNotifyPlugIn"), settings.LowReminderPercent);
        NotifyTopUpSwitch.IsOn = settings.NotifyTopUpFinished;
        CrashSwitch.IsOn = settings.SendCrashReports;
        _updating = false;
    }

    private void OnShowWelcome(object sender, RoutedEventArgs e) => App.Instance?.ShowWelcome();

    private void OnNotifyToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        bool limit = NotifyLimitSwitch.IsOn, plugIn = NotifyPlugInSwitch.IsOn, topUp = NotifyTopUpSwitch.IsOn;
        try
        {
            AppServices.Settings.Update(s =>
            {
                s.NotifyWhenLimitReached = limit;
                s.NotifyPlugIn = plugIn;
                s.NotifyTopUpFinished = topUp;
            });
        }
        catch (Exception ex)
        {
            StatusInfo.Severity = InfoBarSeverity.Error;
            StatusInfo.Message = string.Format(Strings.Get("SettingsNotifyFailed"), ex.Message);
            StatusInfo.IsOpen = true;
            try { ShowStoredValue(); } catch (Exception) { }
        }
    }

    private void OnCrashToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        bool on = CrashSwitch.IsOn;
        try
        {
            AppServices.Settings.Update(s => s.SendCrashReports = on);
            CrashReporting.Apply(on);
        }
        catch (Exception ex)
        {
            StatusInfo.Severity = InfoBarSeverity.Error;
            StatusInfo.Message = string.Format(Strings.Get("SettingsNotifyFailed"), ex.Message);
            StatusInfo.IsOpen = true;
            try { ShowStoredValue(); } catch (Exception) { }
        }
    }

    // Turning beta on always needs the warning accepted; nothing is stored until the user confirms.
    // Async void: any exception is shown on the page, never let out (it would end the app).
    private async void OnBetaToggled(object sender, RoutedEventArgs e)
    {
        if (_updating || _busy) return;
        bool on = BetaSwitch.IsOn;
        _busy = true;
        BetaSwitch.IsEnabled = false;
        StatusInfo.IsOpen = false;
        try
        {
            bool confirmed = await ConfirmAsync(on);
            if (!confirmed) return;
            AppServices.SetBetaOptIn(on);
            await AppServices.CheckDeviceAsync();
        }
        catch (Exception ex)
        {
            StatusInfo.Severity = InfoBarSeverity.Error;
            StatusInfo.Message = string.Format(Strings.Get("SettingsBetaFailed"), ex.Message);
            StatusInfo.IsOpen = true;
        }
        finally
        {
            _busy = false;
            BetaSwitch.IsEnabled = true;
            try { ShowStoredValue(); } catch (Exception) { }
        }
    }

    private async Task<bool> ConfirmAsync(bool turningOn)
    {
        bool betaChanges = !turningOn && AppServices.Support is { BetaActive: true } && AppServices.Charge is { HasSavedOriginals: true };
        if (!turningOn && !betaChanges) return true;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Strings.Get(turningOn ? "SettingsBetaConfirmTitle" : "SettingsBetaOffTitle"),
            Content = new TextBlock { Text = Strings.Get(turningOn ? "SettingsBetaConfirmText" : "SettingsBetaOffText"), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Strings.Get(turningOn ? "SettingsBetaConfirmYes" : "SettingsBetaOffYes"),
            CloseButtonText = Strings.Get("DialogCancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
