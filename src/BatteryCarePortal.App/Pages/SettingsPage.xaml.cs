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
        ShowAbout();
    }

    private const string IssuesUrl = "https://github.com/MindThaApp/BatteryCarePortal/issues";
    private const string PrivacyUrl = "https://github.com/MindThaApp/BatteryCarePortal/blob/main/docs/privacy-policy.md";

    private void ShowAbout()
    {
        string version = AppVersion.Short;
        AboutName.Text = Strings.Get("AppName");
        AboutVersion.Text = string.Format(Strings.Get("SettingsAboutVersion"), version.Length > 0 ? version : "?");
        AboutLicence.Text = Strings.Get("SettingsAboutLicence");
        CopyVersionButton.Content = Strings.Get("SettingsAboutCopy");
        WhatsNewButton.Content = Strings.Get("SettingsAboutWhatsNew");
        FeedbackButton.Content = Strings.Get("SettingsAboutFeedback");
        EmailButton.Content = Strings.Get("SettingsAboutEmail");
        PrivacyButton.Content = Strings.Get("SettingsAboutPrivacy");
        DataFolderButton.Content = Strings.Get("SettingsAboutDataFolder");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CopyVersionButton, Strings.Get("SettingsAboutCopyName"));
    }

    private void OnCopyVersion(object sender, RoutedEventArgs e)
    {
        try
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText($"{Strings.Get("AppName")} {AppVersion.Short}");
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        }
        catch (Exception ex) { ShowAboutFailure(ex); }
    }

    private void OnWhatsNew(object sender, RoutedEventArgs e) => App.Instance?.ShowWhatsNew();

    private async void OnFeedback(object sender, RoutedEventArgs e) => await OpenLinkAsync(IssuesUrl);

    private async void OnEmail(object sender, RoutedEventArgs e) => await OpenLinkAsync("mailto:mindthaapp@gmail.com");

    private async void OnPrivacy(object sender, RoutedEventArgs e) => await OpenLinkAsync(PrivacyUrl);

    private async Task OpenLinkAsync(string url)
    {
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri(url)); }
        catch (Exception ex) { ShowAboutFailure(ex); }
    }

    // Read only: Explorer opens the folder; the app changes nothing in it.
    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { AppServices.StateFolder }, UseShellExecute = false });
        }
        catch (Exception ex) { ShowAboutFailure(ex); }
    }

    private void ShowAboutFailure(Exception ex)
    {
        StatusInfo.Severity = InfoBarSeverity.Warning;
        StatusInfo.Message = string.Format(Strings.Get("SettingsAboutFailed"), ex.Message);
        StatusInfo.IsOpen = true;
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
        ShowAppearance(settings.Appearance);
        _updating = false;
    }

    private void ShowAppearance(Portal.Core.Theming.AppearanceSettings a)
    {
        ThemeCombo.SelectedIndex = (int)a.Theme;
        AccentCombo.SelectedIndex = (int)a.Accent;
        MaterialCombo.SelectedIndex = (int)a.Material;
        CornersCombo.SelectedIndex = (int)a.Corners;
        DensityCombo.SelectedIndex = (int)a.Density;
        MotionCombo.SelectedIndex = (int)a.Motion;
        TextSizeCombo.SelectedIndex = (int)a.TextSize;
    }

    private static int Choice(ComboBox box) => box.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int v) ? v : 0;

    private void OnAppearanceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        SaveAppearance(a =>
        {
            a.Theme = (Portal.Core.Theming.ThemeChoice)Choice(ThemeCombo);
            a.Accent = (Portal.Core.Theming.AccentChoice)Choice(AccentCombo);
            a.Material = (Portal.Core.Theming.MaterialChoice)Choice(MaterialCombo);
            a.Corners = (Portal.Core.Theming.CornerChoice)Choice(CornersCombo);
            a.Density = (Portal.Core.Theming.DensityChoice)Choice(DensityCombo);
            a.Motion = (Portal.Core.Theming.MotionChoice)Choice(MotionCombo);
            a.TextSize = (Portal.Core.Theming.TextSizeChoice)Choice(TextSizeCombo);
        });
    }

    private void OnResetAppearance(object sender, RoutedEventArgs e) =>
        SaveAppearance(a => a.Reset());

    // Applies first so the change shows at once; a failed save is reported and the saved values come back on screen.
    private void SaveAppearance(Action<Portal.Core.Theming.AppearanceSettings> change)
    {
        try
        {
            Portal.Core.Battery.BatterySettings saved = AppServices.Settings.Update(s => change(s.Appearance));
            AppAppearance.Apply(saved.Appearance);
            _updating = true;
            try { ShowAppearance(saved.Appearance); } finally { _updating = false; }
        }
        catch (Exception ex)
        {
            StatusInfo.Severity = InfoBarSeverity.Error;
            StatusInfo.Message = string.Format(Strings.Get("SettingsAppearanceFailed"), ex.Message);
            StatusInfo.IsOpen = true;
            try { ShowStoredValue(); } catch (Exception) { }
        }
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
