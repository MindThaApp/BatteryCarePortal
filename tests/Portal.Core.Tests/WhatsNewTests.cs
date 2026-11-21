using Portal.Core.Battery;
using Xunit;

namespace Portal.Core.Tests;

public class WhatsNewTests
{
    private const string Good = """
        { "version": "0.4.0", "cards": [
          { "title": "A", "text": "a text", "page": "Settings" },
          { "title": "B", "text": "b text" },
          { "title": "", "text": "dropped" } ] }
        """;

    [Fact]
    public void ParsesCardsAndDropsIncompleteOnes()
    {
        WhatsNewRelease? r = WhatsNewLogic.Parse(Good);
        Assert.NotNull(r);
        Assert.Equal("0.4.0", r!.Version);
        Assert.Equal(2, r.Cards.Count);
        Assert.Equal("Settings", r.Cards[0].Page);
        Assert.Equal("", r.Cards[1].Page);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{ \"version\": \"1\" }")]
    [InlineData("{ \"cards\": [ { \"title\": \"a\", \"text\": \"b\" } ] }")]
    [InlineData("{ \"version\": \"1\", \"cards\": [] }")]
    public void RejectsUnusableFiles(string? json) => Assert.Null(WhatsNewLogic.Parse(json));

    [Fact]
    public void KeepsAtMostFourCards()
    {
        string json = "{\"version\":\"1\",\"cards\":[" + string.Join(",", Enumerable.Range(0, 6).Select(i => $"{{\"title\":\"t{i}\",\"text\":\"x\"}}")) + "]}";
        Assert.Equal(WhatsNewLogic.MaxCards, WhatsNewLogic.Parse(json)!.Cards.Count);
    }

    [Theory]
    [InlineData("", "0.4.0", false, true)]
    [InlineData("0.3.0", "0.4.0", false, true)]
    [InlineData("0.4.0", "0.4.0", false, false)]
    [InlineData("0.3.0", "0.4.0", true, false)]
    [InlineData("", "", false, false)]
    public void ShowsOncePerVersionAndNeverWithTheWelcome(string seen, string current, bool welcome, bool expected) =>
        Assert.Equal(expected, WhatsNewLogic.ShouldShow(seen, current, welcome));

    [Fact]
    public void SettingsKeepSeenVersionThroughSaveAndLoad() =>
        Assert.Equal("0.4.0", new BatterySettings { WhatsNewSeenVersion = "0.4.0" }.Clone().WhatsNewSeenVersion);
}
