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
        Known(key, element.ActualTheme) ?? Find(Application.Current.Resources, key, element.ActualTheme) as Brush ?? fallback;

    // The control library's theme dictionaries are not reachable from code, so the generic lookup can still return the
    // application theme's brush. The neutral text and stroke colours drawn in code are pinned here per theme.
    private static Brush? Known(string key, ElementTheme theme)
    {
        bool light = theme == ElementTheme.Light;
        Windows.UI.Color? color = key switch
        {
            "TextFillColorPrimaryBrush" => light ? Windows.UI.Color.FromArgb(0xE4, 0, 0, 0) : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            "TextFillColorSecondaryBrush" => light ? Windows.UI.Color.FromArgb(0x9E, 0, 0, 0) : Windows.UI.Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF),
            "ControlStrongStrokeColorDefaultBrush" => light ? Windows.UI.Color.FromArgb(0x72, 0, 0, 0) : Windows.UI.Color.FromArgb(0x8B, 0xFF, 0xFF, 0xFF),
            "ControlStrokeColorDefaultBrush" => light ? Windows.UI.Color.FromArgb(0x0F, 0, 0, 0) : Windows.UI.Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF),
            _ => null,
        };
        return color is { } c ? new SolidColorBrush(c) : null;
    }

    public static Brush Brush(FrameworkElement element, string key) =>
        Known(key, element.ActualTheme) ?? Find(Application.Current.Resources, key, element.ActualTheme) as Brush
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
