using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Portal.Core.Power;


namespace Portal.Core.UI.Controls;

/// <summary>Colours and names for the power categories, shared by the donut, the legend, the tree and the flow bands.</summary>
internal static class PowerPalette
{
    /// <summary>Theme brush key and opacity for a category. Only theme brushes are used, so every theme and high contrast work.</summary>
    public static (string Key, double Opacity) For(PowerCategory category) => category switch
    {
        PowerCategory.Cpu => ("AccentBrush", 1.0),
        PowerCategory.Gpu => ("StatusWarnBrush", 1.0),
        PowerCategory.Memory => ("StatusOkBrush", 1.0),
        PowerCategory.Storage => ("AccentBrush", 0.55),
        PowerCategory.Fans => ("StatusOkBrush", 0.55),
        PowerCategory.Motherboard => ("StatusWarnBrush", 0.55),
        PowerCategory.Peripherals => ("TextSecondaryBrush", 1.0),
        PowerCategory.PsuLoss => ("TextTertiaryBrush", 1.0),
        PowerCategory.Unaccounted => ("TextTertiaryBrush", 0.5),
        _ => ("AccentBrush", 1.0),
    };

    public static Brush Brush(FrameworkElement scope, PowerCategory category) => ThemeBrushes.Get(scope, For(category).Key);

    /// <summary>The words for a category in the user's language.</summary>
    public static string CategoryName(PowerCategory category) => Localization.Get("PowerCategory" + category);

    /// <summary>Title of a node: a known part is translated, anything else (a drive or fan name) is shown as the model gives it.</summary>
    public static string NodeTitle(PowerNode node, int depth)
    {
        if (depth == 0 && node.Category != PowerCategory.Total) return CategoryName(node.Category);
        string key = node.Id switch
        {
            "cpu.cores" => "PowerPartCores",
            "cpu.soc" => "PowerPartSoc",
            "cpu.memctrl" => "PowerPartMemoryController",
            "cpu.graphics" => "PowerPartIntegratedGraphics",
            "cpu.other" => "PowerPartOtherCpu",
            "usb.default" => "PowerPartUsbDevices",
            "board.chipset" => "PowerPartChipset",
            "board.lan" => "PowerPartWiredNetwork",
            "board.wifi" => "PowerPartWifi",
            "board.audio" => "PowerPartAudio",
            "pc" => "PowerPartWholePc",
            _ when node.Id.EndsWith(".core", StringComparison.Ordinal) => "PowerPartCore",
            _ when node.Id.EndsWith(".soc", StringComparison.Ordinal) => "PowerPartSoc",
            _ when node.Id.EndsWith(".memory", StringComparison.Ordinal) => "PowerPartGpuMemory",
            _ when node.Id.EndsWith(".other", StringComparison.Ordinal) => "PowerPartOtherGpu",
            _ => string.Empty,
        };
        return key.Length > 0 && Localization.TryGet(key) is { } text ? text : node.Name;
    }

    public static string QualityName(PowerQuality quality) => Localization.Get("PowerQuality" + quality);

    public static string QualityTipKey(PowerQuality quality) => quality switch
    {
        PowerQuality.Measured => "TipPowerQualityMeasured",
        PowerQuality.Estimated => "TipPowerQualityEstimated",
        PowerQuality.Calibrated => "TipPowerQualityCalibrated",
        _ => "TipPowerQualityUnavailable",
    };

    public static string BrushKeyForQuality(PowerQuality quality) => quality switch
    {
        PowerQuality.Measured => "StatusOkBrush",
        PowerQuality.Calibrated => "AccentTextBrush",
        _ => "TextSecondaryBrush",
    };
}
