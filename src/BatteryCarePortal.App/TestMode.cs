using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Portal.Core.Tray;
using Windows.Graphics;

namespace BatteryCarePortal;

/// <summary>
/// Test-only quiet launch, switched on by a test-quiet.flag file in the app's local folder (only ever read).
/// Windows are shown without activation, never take focus and only ever appear on a non-primary display.
/// </summary>
internal static class TestMode
{
    public const string FlagFileName = "test-quiet.flag";

    private static readonly Lazy<bool> QuietFlag = new(() =>
    {
        try { return File.Exists(Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, FlagFileName)); }
        catch (Exception) { return false; }
    });

    public static bool Quiet => QuietFlag.Value;

    /// <summary>The work area of the first non-primary display, or null when there is none.</summary>
    public static RectInt32? SecondaryWorkArea()
    {
        IReadOnlyList<DisplayArea> areas = DisplayArea.FindAll();
        var outer = new List<PxRect>(areas.Count);
        foreach (DisplayArea a in areas)
        {
            RectInt32 o = a.OuterBounds;
            outer.Add(new PxRect(o.X, o.Y, o.X + o.Width, o.Y + o.Height));
        }
        int index = QuietDisplay.PickNonPrimary(outer);
        return index < 0 ? null : areas[index].WorkArea;
    }

    /// <summary>
    /// Shows the window the normal way, or in quiet mode on a secondary display without activating it.
    /// With a single display a quiet window stays hidden.
    /// </summary>
    public static void Present(Window window)
    {
        if (!Quiet) { window.Activate(); return; }
        if (!MoveToSecondary(window)) return;
        window.AppWindow.Show(false);
    }

    /// <summary>Quiet mode only: centres the window, keeping its size, on the secondary display. False when there is none.</summary>
    public static bool MoveToSecondary(Window window, int? width = null, int? height = null)
    {
        if (SecondaryWorkArea() is not RectInt32 work)
        {
            System.Diagnostics.Debug.WriteLine("test-quiet: only one display, window stays hidden.");
            return false;
        }
        int w = Math.Min(width ?? window.AppWindow.Size.Width, work.Width);
        int h = Math.Min(height ?? window.AppWindow.Size.Height, work.Height);
        var (x, y) = QuietDisplay.CenterIn(new PxRect(work.X, work.Y, work.X + work.Width, work.Y + work.Height), w, h);
        window.AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
        return true;
    }
}
