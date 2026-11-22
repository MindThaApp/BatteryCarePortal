using Portal.Core.Battery;
using Xunit;

namespace Portal.Core.Tests;

public class WelcomeCaptureTests
{
    [Fact]
    public void Parses_all_fields()
    {
        WelcomeCaptureOptions o = WelcomeCapture.Parse("{\"step\":2,\"theme\":\"dark\",\"textScale\":1.5}");
        Assert.Equal(new WelcomeCaptureOptions(2, "Dark", 1.5), o);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{}")]
    public void Missing_or_bad_json_gives_defaults(string? json) =>
        Assert.Equal(new WelcomeCaptureOptions(1, null, 1.0), WelcomeCapture.Parse(json));

    [Fact]
    public void Values_are_clamped_and_unknown_theme_ignored()
    {
        Assert.Equal(new WelcomeCaptureOptions(3, null, 2.0), WelcomeCapture.Parse("{\"step\":9,\"theme\":\"Pink\",\"textScale\":5}"));
        Assert.Equal(new WelcomeCaptureOptions(1, null, 1.0), WelcomeCapture.Parse("{\"step\":0,\"textScale\":0.2}"));
    }

    [Fact]
    public void Ignored_outside_fake_mode_and_without_flag()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bcp-welcome-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(WelcomeCapture.Read(dir, fakeMode: true));
            File.WriteAllText(Path.Combine(dir, WelcomeCapture.FlagFile), "");
            File.WriteAllText(Path.Combine(dir, WelcomeCapture.JsonFile), "{\"step\":3,\"theme\":\"Light\"}");
            Assert.Null(WelcomeCapture.Read(dir, fakeMode: false));
            Assert.Equal(new WelcomeCaptureOptions(3, "Light", 1.0), WelcomeCapture.Read(dir, fakeMode: true));
            File.Delete(Path.Combine(dir, WelcomeCapture.JsonFile));
            Assert.Equal(new WelcomeCaptureOptions(1, null, 1.0), WelcomeCapture.Read(dir, fakeMode: true));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
