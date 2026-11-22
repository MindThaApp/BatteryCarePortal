namespace Portal.Core.Theming;

public enum ThemeChoice { System = 0, Light = 1, Dark = 2 }

public enum AccentChoice { Windows = 0, Blue = 1, Teal = 2, Green = 3, Purple = 4, Orange = 5 }

public enum MaterialChoice { Mica = 0, Acrylic = 1, Solid = 2 }

public enum CornerChoice { Rounded = 0, Square = 1, ExtraRound = 2 }

public enum DensityChoice { Standard = 0, Compact = 1 }

public enum MotionChoice { FollowWindows = 0, Reduced = 1, Off = 2 }

public enum TextSizeChoice { Standard = 0, Larger = 1, Largest = 2 }

public static class TextSize
{
    /// <summary>The in-app step on top of Windows text scaling: 100, 115 or 130 percent.</summary>
    public static double Factor(TextSizeChoice choice) => choice switch
    {
        TextSizeChoice.Larger => 1.15,
        TextSizeChoice.Largest => 1.30,
        _ => 1.0,
    };

    public static double Scale(double fontSize, TextSizeChoice choice) => Math.Round(fontSize * Factor(choice), 1);
}

/// <summary>
/// How the app looks. The defaults are the design guide's: system theme, Windows accent, Mica, rounded corners,
/// standard density, motion follows Windows. A value this build does not know falls back to its default.
/// </summary>
public sealed class AppearanceSettings
{
    public ThemeChoice Theme { get; set; }
    public AccentChoice Accent { get; set; }
    public MaterialChoice Material { get; set; }
    public CornerChoice Corners { get; set; }
    public DensityChoice Density { get; set; }
    public MotionChoice Motion { get; set; }
    public TextSizeChoice TextSize { get; set; }

    public bool IsDefault =>
        Theme == ThemeChoice.System && Accent == AccentChoice.Windows && Material == MaterialChoice.Mica &&
        Corners == CornerChoice.Rounded && Density == DensityChoice.Standard && Motion == MotionChoice.FollowWindows &&
        TextSize == TextSizeChoice.Standard;

    public void Reset()
    {
        Theme = ThemeChoice.System;
        Accent = AccentChoice.Windows;
        Material = MaterialChoice.Mica;
        Corners = CornerChoice.Rounded;
        Density = DensityChoice.Standard;
        Motion = MotionChoice.FollowWindows;
        TextSize = TextSizeChoice.Standard;
    }

    public AppearanceSettings Normalize()
    {
        if (!Enum.IsDefined(Theme)) Theme = ThemeChoice.System;
        if (!Enum.IsDefined(Accent)) Accent = AccentChoice.Windows;
        if (!Enum.IsDefined(Material)) Material = MaterialChoice.Mica;
        if (!Enum.IsDefined(Corners)) Corners = CornerChoice.Rounded;
        if (!Enum.IsDefined(Density)) Density = DensityChoice.Standard;
        if (!Enum.IsDefined(Motion)) Motion = MotionChoice.FollowWindows;
        if (!Enum.IsDefined(TextSize)) TextSize = TextSizeChoice.Standard;
        return this;
    }
}

public static class AccentPalette
{
    private const double MinTextRatio = ColorMath.AaTextRatio;

    /// <summary>The preset's base colour, or null for the Windows accent.</summary>
    public static Rgb? Base(AccentChoice choice) => choice switch
    {
        AccentChoice.Blue => new Rgb(0x00, 0x78, 0xD4),
        AccentChoice.Teal => new Rgb(0x00, 0x99, 0xBC),
        AccentChoice.Green => new Rgb(0x10, 0x89, 0x3E),
        AccentChoice.Purple => new Rgb(0x88, 0x17, 0x98),
        AccentChoice.Orange => new Rgb(0xF7, 0x63, 0x0C),
        _ => null,
    };

    /// <summary>The colour to use for accent fills and text: darkened in the light theme until it reads on white, as is, in the dark theme lightened until it reads on the dark window.</summary>
    public static Rgb? Resolve(AccentChoice choice, bool darkTheme)
    {
        if (Base(choice) is not Rgb color) return null;
        return darkTheme
            ? ColorMath.EnsureContrast(color, new Rgb(0x20, 0x20, 0x20), MinTextRatio)
            : ColorMath.EnsureContrast(color, Rgb.White, MinTextRatio);
    }
}
