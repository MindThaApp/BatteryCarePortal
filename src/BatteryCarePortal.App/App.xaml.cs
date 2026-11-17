using System.Drawing;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using Portal.Core.Battery;

namespace BatteryCarePortal;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private Icon? _trayGlyph;
    private MainWindow? _mainWindow;
    private TrayFlyoutWindow? _flyout;
    private bool _exiting;
    private bool _limitOffNoticeShown;

    public static App? Instance { get; private set; }

    public App()
    {
        Instance = this;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mainWindow = new MainWindow();
        _flyout = new TrayFlyoutWindow();
        CreateTrayIcon();
        AppServices.Battery.SnapshotChanged += (_, _) => _mainWindow.DispatcherQueue.TryEnqueue(UpdateTray);
        AppServices.SupportChanged += UpdateTray;
        AppServices.ChargeStateChanged += UpdateTray;
        AppServices.ChargeWriteChanged += UpdateTray;
        try { Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) => _mainWindow.DispatcherQueue.TryEnqueue(UpdateTray); } catch (Exception) { }
        AppServices.ReapplyNeedsUser += ShowLimitOffNotification;
        AppServices.ToastDue += toast => { if (!_exiting) AppToasts.Show(toast); };
        AppToasts.PageRequested += page => _mainWindow.DispatcherQueue.TryEnqueue(() => ShowPage(page));
        AppToasts.Register();
        AppServices.Start();

        // A login start (StartupTask) lives in the tray; a notification click opens its page; any other launch shows the window.
        AppActivationArguments activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (AppToasts.PageFromActivation(activation) is { } page) ShowPage(page);
        else if (activation.Kind != ExtendedActivationKind.StartupTask) ShowMainWindow();
    }

    private void ShowPage(string page)
    {
        if (!_exiting) _mainWindow?.ShowPage(page);
    }

    private void CreateTrayIcon()
    {
        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = Strings.Get("TrayOpen") };
        open.Click += (_, _) => ShowMainWindow();
        var quit = new MenuFlyoutItem { Text = Strings.Get("TrayQuit") };
        quit.Click += (_, _) => ExitApplication();
        menu.Items.Add(open);
        menu.Items.Add(quit);

        _trayGlyph = BatteryTrayIconRenderer.Render(0, null, 32, lightTaskbar: TaskbarIsLight());
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = Strings.Get("AppName"),
            Icon = _trayGlyph,
            ContextFlyout = menu,
            ContextMenuMode = ContextMenuMode.PopupMenu,
            MenuActivation = PopupActivationMode.RightClick,
        };
        _trayIcon.TrayIcon.MessageWindow.MouseEventReceived += (_, e) =>
        {
            if (e.MouseEvent == MouseEvent.IconLeftMouseUp) _flyout?.Toggle();
            else if (e.MouseEvent == MouseEvent.IconLeftDoubleClick) ShowMainWindow();
            else if (e.MouseEvent == MouseEvent.BalloonToolTipClicked && _limitOffNoticeShown)
            {
                _limitOffNoticeShown = false;
                _mainWindow?.DispatcherQueue.TryEnqueue(() => _mainWindow?.ShowPage("Charging"));
            }
        };
        _trayIcon.ForceCreate();
    }

    // The user puts the limit back from the Charging page; the app never raises an administrator prompt by itself.
    private void ShowLimitOffNotification(ChargeActionResult result)
    {
        if (_exiting || _trayIcon is null) return;
        try
        {
            _trayIcon.ShowNotification(Strings.Get("NotifyLimitOffTitle"), Strings.Get("NotifyLimitOffText"));
            _limitOffNoticeShown = true;
        }
        catch (Exception) { }
    }

    // Level from the battery reader; the tick is the read-only charge limit, drawn only when the device has one set.
    private void UpdateTray()
    {
        if (_exiting || _trayIcon is null) return;
        BatterySnapshot snapshot = AppServices.Battery.Current;
        int? limit = AppServices.Support is { ChargeLimitAvailable: true } support ? support.CurrentLimit : null;
        Icon glyph = BatteryTrayIconRenderer.Render(snapshot.Percent ?? 0, limit, 32, lightTaskbar: TaskbarIsLight());
        Icon? old = _trayGlyph;
        _trayGlyph = glyph;
        _trayIcon.Icon = glyph;
        old?.Dispose();
        string quick = !snapshot.Present || snapshot.Percent is not int percent
            ? Strings.Get("AppName")
            : string.Format(Strings.Get(snapshot.IsCharging ? "TrayTipCharging" : "TrayTipLevel"), percent);
        _trayIcon.ToolTipText = quick;
        _ = UpdateTooltipAsync(snapshot);
    }

    // Level plus the status sentence; the sentence reads the firmware state, so it is built off the UI thread.
    private async Task UpdateTooltipAsync(BatterySnapshot snapshot)
    {
        if (snapshot.Percent is not int percent || !snapshot.Present || AppServices.Support is null) return;
        try
        {
            bool busy = AppServices.Charge?.IsBusy == true;
            ChargeStatusView view = await Task.Run(() => ChargeStatusView.Build(null, busy));
            if (_exiting || _trayIcon is null) return;
            string tip = string.Format(Strings.Get("TrayTipSentence"), percent, view.Sentence);
            _trayIcon.ToolTipText = tip.Length <= 127 ? tip : tip[..126] + "…"; // the tray tooltip holds 127 characters
        }
        catch (Exception) { }
    }

    // The taskbar and tray follow SystemUsesLightTheme; the glyph ink has to contrast with it. Read only.
    private static bool TaskbarIsLight()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch (Exception) { return false; }
    }

    public void ShowMainWindow() => _mainWindow?.ShowAndActivate();

    internal bool IsExiting => _exiting;

    // A second launch lands here on the first instance's UI thread via the instance redirection.
    public void OnRedirectedActivation(AppActivationArguments args)
    {
        string? page = AppToasts.PageFromActivation(args);
        _mainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            if (page is null) ShowMainWindow();
            else ShowPage(page);
        });
    }

    // Runs once when the user quits: puts the steady-state charge limit back if a top-up had it off (bounded in time).
    private void OnAppExiting()
    {
        AppServices.OnExit();
    }

    public void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        OnAppExiting();
        _trayIcon?.Dispose();
        _trayGlyph?.Dispose();
        _flyout?.Close();
        _mainWindow?.Close();
        Exit();
    }
}

