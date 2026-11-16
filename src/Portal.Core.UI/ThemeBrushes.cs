using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Portal.Core.UI;

/// <summary>
/// Theme brushes for elements built in code. Application.Current.Resources answers for the app's theme, which differs
/// from the window's when the user picked Light or Dark in the app; this looks the key up in the theme dictionary that
/// matches the element's actual theme (and high contrast).
/// </summary>
internal static class ThemeBrushes
{
    private static readonly Microsoft.UI.Xaml.Media.Brush Fallback = new SolidColorBrush(Microsoft.UI.Colors.Gray);

    /// <summary>Looks the key up for an explicit theme, for code that has just changed a window's theme.</summary>
    public static Brush Get(ElementTheme theme, string key) => Get(null, key, theme);

    public static Brush Get(FrameworkElement? scope, string key) => Get(scope, key, scope?.ActualTheme ?? ElementTheme.Default);

    private static Brush Get(FrameworkElement? scope, string key, ElementTheme elementTheme)
    {
        string[] themes = HighContrast() ? ["HighContrast"]
            : elementTheme switch
            {
                ElementTheme.Light => ["Light"],
                ElementTheme.Dark => ["Dark", "Default"],
                _ => Application.Current.RequestedTheme == ApplicationTheme.Light ? ["Light"] : ["Dark", "Default"],
            };
        foreach (string theme in themes)
            if (Find(Application.Current.Resources, theme, key, 0) is Brush b) return b;
        return Application.Current.Resources.TryGetValue(key, out object? v) && v is Brush any ? any : Fallback;
    }

    private static bool HighContrast()
    {
        try { return new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast; }
        catch { return false; }
    }

    private static object? Find(ResourceDictionary rd, string theme, string key, int depth)
    {
        if (depth > 8) return null;
        if (rd.ThemeDictionaries.TryGetValue(theme, out object? td) && td is ResourceDictionary t && t.TryGetValue(key, out object? v)) return v;
        for (int i = rd.MergedDictionaries.Count - 1; i >= 0; i--)
            if (Find(rd.MergedDictionaries[i], theme, key, depth + 1) is object found) return found;
        return null;
    }
}
