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
        Loaded += (_, _) => ShowStoredValue();
    }

    private void ShowStoredValue()
    {
        _updating = true;
        BetaSwitch.IsOn = AppServices.Settings.Load().BetaOtherBrandsOptIn;
        _updating = false;
    }

    // Turning beta on always needs the warning accepted; nothing is stored until the user confirms.
    private async void OnBetaToggled(object sender, RoutedEventArgs e)
    {
        if (_updating || _busy) return;
        bool on = BetaSwitch.IsOn;
        _busy = true;
        BetaSwitch.IsEnabled = false;
        try
        {
            bool confirmed = await ConfirmAsync(on);
            if (!confirmed)
            {
                ShowStoredValue();
                return;
            }
            AppServices.SetBetaOptIn(on);
            await AppServices.CheckDeviceAsync();
        }
        finally
        {
            _busy = false;
            BetaSwitch.IsEnabled = true;
            ShowStoredValue();
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
