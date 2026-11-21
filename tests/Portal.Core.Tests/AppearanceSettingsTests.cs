using Portal.Core.Battery;
using Portal.Core.Theming;
using Xunit;

namespace Portal.Core.Tests;

public class AppearanceSettingsTests
{
    [Fact]
    public void Defaults_follow_the_design_guide()
    {
        var a = new BatterySettings().Normalize().Appearance;
        Assert.Equal(ThemeChoice.System, a.Theme);
        Assert.Equal(AccentChoice.Windows, a.Accent);
        Assert.Equal(MaterialChoice.Mica, a.Material);
        Assert.Equal(CornerChoice.Rounded, a.Corners);
        Assert.Equal(DensityChoice.Standard, a.Density);
        Assert.Equal(MotionChoice.FollowWindows, a.Motion);
        Assert.True(a.IsDefault);
    }

    [Fact]
    public void Unknown_values_fall_back_to_the_defaults()
    {
        var a = new AppearanceSettings
        {
            Theme = (ThemeChoice)99, Accent = (AccentChoice)(-1), Material = (MaterialChoice)7,
            Corners = (CornerChoice)42, Density = (DensityChoice)5, Motion = (MotionChoice)9,
        }.Normalize();
        Assert.True(a.IsDefault);
    }

    [Fact]
    public void A_settings_file_without_appearance_gets_the_defaults()
    {
        BatterySettings s = System.Text.Json.JsonSerializer.Deserialize<BatterySettings>("{\"Limit\":80,\"Appearance\":null}")!.Normalize();
        Assert.True(s.Appearance.IsDefault);
    }

    [Fact]
    public void Appearance_round_trips_through_the_store()
    {
        string folder = Path.Combine(Path.GetTempPath(), "bcp-appearance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new BatterySettingsStore(folder);
            store.Update(s =>
            {
                s.Appearance.Theme = ThemeChoice.Dark;
                s.Appearance.Accent = AccentChoice.Teal;
                s.Appearance.Material = MaterialChoice.Solid;
                s.Appearance.Corners = CornerChoice.ExtraRound;
                s.Appearance.Density = DensityChoice.Compact;
                s.Appearance.Motion = MotionChoice.Off;
            });
            AppearanceSettings a = new BatterySettingsStore(folder).Load().Appearance;
            Assert.Equal(ThemeChoice.Dark, a.Theme);
            Assert.Equal(AccentChoice.Teal, a.Accent);
            Assert.Equal(MaterialChoice.Solid, a.Material);
            Assert.Equal(CornerChoice.ExtraRound, a.Corners);
            Assert.Equal(DensityChoice.Compact, a.Density);
            Assert.Equal(MotionChoice.Off, a.Motion);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData(AccentChoice.Blue)]
    [InlineData(AccentChoice.Teal)]
    [InlineData(AccentChoice.Green)]
    [InlineData(AccentChoice.Purple)]
    [InlineData(AccentChoice.Orange)]
    public void Light_theme_accent_reaches_text_contrast_on_white(AccentChoice choice)
    {
        Rgb accent = AccentPalette.Resolve(choice, darkTheme: false)!.Value;
        Assert.True(ColorMath.ContrastRatio(accent, Rgb.White) >= 4.5);
    }

    [Fact]
    public void Windows_accent_has_no_override()
    {
        Assert.Null(AccentPalette.Resolve(AccentChoice.Windows, darkTheme: false));
    }
}
