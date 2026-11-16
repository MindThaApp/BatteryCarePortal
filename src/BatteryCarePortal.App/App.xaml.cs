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
        AppServices.Start();

        // A login start (StartupTask) lives in the tray; any other launch shows the window.
        bool startHidden = AppInstance.GetCurrent().GetActivatedEventArgs().Kind == ExtendedActivationKind.StartupTask;
        if (!startHidden) ShowMainWindow();
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

        _trayGlyph = BatteryTrayIconRenderer.Render(0, null, 32, lightTaskbar: false);
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
        };
        _trayIcon.ForceCreate();
    }

    // Level from the battery reader; the tick is the read-only charge limit, drawn only when the device has one set.
    private void UpdateTray()
    {
        if (_exiting || _trayIcon is null) return;
        BatterySnapshot snapshot = AppServices.Battery.Current;
        int? limit = AppServices.Support is { ChargeLimitAvailable: true } support ? support.CurrentLimit : null;
        Icon glyph = BatteryTrayIconRenderer.Render(snapshot.Percent ?? 0, limit, 32, lightTaskbar: false);
        Icon? old = _trayGlyph;
        _trayGlyph = glyph;
        _trayIcon.Icon = glyph;
        old?.Dispose();
        _trayIcon.ToolTipText = !snapshot.Present || snapshot.Percent is not int percent
            ? Strings.Get("AppName")
            : string.Format(Strings.Get(snapshot.IsCharging ? "TrayTipCharging" : "TrayTipLevel"), percent);
    }

    public void ShowMainWindow() => _mainWindow?.ShowAndActivate();

    internal bool IsExiting => _exiting;

    // A second launch lands here on the first instance's UI thread via the instance redirection.
    public void OnRedirectedActivation(AppActivationArguments args)
    {
        _mainWindow?.DispatcherQueue.TryEnqueue(ShowMainWindow);
    }

    // Runs once when the user quits: the later step that restores the charge limit hooks in here.
    private void OnAppExiting()
    {
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

