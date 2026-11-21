using System.Text.Json;

namespace Portal.Core.Battery;

/// <summary>One highlight: a title, a sentence and the page the Show me link opens.</summary>
public sealed record WhatsNewCard(string Title, string Text, string Page);

public sealed record WhatsNewRelease(string Version, IReadOnlyList<WhatsNewCard> Cards);

/// <summary>Pure: reading a per-release highlights file and deciding when What's new opens by itself.</summary>
public static class WhatsNewLogic
{
    public const int MaxCards = 4;

    /// <summary>Null when the text is not a usable release file (no version or no cards).</summary>
    public static WhatsNewRelease? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string version = Text(root, "version");
            if (version.Length == 0 || !root.TryGetProperty("cards", out JsonElement list) || list.ValueKind != JsonValueKind.Array) return null;

            var cards = new List<WhatsNewCard>();
            foreach (JsonElement item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || cards.Count >= MaxCards) continue;
                string title = Text(item, "title");
                string text = Text(item, "text");
                if (title.Length == 0 || text.Length == 0) continue;
                cards.Add(new WhatsNewCard(title, text, Text(item, "page")));
            }
            return cards.Count == 0 ? null : new WhatsNewRelease(version, cards);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the running version differs from the one last shown. Never at a launch that shows the welcome (that launch
    /// is one screen only); a brand-new install is marked as seen so it does not open What's new after the welcome.
    /// </summary>
    public static bool ShouldShow(string? lastSeenVersion, string currentVersion, bool welcomeThisLaunch) =>
        !welcomeThisLaunch
        && currentVersion.Length > 0
        && !string.Equals(lastSeenVersion ?? string.Empty, currentVersion, StringComparison.Ordinal);

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? string.Empty : string.Empty;
}
