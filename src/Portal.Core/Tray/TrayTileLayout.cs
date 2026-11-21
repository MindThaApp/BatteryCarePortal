namespace Portal.Core.Tray;

/// <summary>
/// Order and visibility of the tray pop-up tiles. Stored as one string in the settings: tile ids in order,
/// comma separated, a leading '-' marks a hidden tile ("limit,battery,-heat,topup,health").
/// </summary>
public sealed class TrayTileLayout
{
    public const string Battery = "battery";
    public const string Limit = "limit";
    public const string TopUp = "topup";
    public const string Heat = "heat";
    public const string Health = "health";

    public static readonly string[] DefaultOrder = { Battery, Limit, TopUp, Heat, Health };

    private readonly List<(string Id, bool Visible)> _items;

    private TrayTileLayout(List<(string, bool)> items) => _items = items;

    public IReadOnlyList<(string Id, bool Visible)> Items => _items;

    public static TrayTileLayout Default() => Parse(null);

    /// <summary>Unknown and repeated ids are dropped; tiles missing from the text are appended visible.</summary>
    public static TrayTileLayout Parse(string? text)
    {
        var items = new List<(string, bool)>();
        foreach (string raw in (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            bool visible = !raw.StartsWith('-');
            string id = raw.TrimStart('-');
            if (Array.IndexOf(DefaultOrder, id) < 0 || items.Exists(i => i.Item1 == id)) continue;
            items.Add((id, visible));
        }
        foreach (string id in DefaultOrder)
            if (!items.Exists(i => i.Item1 == id)) items.Add((id, true));
        return new TrayTileLayout(items);
    }

    public string Serialize() => string.Join(',', _items.Select(i => (i.Visible ? "" : "-") + i.Id));

    public bool IsVisible(string id) => _items.Find(i => i.Id == id).Visible;

    public void SetVisible(string id, bool visible)
    {
        int at = _items.FindIndex(i => i.Id == id);
        if (at >= 0) _items[at] = (id, visible);
    }

    /// <summary>Moves a tile among the tiles in <paramref name="shown"/> (the ones currently on screen); returns false at either end.</summary>
    public bool Move(string id, int direction, IReadOnlyCollection<string> shown)
    {
        int from = _items.FindIndex(i => i.Id == id);
        if (from < 0 || direction == 0) return false;
        int step = direction < 0 ? -1 : 1;
        for (int to = from + step; to >= 0 && to < _items.Count; to += step)
        {
            if (!shown.Contains(_items[to].Id)) continue;
            (string, bool) moving = _items[from];
            _items.RemoveAt(from);
            _items.Insert(to, moving);
            return true;
        }
        return false;
    }
}
