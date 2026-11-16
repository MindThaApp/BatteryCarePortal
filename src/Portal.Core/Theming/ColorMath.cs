using System.Globalization;

namespace Portal.Core.Theming;

/// <summary>An opaque sRGB colour.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0, 0, 0);
    public static readonly Rgb White = new(255, 255, 255);

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>Parses "#RRGGBB", "RRGGBB" or "#AARRGGBB" (the alpha is dropped).</summary>
    public static bool TryParse(string? text, out Rgb color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string hex = text.Trim().TrimStart('#');
        if (hex.Length == 8) hex = hex[2..];
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value)) return false;
        color = new Rgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    public static Rgb ParseOr(string? text, Rgb fallback) => TryParse(text, out Rgb c) ? c : fallback;
}

/// <summary>A colour with an alpha channel.</summary>
public readonly record struct Argb(byte A, byte R, byte G, byte B)
{
    public static Argb From(Rgb c, byte alpha = 255) => new(alpha, c.R, c.G, c.B);
    public static Argb From(Rgb c, double alpha) => new(ColorMath.ToByte(alpha * 255), c.R, c.G, c.B);
    public Rgb Rgb => new(R, G, B);
}

/// <summary>Pure colour maths: WCAG contrast, HSL conversion, mixing and contrast-driven adjustment.</summary>
public static class ColorMath
{
    public const double AaTextRatio = 4.5;
    public const double AaLargeRatio = 3.0;

    public static byte ToByte(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    private static double Linear(byte channel)
    {
        double c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary>WCAG relative luminance, 0 (black) to 1 (white).</summary>
    public static double RelativeLuminance(Rgb c) => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

    /// <summary>WCAG contrast ratio, 1 to 21, symmetric in its arguments.</summary>
    public static double ContrastRatio(Rgb a, Rgb b)
    {
        double la = RelativeLuminance(a), lb = RelativeLuminance(b);
        double hi = Math.Max(la, lb), lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>Converts to hue (0-360), saturation (0-1) and lightness (0-1).</summary>
    public static (double H, double S, double L) ToHsl(Rgb c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2;
        double d = max - min;
        if (d < 1e-9) return (0, 0, l);
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        return (h * 60, s, l);
    }

    public static Rgb FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        if (s < 1e-9) { byte v = ToByte(l * 255); return new Rgb(v, v, v); }
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        double hk = h / 360;
        return new Rgb(ToByte(Channel(p, q, hk + 1.0 / 3) * 255), ToByte(Channel(p, q, hk) * 255), ToByte(Channel(p, q, hk - 1.0 / 3) * 255));
    }

    private static double Channel(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 0.5) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    public static Rgb WithLightness(Rgb c, double lightness)
    {
        var (h, s, _) = ToHsl(c);
        return FromHsl(h, s, lightness);
    }

    /// <summary>Moves lightness by a signed amount (positive lightens).</summary>
    public static Rgb ShiftLightness(Rgb c, double delta)
    {
        var (h, s, l) = ToHsl(c);
        return FromHsl(h, s, l + delta);
    }

    /// <summary>Linear mix in sRGB space: t = 0 gives a, t = 1 gives b.</summary>
    public static Rgb Mix(Rgb a, Rgb b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new Rgb(ToByte(a.R + (b.R - a.R) * t), ToByte(a.G + (b.G - a.G) * t), ToByte(a.B + (b.B - a.B) * t));
    }

    /// <summary>The opaque colour a translucent colour shows when laid over an opaque background.</summary>
    public static Rgb Composite(Argb top, Rgb background)
    {
        double a = top.A / 255.0;
        return new Rgb(ToByte(top.R * a + background.R * (1 - a)), ToByte(top.G * a + background.G * (1 - a)), ToByte(top.B * a + background.B * (1 - a)));
    }

    /// <summary>
    /// Returns the colour itself when it already reaches the ratio against the background; otherwise lightens it (on a dark
    /// background) or darkens it (on a light one) in small steps until it does. Falls back to white or black if the hue can't get there.
    /// </summary>
    public static Rgb EnsureContrast(Rgb foreground, Rgb background, double minRatio)
    {
        if (ContrastRatio(foreground, background) >= minRatio) return foreground;
        bool lighten = RelativeLuminance(background) < 0.4;
        var (h, s, l) = ToHsl(foreground);
        for (int i = 0; i < 100; i++)
        {
            l = Math.Clamp(l + (lighten ? 0.01 : -0.01), 0, 1);
            Rgb candidate = FromHsl(h, s, l);
            if (ContrastRatio(candidate, background) >= minRatio) return candidate;
            if (l <= 0 || l >= 1) break;
        }
        Rgb extreme = lighten ? Rgb.White : Rgb.Black;
        return ContrastRatio(extreme, background) >= minRatio ? extreme : (ContrastRatio(Rgb.White, background) >= ContrastRatio(Rgb.Black, background) ? Rgb.White : Rgb.Black);
    }

    /// <summary>Black or white, whichever reads better on a filled accent surface.</summary>
    public static Rgb ChooseOnAccent(Rgb accent) =>
        ContrastRatio(Rgb.White, accent) >= ContrastRatio(Rgb.Black, accent) ? Rgb.White : Rgb.Black;
}
