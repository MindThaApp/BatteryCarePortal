using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Portal.Core.Theming;
using Portal.Core.Tray;
using Windows.Graphics;

namespace BatteryCarePortal;

/// <summary>
/// The tray flyout: a borderless tool window that opens from the tray icon toward the screen interior on the icon's monitor.
/// It takes focus on open, closes on Esc and, 100 ms after losing focus, unless the user is editing the tiles.
/// </summary>
public sealed class TrayFlyoutWindow : Window
{
    private const int FocusLossGraceMs = 100;
    private const int ReopenGuardMs = 400;
    private readonly TrayFlyoutPanel _panel = new();
    private readonly DispatcherQueueTimer _grace;
    private readonly IntPtr _hwnd;
    private DateTime _lostFocusAt = DateTime.MinValue;
    private RectInt32 _placed;
    private bool _solid;
    private bool _roundedWindow = true;
    private TrayNative.Target _target;

    /// <summary>Identifies the tray icon so the pop-up can open next to it.</summary>
    public Guid TrayIconId { get; set; }
    public IntPtr TrayIconWindow { get; set; }

    public TrayFlyoutWindow()
    {
        Content = _panel;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _grace = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _grace.Interval = TimeSpan.FromMilliseconds(FocusLossGraceMs);
        _grace.IsRepeating = false;
        _grace.Tick += (_, _) => { if (!_panel.IsEditing) HideFlyout(); };

        _panel.ContentChanged += () => { if (_panel.IsShown) Fit(); };
        _panel.CloseRequested += HideFlyout;
        _panel.OpenRequested += () =>
        {
            HideFlyout();
            App.Instance?.ShowMainWindow();
        };

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(false, false);
        }
        AppWindow.IsShownInSwitchers = false;
        ApplyBackdrop();

        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                _lostFocusAt = DateTime.UtcNow;
                _grace.Start();
            }
            else _grace.Stop();
        };
    }

    // Acrylic, then Mica Base Alt, then a solid pane.
    private void ApplyBackdrop()
    {
        try
        {
            if (DesktopAcrylicController.IsSupported()) { SystemBackdrop = new DesktopAcrylicBackdrop(); _solid = false; return; }
            if (MicaController.IsSupported()) { SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt }; _solid = false; return; }
        }
        catch (Exception) { }
        SystemBackdrop = null;
        _solid = true;
    }

    private static bool IsHighContrast()
    {
        try { return new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast; }
        catch (Exception) { return false; }
    }

    public void ToggleLimit() => _panel.ToggleLimitFromMenu();

    public void Toggle()
    {
        if (AppWindow.IsVisible) { HideFlyout(); return; }
        // The tray click that took the focus away also lands here; do not reopen it straight away.
        if ((DateTime.UtcNow - _lostFocusAt).TotalMilliseconds < ReopenGuardMs) return;

        AppearanceSettings appearance;
        try { appearance = AppServices.Settings.Load().Appearance; }
        catch (Exception) { appearance = new AppearanceSettings(); }
        bool highContrast = IsHighContrast();
        _roundedWindow = appearance.Corners != CornerChoice.Square;
        TrayNative.SetWindowCorners(_hwnd, _roundedWindow);
        _panel.ApplyAppearance(appearance, solid: _solid || highContrast);
        if (highContrast) SystemBackdrop = null;
        else if (SystemBackdrop is null && !_solid) ApplyBackdrop();

        _panel.PrepareToShow();
        _panel.IsShown = true;
        _panel.Refresh();
        _panel.ApplyTextSize();
        _placed = default;
        Fit();
        AppWindow.Show();
        Activate();
        DispatcherQueue.TryEnqueue(_panel.FocusFirst);
    }

    private void HideFlyout()
    {
        _grace.Stop();
        AppWindow.Hide();
        _panel.IsShown = false;
    }

    // Size to the content (default 380 DIP, up to 560, scaled with text size), capped to the work area, then placed by the pure maths.
    private void Fit()
    {
        TrayNative.Target target = TrayNative.Locate(TrayIconId, TrayIconWindow);
        double scale = target.Scale;
        int gap = (int)Math.Round(8 * scale);
        double textScale = 1;
        try { textScale = Math.Max(1, new Windows.UI.ViewManagement.UISettings().TextScaleFactor); } catch (Exception) { }
        textScale *= TextSize.Factor(_panel.TextSizeStep);
        double roomWidth = (target.Work.Width - 2 * gap) / scale;
        double roomHeight = (target.Work.Height - 2 * gap) / scale;

        double widthDip = TrayPlacement.WidthDip(TrayPlacement.DefaultWidthDip * textScale, roomWidth);
        _panel.Width = widthDip;
        _panel.Measure(new Windows.Foundation.Size(widthDip, double.PositiveInfinity));
        double naturalDip = Math.Max(120, Math.Ceiling(_panel.DesiredSize.Height));
        double heightDip = TrayPlacement.HeightDip(naturalDip, roomHeight);
        if (naturalDip > roomHeight)
        {
            // Header and footer stay pinned; the tile area ends after the last whole row that fits.
            var m = _panel.MeasureTileRows();
            double pinned = _panel.DesiredSize.Height - m.ScrollHeight;
            heightDip = Math.Min(roomHeight, pinned + TrayPlacement.TileViewportDip(roomHeight - pinned, m.Rows, m.Gap, m.PadTop, m.PadBottom));
        }

        PxRect r = TrayPlacement.Place(target.Anchor, target.Work, (int)Math.Round(widthDip * scale), (int)Math.Round(heightDip * scale), gap);
        var next = new RectInt32(r.Left, r.Top, r.Width, r.Height);
        if (next.X == _placed.X && next.Y == _placed.Y && next.Width == _placed.Width && next.Height == _placed.Height) return;
        _placed = next;
        AppWindow.MoveAndResize(next);
    }
}
