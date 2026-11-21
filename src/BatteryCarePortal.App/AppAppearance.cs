using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Portal.Core.Theming;
using Portal.Core.UI;
using Windows.UI;

namespace BatteryCarePortal;

/// <summary>
/// Applies the saved appearance (theme, accent, material, corners, density, motion) to the main window and the app resources.
/// Everything is applied live; nothing here writes a file. High contrast keeps the system colours and a solid window.
/// </summary>
internal static class AppAppearance
{
    private const string CompactDensity = "ms-appx:///Microsoft.UI.Xaml/DensityStyles/Compact.xaml";

    private static readonly string[] AccentKeys =
    {
        "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
        "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3", "AccentFillColorDefault",
        "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush",
        "AccentFillColorSelectedTextBackgroundBrush", "SystemControlHighlightAccentBrush",
        "AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush", "AccentTextFillColorTertiaryBrush",
        "TextOnAccentFillColorPrimaryBrush", "TextOnAccentFillColorSelectedTextBrush",
    };

    private static readonly string[] CornerKeys = { "ControlCornerRadius", "OverlayCornerRadius", "BcpCardRadius" };

    private static Window? _window;
    private static FrameworkElement? _root;
    private static AppearanceSettings _current = new();
    private static ResourceDictionary? _density;
    private static bool _flipping;

    /// <summary>Remembers the window and applies the settings before it is shown.</summary>
    public static void Attach(Window window, FrameworkElement root, AppearanceSettings settings)
    {
        _window = window;
        _root = root;
        root.ActualThemeChanged += (_, _) => { if (!_flipping && _current.Accent != AccentChoice.Windows) Apply(_current); };
        Apply(settings, refresh: false);
    }

    public static void Apply(AppearanceSettings settings) => Apply(settings, refresh: true);

    private static void Apply(AppearanceSettings settings, bool refresh)
    {
        _current = settings;
        if (_window is null || _root is null) return;
        bool highContrast = IsHighContrast();

        _root.RequestedTheme = settings.Theme switch
        {
            ThemeChoice.Light => ElementTheme.Light,
            ThemeChoice.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        ApplyAccent(settings.Accent, highContrast);
        ApplyCorners(settings.Corners);
        ApplyDensity(settings.Density);
        ApplyMaterial(highContrast ? MaterialChoice.Solid : settings.Material);
        UiMotion.Mode = settings.Motion switch
        {
            MotionChoice.Reduced => UiMotionMode.Reduced,
            MotionChoice.Off => UiMotionMode.Off,
            _ => UiMotionMode.FollowWindows,
        };

        if (refresh) ReResolveThemeResources();
    }

    private static bool IsDark()
    {
        if (_root is null) return false;
        if (_current.Theme != ThemeChoice.System) return _current.Theme == ThemeChoice.Dark;
        return _root.ActualTheme switch
        {
            ElementTheme.Dark => true,
            ElementTheme.Light => false,
            _ => Application.Current.RequestedTheme == ApplicationTheme.Dark,
        };
    }

    private static bool IsHighContrast()
    {
        try { return new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast; }
        catch { return false; }
    }

    private static void ApplyAccent(AccentChoice choice, bool highContrast)
    {
        ResourceDictionary resources = Application.Current.Resources;
        foreach (string key in AccentKeys) resources.Remove(key);
        if (highContrast || AccentPalette.Resolve(choice, IsDark()) is not Rgb rgb) return;

        Color accent = Color.FromArgb(255, rgb.R, rgb.G, rgb.B);
        Rgb on = ColorMath.ChooseOnAccent(rgb);
        Color onAccent = Color.FromArgb(255, on.R, on.G, on.B);
        Color Tint(double amount) { Rgb t = ColorMath.Mix(rgb, amount > 0 ? Rgb.White : Rgb.Black, Math.Abs(amount)); return Color.FromArgb(255, t.R, t.G, t.B); }

        resources["SystemAccentColor"] = accent;
        resources["SystemAccentColorLight1"] = Tint(0.15);
        resources["SystemAccentColorLight2"] = Tint(0.30);
        resources["SystemAccentColorLight3"] = Tint(0.45);
        resources["SystemAccentColorDark1"] = Tint(-0.15);
        resources["SystemAccentColorDark2"] = Tint(-0.30);
        resources["SystemAccentColorDark3"] = Tint(-0.45);
        resources["AccentFillColorDefault"] = accent;
        resources["AccentFillColorDefaultBrush"] = new SolidColorBrush(accent);
        resources["AccentFillColorSecondaryBrush"] = new SolidColorBrush(accent) { Opacity = 0.9 };
        resources["AccentFillColorTertiaryBrush"] = new SolidColorBrush(accent) { Opacity = 0.8 };
        resources["AccentFillColorSelectedTextBackgroundBrush"] = new SolidColorBrush(accent);
        resources["SystemControlHighlightAccentBrush"] = new SolidColorBrush(accent);
        resources["AccentTextFillColorPrimaryBrush"] = new SolidColorBrush(accent);
        resources["AccentTextFillColorSecondaryBrush"] = new SolidColorBrush(accent);
        resources["AccentTextFillColorTertiaryBrush"] = new SolidColorBrush(accent) { Opacity = 0.8 };
        resources["TextOnAccentFillColorPrimaryBrush"] = new SolidColorBrush(onAccent);
        resources["TextOnAccentFillColorSelectedTextBrush"] = new SolidColorBrush(onAccent);
    }

    private static void ApplyCorners(CornerChoice choice)
    {
        ResourceDictionary resources = Application.Current.Resources;
        foreach (string key in CornerKeys) resources.Remove(key);
        (double control, double overlay, double card) = choice switch
        {
            CornerChoice.Square => (0d, 0d, 0d),
            CornerChoice.ExtraRound => (8d, 12d, 16d),
            _ => (4d, 8d, 8d),
        };
        resources["ControlCornerRadius"] = new CornerRadius(control);
        resources["OverlayCornerRadius"] = new CornerRadius(overlay);
        resources["BcpCardRadius"] = new CornerRadius(card);
    }

    private static void ApplyDensity(DensityChoice choice)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        if (choice == DensityChoice.Compact)
        {
            if (_density is not null) return;
            try
            {
                _density = new ResourceDictionary { Source = new Uri(CompactDensity) };
                merged.Add(_density);
            }
            catch (Exception)
            {
                _density = null;
            }
        }
        else if (_density is not null)
        {
            merged.Remove(_density);
            _density = null;
        }
    }

    // Mica and acrylic need transparency effects; where the system has them off or the OS lacks them, the window stays solid.
    private static void ApplyMaterial(MaterialChoice choice)
    {
        if (_window is null) return;
        try
        {
            _window.SystemBackdrop = choice switch
            {
                MaterialChoice.Mica when MicaController.IsSupported() => new MicaBackdrop(),
                MaterialChoice.Acrylic when DesktopAcrylicController.IsSupported() => new DesktopAcrylicBackdrop(),
                _ => null,
            };
        }
        catch (Exception)
        {
            _window.SystemBackdrop = null;
        }
    }

    // Theme resources keep the value they resolved at load; flipping the root theme and back makes every element look them up again.
    private static void ReResolveThemeResources()
    {
        if (_root is null) return;
        _flipping = true;
        try
        {
            ElementTheme wanted = _root.RequestedTheme;
            _root.RequestedTheme = wanted == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            _root.RequestedTheme = wanted;
        }
        finally { _flipping = false; }
    }
}
