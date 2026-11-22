using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Portal.Core.Battery;

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

        // Screenshot runs only: simulated battery plus a flag file; reads, never writes.
        WelcomeCaptureOptions? capture = WelcomeCapture.Read(AppServices.StateFolder, BatteryEnvironment.IsFakeBatteryEnabled);
        if (capture is not null)
        {
            if (capture.Theme is not null) _panel.RequestedTheme = capture.Theme == "Dark" ? ElementTheme.Dark : ElementTheme.Light;
            if (capture.TextScale > 1.0) { AppAppearance.OverrideTextFactor(capture.TextScale); _panel.CaptureTextFactor = capture.TextScale; }
        }
        _ = _panel.StartAsync(capture is null ? 0 : capture.Step - 1);
    }

    private static void StoreFlag()
    {
        try { AppServices.Settings.Update(s => s.FirstRunCompletedUtc = DateTimeOffset.UtcNow); }
        catch (Exception) { }
    }
}
