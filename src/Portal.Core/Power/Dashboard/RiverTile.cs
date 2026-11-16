namespace Portal.Core.Power.Dashboard;

/// <summary>
/// The power tree cut down for the small river of the tray "Power flow" tile: the root keeps its total, and only the
/// biggest parts of the PC get a stream of their own; the rest share one "other" stream. Nothing opens further.
/// </summary>
public static class RiverTile
{
    /// <summary>Id of the stream that holds the parts left out.</summary>
    public const string OtherId = "tray.other";

    public static PowerNode Trim(PowerNode root, int streams, bool includePsuLoss, string otherName)
    {
        if (!root.HasValue) return root with { Children = [] };
        streams = Math.Max(1, streams);
        List<PowerNode> parts = root.Children
            .Where(c => c.HasValue && c.Watts is double w && double.IsFinite(w) && w > 0)
            .Where(c => includePsuLoss || (c.Id != RiverLayout.PsuLossId && c.Category != PowerCategory.PsuLoss))
            .OrderByDescending(c => c.Watts)
            .ToList();
        // When some parts are left out, the last place goes to the stream that sums them up.
        int keep = parts.Count > streams ? streams - 1 : parts.Count;
        var shown = parts.Take(keep).Select(p => p with { Children = [] }).ToList();
        List<PowerNode> rest = parts.Skip(keep).ToList();
        if (rest.Count > 0)
            shown.Add(PowerNodes.Leaf(OtherId, otherName, PowerCategory.Unaccounted, rest.Sum(p => p.Watts!.Value), PowerNodes.Combine(rest)));
        return root with { Children = shown };
    }
}
