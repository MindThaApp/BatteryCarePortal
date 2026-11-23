using Microsoft.UI.Xaml;

namespace BatteryCarePortal;

internal static class AppIcon
{
    public static void Apply(Window window)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "batterycareportal.ico");
            if (File.Exists(path)) window.AppWindow.SetIcon(path);
        }
        catch (Exception) { }
    }
}
