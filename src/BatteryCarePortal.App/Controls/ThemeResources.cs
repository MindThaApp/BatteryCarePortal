using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace BatteryCarePortal.Controls;

/// <summary>
/// Looks brushes up for an element's own theme. Application.Current.Resources resolves for the application theme, which
/// differs from the element's when its window or page sets a RequestedTheme.
/// </summary>
internal static class ThemeResources
{
    public static Brush Brush(FrameworkElement element, string key, Brush fallback) =>
        Find(Application.Current.Resources, key, element.ActualTheme) as Brush ?? fallback;

    public static Brush Brush(FrameworkElement element, string key) =>
        Find(Application.Current.Resources, key, element.ActualTheme) as Brush
        ?? (Brush)Application.Current.Resources[key];

    private static object? Find(ResourceDictionary dictionary, string key, ElementTheme theme)
    {
        string name = theme == ElementTheme.Light ? "Light" : "Dark";
        if (dictionary.ThemeDictionaries.TryGetValue(name, out object? themed) && themed is ResourceDictionary t
            && t.TryGetValue(key, out object? value))
            return value;
        if (theme != ElementTheme.Light && dictionary.ThemeDictionaries.TryGetValue("Default", out object? def) && def is ResourceDictionary d
            && d.TryGetValue(key, out object? dv))
            return dv;
        if (dictionary.TryGetValue(key, out object? plain)) return plain;
        foreach (ResourceDictionary merged in dictionary.MergedDictionaries)
            if (Find(merged, key, theme) is { } found) return found;
        return null;
    }
}
