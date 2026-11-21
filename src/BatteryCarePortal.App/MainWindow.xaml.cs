using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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

    private readonly WelcomePanel Welcome = new() { Visibility = Visibility.Collapsed };

    public MainWindow()
    {
        InitializeComponent();
        Root.Children.Add(Welcome);
        try { AppAppearance.Attach(this, Root, AppServices.Settings.Load().Appearance); } catch (Exception) { }
        AddNavigationShortcuts();
        bool simulated = Portal.Core.Battery.BatteryEnvironment.IsFakeBatteryEnabled;
        Title = simulated ? Strings.Get("AppName") + " - " + Strings.Get("WindowTitleSimulated") : Strings.Get("AppName");
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
        Welcome.Finished += () => Welcome.Visibility = Visibility.Collapsed;
        if (Portal.Core.Battery.FirstRunLogic.ShouldShow(AppServices.Settings.Load())) ShowWelcome();
    }

    // Ctrl+, opens Settings, Ctrl+1..5 the top-level pages in pane order; each key shows in the item's tooltip.
    private void AddNavigationShortcuts()
    {
        const Windows.System.VirtualKey comma = (Windows.System.VirtualKey)188;
        void Add(Windows.System.VirtualKey key, NavigationViewItem item, string shortcut)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = Windows.System.VirtualKeyModifiers.Control };
            accelerator.Invoked += (_, args) =>
            {
                args.Handled = true;
                Nav.SelectedItem = item;
            };
            Root.KeyboardAccelerators.Add(accelerator);
            ToolTipService.SetToolTip(item, $"{item.Content} ({shortcut})");
        }

        for (int i = 0; i < Nav.MenuItems.Count && i < 5; i++)
            if (Nav.MenuItems[i] is NavigationViewItem item)
                Add(Windows.System.VirtualKey.Number1 + i, item, $"Ctrl+{i + 1}");
        if (Nav.FooterMenuItems.OfType<NavigationViewItem>().FirstOrDefault() is { } settings)
            Add(comma, settings, "Ctrl+,");
    }

    /// <summary>Shows the welcome flow over the window once the device check has finished.</summary>
    public void ShowWelcome()
    {
        if (Welcome.Visibility == Visibility.Visible) return;
        if (AppServices.Support is null)
        {
            void Ready()
            {
                AppServices.SupportChanged -= Ready;
                DispatcherQueue.TryEnqueue(ShowWelcome);
            }
            AppServices.SupportChanged += Ready;
            return;
        }
        Welcome.Visibility = Visibility.Visible;
        _ = Welcome.StartAsync();
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
