using System.Globalization;

namespace Portal.Core.Battery;

/// <summary>How much battery information the tray popup shows.</summary>
public enum TrayBatteryMode
{
    Off,
    Simple,
    Expanded,
}

/// <summary>
/// Number formatting for battery values. Every method returns an em dash for a missing value, so a field
/// the hardware does not report never shows a zero.
/// </summary>
public static class BatteryFormat
{
    public const string None = "—";

    private static CultureInfo Culture(CultureInfo? culture) => culture ?? CultureInfo.CurrentCulture;

    /// <summary>"57,000 mWh (57.0 Wh)".</summary>
    public static string Capacity(int? mwh, CultureInfo? culture = null)
    {
        if (mwh is not int v) return None;
        CultureInfo c = Culture(culture);
        return $"{v.ToString("N0", c)} mWh ({(v / 1000.0).ToString("0.0", c)} Wh)";
    }

    /// <summary>"57.0 Wh".</summary>
    public static string CapacityWh(int? mwh, CultureInfo? culture = null) =>
        mwh is int v ? $"{(v / 1000.0).ToString("0.0", Culture(culture))} Wh" : None;

    public static string Percent(double? value, CultureInfo? culture = null, string format = "0.#") =>
        value is double v ? v.ToString(format, Culture(culture)) + " %" : None;

    public static string Volts(int? mv, CultureInfo? culture = null) =>
        mv is int v ? (v / 1000.0).ToString("0.00", Culture(culture)) + " V" : None;

    /// <summary>Signed current: "+1,250 mA" while charging, "−750 mA" while discharging.</summary>
    public static string Milliamps(int? ma, CultureInfo? culture = null)
    {
        if (ma is not int v) return None;
        return Signed(v, v.ToString("N0", Culture(culture))) + " mA";
    }

    /// <summary>Signed power: "+15.0 W" charging, "−6.0 W" discharging, "0.0 W" idle.</summary>
    public static string Watts(double? watts, CultureInfo? culture = null)
    {
        if (watts is not double w) return None;
        string text = Math.Abs(w).ToString("0.0", Culture(culture));
        return Signed(w, text) + " W";
    }

    public static string Temperature(double? celsius, CultureInfo? culture = null) =>
        celsius is double t ? t.ToString("0.#", Culture(culture)) + " °C" : None;

    public static string Count(int? value, CultureInfo? culture = null) =>
        value is int v ? v.ToString("N0", Culture(culture)) : None;

    public static string Date(DateOnly? date, CultureInfo? culture = null) =>
        date is DateOnly d ? d.ToString("d", Culture(culture)) : None;

    public static string Text(string? text) => string.IsNullOrWhiteSpace(text) ? None : text.Trim();

    private static string Signed(double value, string magnitude) =>
        value > 0 ? "+" + magnitude.TrimStart('-') : value < 0 ? "−" + magnitude.TrimStart('-') : magnitude;

    /// <summary>Share of the design capacity (0 to 1) taken by a value, for drawing the capacity bar. Null when unknown.</summary>
    public static double? Fraction(int? value, int? design) =>
        value is int v && design is int d && d > 0 ? Math.Clamp(v / (double)d, 0, 1) : null;
}
