using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace PortalBatteryCare;

/// <summary>The tray flyout: a small borderless window pinned to the bottom-right of the work area. Empty for now.</summary>
public sealed class TrayFlyoutWindow : Window
{
    private const int Width = 360;
    private const int Height = 420;
    private DateTime _hiddenAt = DateTime.MinValue;

    public TrayFlyoutWindow()
    {
        Content = new Grid();
        SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(true, false);
        }
        AppWindow.IsShownInSwitchers = false;
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                AppWindow.Hide();
                _hiddenAt = DateTime.UtcNow;
            }
        };
    }

    public void Toggle()
    {
        // The tray click that deactivated the flyout also lands here; do not reopen it straight away.
        if (AppWindow.IsVisible || (DateTime.UtcNow - _hiddenAt).TotalMilliseconds < 300) { AppWindow.Hide(); return; }
        RectInt32 work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double scale = Content.XamlRoot?.RasterizationScale ?? 1.0;
        int w = (int)(Width * scale), h = (int)(Height * scale);
        AppWindow.MoveAndResize(new RectInt32(work.X + work.Width - w - 12, work.Y + work.Height - h - 12, w, h));
        AppWindow.Show();
        Activate();
    }
}
