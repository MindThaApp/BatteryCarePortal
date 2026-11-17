using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace BatteryCarePortal;

/// <summary>The tray flyout: a small borderless window pinned to the bottom-right of the work area. Holds the flyout panel; it is sized to the panel so larger text is not clipped.</summary>
public sealed class TrayFlyoutWindow : Window
{
    private const int Width = 360;
    private const int MinHeight = 200;
    private DateTime _hiddenAt = DateTime.MinValue;
    private readonly TrayFlyoutPanel _panel = new() { Width = Width };

    public TrayFlyoutWindow()
    {
        Content = _panel;
        _panel.ContentChanged += () => { if (_panel.IsShown) Fit(); };
        _panel.OpenRequested += () =>
        {
            AppWindow.Hide();
            App.Instance?.ShowMainWindow();
        };
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
                _panel.IsShown = false;
                _hiddenAt = DateTime.UtcNow;
            }
        };
    }

    public void Toggle()
    {
        // The tray click that deactivated the flyout also lands here; do not reopen it straight away.
        if (AppWindow.IsVisible || (DateTime.UtcNow - _hiddenAt).TotalMilliseconds < 300) { AppWindow.Hide(); _panel.IsShown = false; return; }
        _panel.IsShown = true;
        _panel.Refresh();
        Fit();
        AppWindow.Show();
        Activate();
    }

    // Pinned to the bottom-right of the work area; as tall as the content needs (up to the work area), so 200% text still fits.
    private void Fit()
    {
        RectInt32 work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double scale = Content.XamlRoot?.RasterizationScale ?? 1.0;
        _panel.Measure(new Windows.Foundation.Size(Width, double.PositiveInfinity));
        double heightDip = Math.Max(MinHeight, _panel.DesiredSize.Height);
        int w = (int)(Width * scale), h = (int)Math.Min(heightDip * scale, work.Height - 24);
        AppWindow.MoveAndResize(new RectInt32(work.X + work.Width - w - 12, work.Y + work.Height - h - 12, w, h));
    }
}
