using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace BatteryCarePortal;

/// <summary>
/// The welcome flow in its own window (about 720 x 620 DIP, never larger than the work area, centred on the main window's
/// monitor). The main window stays usable. The first-run flag is stored only after this window has closed.
/// </summary>
internal sealed class WelcomeWindow : Window
{
    private const int WidthDip = 720;
    private const int HeightDip = 620;
    private readonly WelcomePanel _panel = new();

    public WelcomeWindow(Window owner)
    {
        Title = Strings.Get("FirstRunWindowTitle");
        Content = _panel;
        AppAppearance.AttachExtra(this, _panel);
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsMaximizable = false;
        WindowPlacement.CenterOn(this, owner, WidthDip, HeightDip);
        _panel.Finished += Close;
        Closed += (_, _) => StoreFlag();
        _ = _panel.StartAsync();
    }

    private static void StoreFlag()
    {
        try { AppServices.Settings.Update(s => s.FirstRunCompletedUtc = DateTimeOffset.UtcNow); }
        catch (Exception) { }
    }
}
