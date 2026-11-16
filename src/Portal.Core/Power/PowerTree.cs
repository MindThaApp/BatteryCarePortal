namespace Portal.Core.Power;

/// <summary>How a number in the power tree was obtained. The UI must show this next to every value.</summary>
public enum PowerQuality
{
    /// <summary>Read from a sensor or meter that reports watts directly.</summary>
    Measured,
    /// <summary>Computed from a model (hardware type, activity, typical values).</summary>
    Estimated,
    /// <summary>A model value fitted to a wall meter, live or from an earlier learning run.</summary>
    Calibrated,
    /// <summary>Not known on this PC.</summary>
    Unavailable,
}

public enum PowerCategory
{
    Total,
    Cpu,
    Gpu,
    Memory,
    Storage,
    Fans,
    Motherboard,
    Peripherals,
    PsuLoss,
    Unaccounted,
    /// <summary>Laptop battery charging, drawn as a sink next to the parts of the PC.</summary>
    Battery,
}

/// <summary>Where the total comes from.</summary>
public enum PowerModelMode
{
    /// <summary>No wall meter and no calibration: every non-sensor value is a model estimate.</summary>
    Estimate,
    /// <summary>No wall meter, but a calibration learned earlier from one is applied.</summary>
    Calibrated,
    /// <summary>The power supply reports its own input power.</summary>
    PsuMeasured,
    /// <summary>A fresh wall reading anchors the total.</summary>
    WallMeter,
    /// <summary>A laptop running on its battery: the battery's own discharge reading is the total.</summary>
    Battery,
}

/// <summary>One node of the whole-PC power tree. A parent's watts are the sum of its children.</summary>
public sealed record PowerNode
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public PowerCategory Category { get; init; }
    /// <summary>Watts, or null when <see cref="Quality"/> is Unavailable.</summary>
    public double? Watts { get; init; }
    public PowerQuality Quality { get; init; } = PowerQuality.Unavailable;
    /// <summary>Short description such as "DDR5 32 GB, 12 % busy". Never localized here.</summary>
    public string? Detail { get; init; }
    public IReadOnlyList<PowerNode> Children { get; init; } = [];

    public bool HasValue => Watts is not null && Quality != PowerQuality.Unavailable;
    public bool IsEstimate => Quality is PowerQuality.Estimated or PowerQuality.Calibrated;

    public static PowerNode Unavailable(string id, string name, PowerCategory category, string? detail = null) =>
        new() { Id = id, Name = name, Category = category, Watts = null, Quality = PowerQuality.Unavailable, Detail = detail };

    /// <summary>All nodes below this one, depth first.</summary>
    public IEnumerable<PowerNode> Descendants()
    {
        foreach (PowerNode child in Children)
        {
            yield return child;
            foreach (PowerNode d in child.Descendants()) yield return d;
        }
    }

    /// <summary>Leaves below (or this node itself when it has no children).</summary>
    public IEnumerable<PowerNode> Leaves() =>
        Children.Count == 0 ? [this] : Children.SelectMany(c => c.Leaves());
}

/// <summary>Builds group nodes whose watts and quality follow their children.</summary>
public static class PowerNodes
{
    /// <summary>The weakest quality of the children that have a value: Estimated &lt; Calibrated &lt; Measured.</summary>
    public static PowerQuality Combine(IEnumerable<PowerNode> children)
    {
        bool any = false, estimated = false, calibrated = false;
        foreach (PowerNode c in children)
        {
            if (!c.HasValue) continue;
            any = true;
            if (c.Quality == PowerQuality.Estimated) estimated = true;
            else if (c.Quality == PowerQuality.Calibrated) calibrated = true;
        }
        return !any ? PowerQuality.Unavailable : estimated ? PowerQuality.Estimated : calibrated ? PowerQuality.Calibrated : PowerQuality.Measured;
    }

    public static PowerNode Group(string id, string name, PowerCategory category, IReadOnlyList<PowerNode> children, string? detail = null)
    {
        double sum = 0;
        bool any = false;
        foreach (PowerNode c in children)
        {
            if (!c.HasValue) continue;
            sum += c.Watts!.Value;
            any = true;
        }
        return new PowerNode
        {
            Id = id,
            Name = name,
            Category = category,
            Watts = any ? Math.Round(sum, 2) : null,
            Quality = Combine(children),
            Detail = detail,
            Children = children,
        };
    }

    public static PowerNode Leaf(string id, string name, PowerCategory category, double watts, PowerQuality quality, string? detail = null) =>
        new() { Id = id, Name = name, Category = category, Watts = Math.Round(watts, 2), Quality = quality, Detail = detail };

    /// <summary>Copy of the tree with every node passed through <paramref name="map"/> (leaves first), then groups re-summed.</summary>
    public static PowerNode MapLeaves(PowerNode node, Func<PowerNode, PowerNode> map)
    {
        if (node.Children.Count == 0) return map(node);
        PowerNode[] children = node.Children.Select(c => MapLeaves(c, map)).ToArray();
        return Group(node.Id, node.Name, node.Category, children, node.Detail);
    }
}

/// <summary>Everything the power model knows at one moment.</summary>
public sealed record PowerTreeSnapshot
{
    public DateTimeOffset Timestamp { get; init; }
    public PowerModelMode Mode { get; init; } = PowerModelMode.Estimate;
    /// <summary>Root node: whole PC at the wall. Its children are the components, PSU loss and any unaccounted power.</summary>
    public PowerNode Root { get; init; } = PowerNode.Unavailable("pc", "Whole PC", PowerCategory.Total);
    /// <summary>DC power delivered by the PSU (wall minus PSU loss).</summary>
    public double? DcWatts { get; init; }
    /// <summary>Name of the wall meter when <see cref="Mode"/> is WallMeter.</summary>
    public string? WallSourceName { get; init; }
    /// <summary>Energy since the model started, from <see cref="EnergyAccumulator"/>.</summary>
    public EnergyTotals? Energy { get; init; }
    /// <summary>Battery power in watts: positive while charging, negative while discharging, null without a battery reading.</summary>
    public double? BatteryWatts { get; init; }

    public double? TotalWatts => Root.Watts;
    public PowerQuality TotalQuality => Root.Quality;

    public PowerNode? Find(string id) => Root.Id == id ? Root : Root.Descendants().FirstOrDefault(n => n.Id == id);
}
