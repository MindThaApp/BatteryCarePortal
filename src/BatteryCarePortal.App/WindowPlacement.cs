using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace BatteryCarePortal;

internal static class WindowPlacement
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>Sizes the window to the given DIP size (capped to the work area) and centres it on the owner's monitor.</summary>
    public static void CenterOn(Window window, Window owner, int widthDip, int heightDip, int marginDip = 0)
    {
        double scale = 1;
        try
        {
            uint dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(owner));
            if (dpi > 0) scale = dpi / 96.0;
        }
        catch (Exception) { }
        RectInt32 work = (TestMode.Quiet ? TestMode.SecondaryWorkArea() : null)
            ?? DisplayArea.GetFromWindowId(owner.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int width = Math.Min((int)Math.Round(widthDip * scale), work.Width);
        int height = Math.Min((int)Math.Round(heightDip * scale), work.Height - (int)Math.Round(marginDip * scale));
        window.AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
    }
}
