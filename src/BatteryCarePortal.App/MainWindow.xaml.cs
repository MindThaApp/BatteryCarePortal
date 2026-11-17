using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using BatteryCarePortal.Pages;
using WinRT.Interop;

namespace BatteryCarePortal;

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> PageTypes = new()
    {
        ["Overview"] = typeof(OverviewPage),
        ["Charging"] = typeof(ChargingPage),
        ["HeatProtection"] = typeof(HeatProtectionPage),
        ["PowerFlow"] = typeof(PowerFlowPage),
        ["History"] = typeof(HistoryPage),
        ["Settings"] = typeof(SettingsPage),
    };

    public MainWindow()
    {
        InitializeComponent();
        bool simulated = Portal.Core.Battery.BatteryEnvironment.IsFakeBatteryEnabled;
        Title = simulated ? Strings.Get("AppName") + " - " + Strings.Get("OverviewSimulated/Text") : Strings.Get("AppName");
        SimulatedBadge.Visibility = simulated ? Visibility.Visible : Visibility.Collapsed;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 700));
        // Closing the window keeps the app in the tray; Quit in the tray menu exits.
        AppWindow.Closing += (_, e) =>
        {
            if (App.Instance?.IsExiting == true) return;
            e.Cancel = true;
            AppWindow.Hide();
        };
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    public void ShowAndActivate()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
    }

    public void ShowPage(string tag)
    {
        ShowAndActivate();
        if (Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(i => (i.Tag as string) == tag) is { } item)
            Nav.SelectedItem = item;
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string tag && PageTypes.TryGetValue(tag, out Type? page))
            ContentFrame.Navigate(page);
    }
}
