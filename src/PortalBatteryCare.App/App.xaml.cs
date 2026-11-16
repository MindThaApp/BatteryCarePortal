using System.Drawing;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;

namespace PortalBatteryCare;

public partial class App : Application
{
    // Fake values until the battery layer is wired in.
    private const int PlaceholderLevel = 60;
    private const int PlaceholderLimit = 80;

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

        _trayGlyph = BatteryTrayIconRenderer.Render(PlaceholderLevel, PlaceholderLimit, 32, lightTaskbar: false);
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
