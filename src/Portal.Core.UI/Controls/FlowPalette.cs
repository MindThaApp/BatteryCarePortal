using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Portal.Core.Power;
using Portal.Core.Power.Dashboard;

using Windows.UI;

namespace Portal.Core.UI.Controls;

/// <summary>
/// Colours of the power flow: a muted, harmonious hue per source and per main part, the device in the user's accent, and
/// the frosted tile and ribbon strengths for dark and light. High contrast uses system colours only, without translucency.
/// </summary>
internal sealed record FlowPalette(bool Dark, bool HighContrast, Color Accent, Brush PrimaryText, Brush SecondaryText, Brush HcFill, Brush HcEdge)
{
    /// <summary>True when the battery is the source.</summary>
    public bool OnBattery { get; init; }

    public string Key => $"{Dark}|{HighContrast}|{Accent}|{OnBattery}";

    public double RibbonAlpha => Dark ? 0.50 : 0.42;
    /// <summary>Estimates are drawn paler by this factor.</summary>
    public double EstimateStrength => 0.78;

    /// <summary>Text written on the river itself: light on dark, dark on light, the plain text colour in high contrast.</summary>
    public Brush OnBandText => HighContrast ? PrimaryText
        : new SolidColorBrush(Dark ? Color.FromArgb(0xF0, 255, 255, 255) : Color.FromArgb(0xEB, 0x12, 0x17, 0x1F));
    public double EdgeAlpha => Dark ? 0.32 : 0.30;
    public double HaloAlpha => Dark ? 0.10 : 0.08;
    public double SheenAlpha => Dark ? 0.22 : 0.55;

    public Color RimTop => Dark ? Color.FromArgb(0x5C, 255, 255, 255) : Color.FromArgb(0xF2, 255, 255, 255);
    public Color RimMiddle => Dark ? Color.FromArgb(0x1A, 255, 255, 255) : Color.FromArgb(0x1A, 0, 0, 0);
    public Color RimBottom => Dark ? Color.FromArgb(0x24, 255, 255, 255) : Color.FromArgb(0x24, 0, 0, 0);

    public static FlowPalette Read(FrameworkElement scope)
    {
        bool hc;
        try { hc = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast; } catch { hc = false; }
        bool dark = scope.ActualTheme != ElementTheme.Light;
        Color accent = ThemeBrushes.Get(scope, "AccentBrush") is SolidColorBrush b ? b.Color : Color.FromArgb(255, 10, 132, 255);
        return new FlowPalette(dark, hc, accent, ThemeBrushes.Get(scope, "TextPrimaryBrush"), ThemeBrushes.Get(scope, "TextSecondaryBrush"),
            ThemeBrushes.Get(scope, "CardSurfaceBrush"), ThemeBrushes.Get(scope, "TextPrimaryBrush"));
    }

    /// <summary>The hue of a box: its source, its category, or the accent for the device. Parts are a shade of their category.</summary>
    public Color ColorFor(FlowBox b)
    {
        Color c = b.Role switch
        {
            FlowRole.Mains => Pick(0xE8B866, 0xC08326),
            FlowRole.Battery or FlowRole.Charging => Pick(0x7DCB92, 0x3A9A5B),
            FlowRole.Device => Dark ? Lift(Accent, 0.12) : Accent,
            FlowRole.Loss => Pick(0x9C9CA3, 0x8A8A92),
            _ => b.Node is { } n ? CategoryColor(n.Category) : Pick(0xA3ABB8, 0x7D8794),
        };
        if (b.Role == FlowRole.Part) c = Lift(c, 0.10 + 0.08 * (b.Index % 3));
        return c;
    }

    public Color GlyphColor(FlowBox b) => b.Role == FlowRole.Device ? (Dark ? Lift(Accent, 0.25) : Accent) : ColorFor(b);

    private Color CategoryColor(PowerCategory category) => category switch
    {
        PowerCategory.Cpu => Pick(0x7AA7F5, 0x3D74D6),
        PowerCategory.Gpu => Pick(0xB38FF2, 0x8559D3),
        PowerCategory.Memory => Pick(0x62C9BD, 0x1F9286),
        PowerCategory.Storage => Pick(0x68B6E2, 0x2A83B5),
        PowerCategory.Fans => Pick(0x91D4A3, 0x3F9E62),
        PowerCategory.Motherboard => Pick(0xEDA383, 0xC9673E),
        PowerCategory.Peripherals => Pick(0xE391C1, 0xBC4F8E),
        PowerCategory.Battery => Pick(0x7DCB92, 0x3A9A5B),
        PowerCategory.PsuLoss => Pick(0x9C9CA3, 0x8A8A92),
        _ => Pick(0xA9B1BD, 0x7A8491),
    };

    /// <summary>Top of a tile: the hue lightly washed over frosted glass, a touch brighter than the bottom.</summary>
    public Color TileTop(Color c) => Dark ? WithAlpha(Lift(c, 0.30), 0.30) : WithAlpha(Lift(c, 0.72), 0.80);

    public Color TileBottom(Color c) => Dark ? WithAlpha(c, 0.17) : WithAlpha(Lift(c, 0.60), 0.62);

    public static Color WithAlpha(Color c, double alpha) => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), c.R, c.G, c.B);

    public static Color Lift(Color c, double toWhite)
    {
        toWhite = Math.Clamp(toWhite, 0, 1);
        byte M(byte v) => (byte)Math.Round(v + (255 - v) * toWhite);
        return Color.FromArgb(c.A, M(c.R), M(c.G), M(c.B));
    }

    private Color Pick(uint dark, uint light)
    {
        uint v = Dark ? dark : light;
        return Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
}
