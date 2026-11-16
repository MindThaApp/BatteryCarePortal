namespace Portal.Core.Power.Dashboard;

/// <summary>What one stretch of the power river stands for.</summary>
public enum FlowRole
{
    /// <summary>Mains power: the charger of a laptop, the wall socket or the wall meter of a desktop.</summary>
    Mains,
    /// <summary>A laptop running on its battery.</summary>
    Battery,
    /// <summary>The PC or the laptop itself: the combined stream that every source feeds.</summary>
    Device,
    /// <summary>Power supply or adapter loss, a faded stream peeling off the device.</summary>
    Loss,
    /// <summary>Power going into the battery while it charges: an endpoint next to the parts.</summary>
    Charging,
    /// <summary>A main part of the device: CPU, graphics, memory, storage, fans, board, peripherals or the rest.</summary>
    Category,
    /// <summary>A part of a category, or a part of a part.</summary>
    Part,
}

/// <summary>The kind of one piece of the river.</summary>
public enum FlowSegmentKind
{
    /// <summary>Where a source starts, with a rounded start.</summary>
    Source,
    /// <summary>The combined stream through the device.</summary>
    Trunk,
    /// <summary>The loss peeling off the trunk and fading out.</summary>
    Loss,
    /// <summary>The river fanning out from a stream into its parts.</summary>
    Split,
    /// <summary>The short straight end of a part, with a rounded end; an open part runs on past its name instead.</summary>
    Stub,
}

/// <summary>Sizes for the power river, in device-independent pixels.</summary>
public sealed record FlowOptions
{
    /// <summary>Width to fill. The result is only wider when the stretches would get shorter than their least widths.</summary>
    public double Width { get; init; } = 900;
    /// <summary>Thickness of the river where it starts; every other stream is scaled from it by its watts.</summary>
    public double MainHeight { get; init; } = 180;
    /// <summary>Thinnest stream, so a tiny part stays visible next to a big one.</summary>
    public double MinSlice { get; init; } = 2;
    /// <summary>The thin break across the river between two stretches: the cable run between two loads.</summary>
    public double Break { get; init; } = 3;
    /// <summary>Least height of the row of a part, so its name fits.</summary>
    public double RowHeight { get; init; } = 22;
    public double RowGap { get; init; } = 6;
    /// <summary>Extra space above the battery being charged, which is not a part of the device.</summary>
    public double GroupGap { get; init; } = 10;
    /// <summary>Space above the first and below the last row.</summary>
    public double Padding { get; init; } = 4;
    /// <summary>Length of the rounded end of a part.</summary>
    public double StubWidth { get; init; } = 14;
    /// <summary>Space between the rounded end of a part and its name.</summary>
    public double LabelGap { get; init; } = 8;
    /// <summary>Room for the name of a part; 0 draws the river alone.</summary>
    public double LabelWidth { get; init; } = 132;
    /// <summary>The names may get this narrow before the diagram grows wider than <see cref="Width"/>.</summary>
    public double MinLabelWidth { get; init; } = 84;
    /// <summary>Least lengths of the source, the device and every split.</summary>
    public double SourceWidth { get; init; } = 120;
    public double TrunkWidth { get; init; } = 110;
    public double SplitWidth { get; init; } = 90;
    public double MaxSourceWidth { get; init; } = 240;
    public double MaxTrunkWidth { get; init; } = 260;
    /// <summary>Radius of the rounded start of the sources and the rounded ends of the parts.</summary>
    public double CapRadius { get; init; } = 9;
    /// <summary>How far the loss sinks below the river before it fades out, and the room for its name under it.</summary>
    public double LossDrop { get; init; } = 14;
    public double LossLabelHeight { get; init; } = 16;
    /// <summary>The name of an open part goes inside its stream from this thickness, and on a line above it when thinner.</summary>
    public double InsideLabelMin { get; init; } = 15;
    /// <summary>Height of one line of a name.</summary>
    public double LabelLine { get; init; } = 15;
}

/// <summary>One stream of the power river: a source, the device, the loss, or a part with its name.</summary>
public sealed record FlowBox
{
    public required string Id { get; init; }
    public FlowRole Role { get; init; }
    /// <summary>The tree node for loss, charging, categories and parts; null for the sources and the device.</summary>
    public PowerNode? Node { get; init; }
    public string? ParentId { get; init; }
    /// <summary>Stretch of the river: 0 the sources, 1 the device, 2 its parts, 3 their parts and so on.</summary>
    public int Column { get; init; }
    /// <summary>Level in the list view: sources 0, the device 1, its parts and the loss 2, their parts 3 and more.</summary>
    public int Level { get; init; }
    /// <summary>Place among the streams of its parent, top first.</summary>
    public int Index { get; init; }
    /// <summary>The stream's own stretch of river: the source band, the trunk, the rounded end or the open run of a part.</summary>
    public double X { get; init; }
    public double Width { get; init; }
    /// <summary>The row the stream and its name take up (for a part); the band itself for the sources and the device.</summary>
    public double Y { get; init; }
    public double Height { get; init; }
    /// <summary>The band of the stream, as thick as its watts.</summary>
    public double FlowTop { get; init; }
    public double FlowHeight { get; init; }
    /// <summary>Where the name goes: its left edge, its room and the middle of its line.</summary>
    public double LabelX { get; init; }
    public double LabelWidth { get; init; }
    public double LabelY { get; init; }
    /// <summary>An open part whose stream is too thin for its name: the name is on the line above the stream.</summary>
    public bool LabelAbove { get; init; }
    /// <summary>Watts to show (not eased).</summary>
    public double? Watts { get; init; }
    public PowerQuality Quality { get; init; }
    /// <summary>Share of the total in percent.</summary>
    public double? Percent { get; init; }
    /// <summary>True when the stream can open into its own parts.</summary>
    public bool HasChildren { get; init; }
    public bool Expanded { get; init; }

    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterY => Y + Height / 2;
    public bool IsEstimate => Quality is PowerQuality.Estimated or PowerQuality.Calibrated;
    /// <summary>True for the battery, as a source or being charged: selecting it opens the battery details.</summary>
    public bool IsBattery => Role is FlowRole.Battery or FlowRole.Charging;
    /// <summary>True for the parts and the battery being charged, which end in a rounded cap with their name.</summary>
    public bool IsEndpoint => Role is FlowRole.Category or FlowRole.Part or FlowRole.Charging;
}

/// <summary>
/// One piece of the river from (<see cref="X0"/>, <see cref="Top0"/>..<see cref="Bottom0"/>) to (<see cref="X1"/>,
/// <see cref="Top1"/>..<see cref="Bottom1"/>). Its colour runs from the stream <see cref="FromId"/> to <see cref="ToId"/>.
/// </summary>
public sealed record FlowRibbon
{
    private const double Arc = 0.5523;

    public required string FromId { get; init; }
    public required string ToId { get; init; }
    public FlowSegmentKind Kind { get; init; }
    public FlowRole TargetRole { get; init; }
    public double X0 { get; init; }
    public double Top0 { get; init; }
    public double Bottom0 { get; init; }
    public double X1 { get; init; }
    public double Top1 { get; init; }
    public double Bottom1 { get; init; }
    /// <summary>Radius of the rounded start (sources) and end (parts that are not open); 0 is a straight cut.</summary>
    public double CapStart { get; init; }
    public double CapEnd { get; init; }

    /// <summary>The key of this piece, the same while it changes shape (a part opening keeps its key).</summary>
    public string Key => Kind is FlowSegmentKind.Stub or FlowSegmentKind.Source ? $"{Kind}:{ToId}" : $"{Kind}:{FromId}>{ToId}";
    /// <summary>Power supply loss: the stream fades out towards its end.</summary>
    public bool Fades => Kind == FlowSegmentKind.Loss;
    public double Thickness => Math.Max(Bottom0 - Top0, Bottom1 - Top1);

    /// <summary>
    /// The rounded ends as soft elliptical corners: as tall as the radius allows (at most half the thickness), and as long as
    /// the radius or most of a short piece.
    /// </summary>
    private (Cap Start, Cap End) Radii()
    {
        double len = Math.Max(0, X1 - X0);
        double room = CapStart > 0 && CapEnd > 0 ? len / 2 : len * 0.85;
        Cap Make(double r, double thickness) => r <= 0 ? default : new Cap(Math.Min(r, room), Math.Min(r, thickness / 2));
        return (Make(CapStart, Bottom0 - Top0), Make(CapEnd, Bottom1 - Top1));
    }

    private readonly record struct Cap(double X, double Y)
    {
        public bool On => X > 0 && Y > 0;
    }

    private static RiverCubic Line(RiverPoint a, RiverPoint b) => new(a, b, b);

    private (RiverCubic Top, RiverCubic Bottom, RiverPoint TopStart, RiverPoint BottomStart) Edges(double tension, double waist, Cap c0, Cap c1)
    {
        double a = X0 + c0.X, b = X1 - c1.X;
        double dx = (b - a) * tension;
        var top = new RiverCubic(new RiverPoint(a + dx, Top0 + waist), new RiverPoint(b - dx, Top1 + waist), new RiverPoint(b, Top1));
        var bottom = new RiverCubic(new RiverPoint(b - dx, Bottom1 - waist), new RiverPoint(a + dx, Bottom0 - waist), new RiverPoint(a, Bottom0));
        return (top, bottom, new RiverPoint(a, Top0), new RiverPoint(b, Bottom1));
    }

    private IEnumerable<RiverCubic> EndSide(Cap c)
    {
        if (!c.On) { yield return Line(new RiverPoint(X1, Top1), new RiverPoint(X1, Bottom1)); yield break; }
        double rx = c.X, ry = c.Y;
        yield return new RiverCubic(new RiverPoint(X1 - rx + Arc * rx, Top1), new RiverPoint(X1, Top1 + ry - Arc * ry), new RiverPoint(X1, Top1 + ry));
        yield return Line(new RiverPoint(X1, Top1 + ry), new RiverPoint(X1, Bottom1 - ry));
        yield return new RiverCubic(new RiverPoint(X1, Bottom1 - ry + Arc * ry), new RiverPoint(X1 - rx + Arc * rx, Bottom1), new RiverPoint(X1 - rx, Bottom1));
    }

    private IEnumerable<RiverCubic> StartSide(Cap c)
    {
        if (!c.On) { yield return Line(new RiverPoint(X0, Bottom0), new RiverPoint(X0, Top0)); yield break; }
        double rx = c.X, ry = c.Y;
        yield return new RiverCubic(new RiverPoint(X0 + rx - Arc * rx, Bottom0), new RiverPoint(X0, Bottom0 - ry + Arc * ry), new RiverPoint(X0, Bottom0 - ry));
        yield return Line(new RiverPoint(X0, Bottom0 - ry), new RiverPoint(X0, Top0 + ry));
        yield return new RiverCubic(new RiverPoint(X0, Top0 + ry - Arc * ry), new RiverPoint(X0 + rx - Arc * rx, Top0), new RiverPoint(X0 + rx, Top0));
    }

    /// <summary>
    /// The filled shape: the top edge, the end, the bottom edge back and the start, closed. <paramref name="waist"/> draws both
    /// edges a little towards the middle between the ends, so even a straight stream gets a soft, organic shape.
    /// </summary>
    public RiverPath Outline(double tension = 0.5, double waist = 0)
    {
        (Cap r0, Cap r1) = Radii();
        var e = Edges(tension, waist, r0, r1);
        var parts = new List<RiverCubic> { e.Top };
        parts.AddRange(EndSide(r1));
        parts.Add(e.Bottom);
        parts.AddRange(StartSide(r0));
        return new RiverPath(e.TopStart, parts, Closed: true);
    }

    /// <summary>
    /// The outer edges for the soft rim and halo: the top and bottom edges with any rounded ends, but never the straight cuts,
    /// so the thin breaks between two pieces stay clean.
    /// </summary>
    public IReadOnlyList<RiverPath> Rim(double tension = 0.5, double waist = 0)
    {
        (Cap r0, Cap r1) = Radii();
        var e = Edges(tension, waist, r0, r1);
        if (!r0.On && !r1.On)
            return [new RiverPath(e.TopStart, [e.Top], false), new RiverPath(e.BottomStart, [e.Bottom], false)];
        if (!r0.On)
            return [new RiverPath(e.TopStart, [e.Top, .. EndSide(r1), e.Bottom], false)];
        if (!r1.On)
            return [new RiverPath(e.BottomStart, [e.Bottom, .. StartSide(r0), e.Top], false)];
        return [new RiverPath(e.TopStart, [e.Top, .. EndSide(r1), e.Bottom, .. StartSide(r0)], false)];
    }

    /// <summary>The upper edge alone, for the soft highlight along the top of the stream.</summary>
    public RiverPath TopEdge(double tension = 0.5, double waist = 0)
    {
        (Cap r0, Cap r1) = Radii();
        var e = Edges(tension, waist, r0, r1);
        return new RiverPath(e.TopStart, [e.Top], false);
    }

    /// <summary>The top and bottom of the stream at <paramref name="x"/> (between the ends), on the same curves the outline uses.</summary>
    public (double Top, double Bottom) At(double x, double tension = 0.5, double waist = 0)
    {
        (Cap r0, Cap r1) = Radii();
        var e = Edges(tension, waist, r0, r1);
        double a = X0 + r0.X, b = X1 - r1.X;
        if (b - a < 1e-6) return (Top0, Bottom0);
        x = Math.Clamp(x, a, b);
        double dx = (b - a) * tension;
        // x(t) of the top edge rises with t; the bottom edge runs the same way backwards.
        double lo = 0, hi = 1;
        for (int i = 0; i < 40; i++)
        {
            double mid = (lo + hi) / 2;
            if (Bez(a, a + dx, b - dx, b, mid) < x) lo = mid; else hi = mid;
        }
        double t = (lo + hi) / 2;
        double top = Bez(Top0, e.Top.C1.Y, e.Top.C2.Y, Top1, t);
        double bottom = Bez(Bottom0, e.Bottom.C2.Y, e.Bottom.C1.Y, Bottom1, t);
        return (top, bottom);
    }

    private static double Bez(double p0, double p1, double p2, double p3, double t)
    {
        double u = 1 - t;
        return u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3;
    }
}

public sealed record FlowLayoutResult(IReadOnlyList<FlowBox> Boxes, IReadOnlyList<FlowRibbon> Ribbons, double Width, double Height)
{
    public static readonly FlowLayoutResult Empty = new([], [], 0, 0);
    /// <summary>Stretches drawn: the sources, the device and one per level of parts shown.</summary>
    public int Columns { get; init; }
    /// <summary>The thin break used between stretches.</summary>
    public double Break { get; init; }
    public bool IsEmpty => Boxes.Count == 0;
    public FlowBox? Find(string id) => Boxes.FirstOrDefault(b => b.Id == id);
}

/// <summary>
/// Lays the whole-PC power tree out as one continuous river, left to right, cut only by thin breaks (the cable run between
/// two loads): the source (mains, or the battery while it powers the laptop), the PC or laptop with its loss peeling off,
/// the river fanning out into the main parts and the battery being charged, each ending in a rounded cap with its name,
/// and the parts of every part the user opened, which split again after another break. Streams are as thick as their
/// watts and the stretches spread over the whole width. Pure maths, no UI types.
/// </summary>
public static class FlowLayout
{
    public const string MainsId = "flow.mains";
    public const string BatteryId = "flow.battery";
    public const string DeviceId = "flow.device";

    private sealed class Item
    {
        public required string Id;
        public FlowRole Role;
        public PowerNode? Node;
        public Item? Parent;
        public int Column, Level, Index;
        public double FlowWatts;
        public double? Watts;
        public PowerQuality Quality;
        public bool HasChildren, Expanded, LabelAbove;
        public double Band, Row, Top, BandTop;
        public readonly List<Item> Children = [];
    }

    /// <summary>
    /// Lays out the river. <paramref name="onBattery"/> makes the battery the source; otherwise the source is mains.
    /// <paramref name="expanded"/> holds the ids of the parts that show their own parts. <paramref name="shownWatts"/> can hold
    /// eased watts per node id for the sizes; the labels keep the node's own watts. <paramref name="maxDepth"/> limits the
    /// levels of parts drawn (1 shows only the main parts).
    /// </summary>
    public static FlowLayoutResult Compute(PowerNode root, bool onBattery, ISet<string> expanded, FlowOptions? options = null,
        IReadOnlyDictionary<string, double>? shownWatts = null, int maxDepth = int.MaxValue)
    {
        FlowOptions o = options ?? new FlowOptions();
        double W(PowerNode n)
        {
            double w = shownWatts is not null && shownWatts.TryGetValue(n.Id, out double s) ? s : n.Watts ?? 0;
            return double.IsFinite(w) ? w : 0;
        }
        bool Drawable(PowerNode n) => n.HasValue && W(n) > 0;

        double total = W(root);
        if (!root.HasValue || total <= 0) return FlowLayoutResult.Empty;

        var source = new Item
        {
            Id = onBattery ? BatteryId : MainsId, Role = onBattery ? FlowRole.Battery : FlowRole.Mains, Column = 0, Level = 0,
            FlowWatts = total, Watts = root.Watts, Quality = root.Quality,
        };
        var device = new Item { Id = DeviceId, Role = FlowRole.Device, Column = 1, Level = 1, Parent = source, Watts = root.Watts, Quality = root.Quality };
        source.Children.Add(device);

        List<PowerNode> loss = root.Children.Where(c => c.Category == PowerCategory.PsuLoss && Drawable(c)).ToList();
        List<PowerNode> charging = root.Children.Where(c => c.Category == PowerCategory.Battery && Drawable(c)).ToList();
        List<PowerNode> parts = root.Children
            .Where(c => c.Category is not (PowerCategory.PsuLoss or PowerCategory.Battery) && Drawable(c))
            .OrderBy(c => RiverLayout.CategoryRank(c.Category)).ToList();

        Item Leaf(PowerNode n, FlowRole role, Item parent, int column, int level) => new()
        {
            Id = n.Id, Role = role, Node = n, Parent = parent, Column = column, Level = level, FlowWatts = W(n), Watts = n.Watts, Quality = n.Quality,
        };

        Item Branch(PowerNode n, FlowRole role, Item parent, int depth)
        {
            Item item = Leaf(n, role, parent, depth + 1, depth + 1);
            List<PowerNode> kids = n.Children.Where(Drawable).ToList();
            item.HasChildren = kids.Count > 0;
            item.Expanded = item.HasChildren && expanded.Contains(n.Id) && depth < maxDepth;
            if (item.Expanded)
                for (int i = 0; i < kids.Count; i++)
                {
                    Item child = Branch(kids[i], FlowRole.Part, item, depth + 1);
                    child.Index = i;
                    item.Children.Add(child);
                }
            return item;
        }

        var lossItems = loss.Select(l => Leaf(l, FlowRole.Loss, device, 1, 2)).ToList();
        if (maxDepth >= 1)
        {
            foreach (PowerNode p in parts) device.Children.Add(Branch(p, FlowRole.Category, device, 1));
            foreach (PowerNode c in charging) device.Children.Add(Leaf(c, FlowRole.Charging, device, 2, 2));
        }
        for (int i = 0; i < device.Children.Count; i++) device.Children[i].Index = i;
        for (int i = 0; i < lossItems.Count; i++) lossItems[i].Index = i;

        // Thickness, top down: the device and the loss share the river; every part takes its slice of its parent, so the
        // river carries the same thickness on across every break.
        double[] split = RiverLayout.Distribute([parts.Sum(W) + charging.Sum(W), loss.Sum(W)], o.MainHeight, loss.Count > 0 ? o.MinSlice : 0);
        source.Band = o.MainHeight;
        device.Band = loss.Count > 0 ? split[0] : o.MainHeight;
        double lossBand = o.MainHeight - device.Band;
        double[] lossSlices = RiverLayout.Distribute(lossItems.Select(l => l.FlowWatts).ToArray(), lossBand, o.MinSlice);
        for (int i = 0; i < lossItems.Count; i++) lossItems[i].Band = lossSlices[i];
        void Bands(Item parent)
        {
            if (parent.Children.Count == 0) return;
            double[] slices = RiverLayout.Distribute(parent.Children.Select(c => c.FlowWatts).ToArray(), parent.Band, o.MinSlice);
            for (int i = 0; i < parent.Children.Count; i++)
            {
                Item c = parent.Children[i];
                c.Band = slices[i];
                c.LabelAbove = c.Expanded && o.LabelWidth > 0 && c.Band < o.InsideLabelMin;
                c.Row = c.LabelAbove ? c.Band + o.LabelLine + 2 : Math.Max(c.Band, o.LabelWidth > 0 ? o.RowHeight : 0);
                Bands(c);
            }
        }
        Bands(device);

        // The parts by level, each level in its parents' top-to-bottom order.
        var levels = new List<List<Item>>();
        var queue = new Queue<Item>(device.Children);
        while (queue.Count > 0)
        {
            Item i = queue.Dequeue();
            int d = i.Column - 2;
            while (levels.Count <= d) levels.Add([]);
            levels[d].Add(i);
            foreach (Item c in i.Children) queue.Enqueue(c);
        }
        int depthCount = levels.Count;

        // Lengths: the names and the rounded ends are fixed; the source, the device and the splits share the rest.
        double labelGap = o.LabelWidth > 0 ? o.LabelGap : 0;
        double breaks = o.Break * (depthCount > 0 ? 1 + 2 * depthCount : 1);
        double labelW = o.LabelWidth;
        double mins = o.SourceWidth + o.TrunkWidth + depthCount * o.SplitWidth;
        double Fixed() => breaks + depthCount * (o.StubWidth + labelGap + labelW);
        if (depthCount > 0 && labelW > 0 && o.Width - Fixed() < mins)
            labelW = Math.Max(Math.Min(o.LabelWidth, o.MinLabelWidth), (o.Width - breaks - depthCount * (o.StubWidth + labelGap) - mins) / depthCount);
        labelW = Math.Min(labelW, o.LabelWidth);
        double flexible = Math.Max(mins, o.Width - Fixed());
        double aW, bW, cW;
        if (depthCount == 0)
        {
            aW = Math.Clamp(flexible * 0.4, o.SourceWidth, o.MaxSourceWidth);
            bW = Math.Max(o.TrunkWidth, flexible - aW);
            cW = 0;
        }
        else
        {
            double share = flexible / (1 + 0.9 + 1.4 * depthCount);
            aW = Math.Clamp(share, o.SourceWidth, Math.Max(o.SourceWidth, o.MaxSourceWidth));
            bW = Math.Clamp(share * 0.9, o.TrunkWidth, Math.Max(o.TrunkWidth, o.MaxTrunkWidth));
            cW = Math.Max(o.SplitWidth, (flexible - aW - bW) / depthCount);
            double excess = aW + bW + cW * depthCount - flexible;
            if (excess > 1e-9)
            {
                double room = aW - o.SourceWidth + bW - o.TrunkWidth;
                if (room > 0)
                {
                    double take = Math.Min(1, excess / room);
                    aW -= (aW - o.SourceWidth) * take;
                    bW -= (bW - o.TrunkWidth) * take;
                }
            }
        }

        double aEnd = aW, bStart = aEnd + o.Break, bEnd = bStart + bW;
        var splitStart = new double[depthCount];
        var stubStart = new double[depthCount];
        var labelX = new double[depthCount];
        double x = bEnd + o.Break;
        for (int d = 0; d < depthCount; d++)
        {
            splitStart[d] = x;
            stubStart[d] = x + cW + o.Break;
            labelX[d] = stubStart[d] + o.StubWidth + labelGap;
            x = labelX[d] + labelW + o.Break;
        }
        double width = depthCount > 0 ? labelX[depthCount - 1] + labelW : bEnd;

        // Height: the river with the loss hanging below it, or the tallest level of parts.
        double lossRoom = lossItems.Count > 0 ? o.LossDrop + o.LossLabelHeight : 0;
        double GapAbove(List<Item> level, int i) => i == 0 ? 0 : level[i].Role == FlowRole.Charging && level[i - 1].Role != FlowRole.Charging ? o.RowGap + o.GroupGap : o.RowGap;
        double height = o.MainHeight + lossRoom;
        foreach (List<Item> level in levels)
            height = Math.Max(height, level.Sum(i => i.Row) + Enumerable.Range(0, level.Count).Sum(i => GapAbove(level, i)));
        height += 2 * o.Padding;

        double y = o.Padding + (height - 2 * o.Padding - (o.MainHeight + lossRoom)) / 2;
        source.Top = source.BandTop = device.Top = device.BandTop = y;
        double ly = y + device.Band;
        foreach (Item l in lossItems) { l.BandTop = ly; ly += l.Band; }

        // Every level: each row as close as it can get to its slice of its parent.
        var sliceTop = new Dictionary<Item, double>(ReferenceEqualityComparer.Instance);
        void Slices(Item parent)
        {
            double t = parent.BandTop;
            foreach (Item c in parent.Children) { sliceTop[c] = t; t += c.Band; }
        }
        Slices(device);
        foreach (List<Item> level in levels)
        {
            double BandOffset(Item i) => i.LabelAbove ? i.Row - i.Band : (i.Row - i.Band) / 2;
            double[] wanted = level.Select(i => sliceTop[i] - BandOffset(i)).ToArray();
            double[] gaps = Enumerable.Range(0, level.Count).Select(i => GapAbove(level, i)).ToArray();
            double[] tops = Resolve(wanted, level.Select(i => i.Row).ToArray(), gaps, o.Padding, height - o.Padding);
            for (int i = 0; i < level.Count; i++)
            {
                level[i].Top = tops[i];
                level[i].BandTop = tops[i] + BandOffset(level[i]);
            }
            foreach (Item i in level) Slices(i);
        }

        // The pieces of the river.
        var ribbons = new List<FlowRibbon>
        {
            new()
            {
                FromId = source.Id, ToId = source.Id, Kind = FlowSegmentKind.Source, TargetRole = source.Role,
                X0 = 0, Top0 = y, Bottom0 = y + o.MainHeight, X1 = aEnd, Top1 = y, Bottom1 = y + o.MainHeight, CapStart = o.CapRadius,
            },
            new()
            {
                FromId = source.Id, ToId = device.Id, Kind = FlowSegmentKind.Trunk, TargetRole = FlowRole.Device,
                X0 = bStart, Top0 = y, Bottom0 = y + device.Band, X1 = bEnd, Top1 = y, Bottom1 = y + device.Band,
                CapEnd = depthCount == 0 ? o.CapRadius : 0,
            },
        };
        double lossEnd = bStart + bW * 0.92;
        foreach (Item l in lossItems)
            ribbons.Add(new FlowRibbon
            {
                FromId = device.Id, ToId = l.Id, Kind = FlowSegmentKind.Loss, TargetRole = FlowRole.Loss,
                X0 = bStart, Top0 = l.BandTop, Bottom0 = l.BandTop + l.Band,
                X1 = lossEnd, Top1 = l.BandTop + o.LossDrop, Bottom1 = l.BandTop + l.Band + o.LossDrop,
            });
        for (int d = 0; d < depthCount; d++)
            foreach (Item i in levels[d])
            {
                double s = sliceTop[i];
                ribbons.Add(new FlowRibbon
                {
                    FromId = i.Parent!.Id, ToId = i.Id, Kind = FlowSegmentKind.Split, TargetRole = i.Role,
                    X0 = splitStart[d], Top0 = s, Bottom0 = s + i.Band, X1 = splitStart[d] + cW, Top1 = i.BandTop, Bottom1 = i.BandTop + i.Band,
                });
                double end = i.Expanded ? labelX[d] + labelW : stubStart[d] + o.StubWidth;
                ribbons.Add(new FlowRibbon
                {
                    FromId = i.Id, ToId = i.Id, Kind = FlowSegmentKind.Stub, TargetRole = i.Role,
                    X0 = stubStart[d], Top0 = i.BandTop, Bottom0 = i.BandTop + i.Band, X1 = end, Top1 = i.BandTop, Bottom1 = i.BandTop + i.Band,
                    CapEnd = i.Expanded ? 0 : o.CapRadius,
                });
            }

        // The streams, in list order (a part before its own parts).
        var all = new List<Item> { source, device };
        all.AddRange(lossItems);
        void Walk(Item i) { all.Add(i); foreach (Item c in i.Children) Walk(c); }
        foreach (Item c in device.Children) Walk(c);
        double rootWatts = root.Watts ?? total;
        FlowBox Box(Item i)
        {
            double bx, bw, lx, lw, labelY, top = i.Top, h = i.Row;
            switch (i.Role)
            {
                case FlowRole.Mains or FlowRole.Battery:
                    bx = 0; bw = aEnd; lx = 0; lw = aEnd; labelY = y + o.MainHeight / 2; h = o.MainHeight;
                    break;
                case FlowRole.Device:
                    bx = bStart; bw = bW; lx = bStart; lw = bW; labelY = y + device.Band / 2; h = device.Band;
                    break;
                case FlowRole.Loss:
                    bx = bStart; bw = lossEnd - bStart; lx = bStart; lw = lossEnd - bStart;
                    top = i.BandTop; h = i.Band + o.LossDrop + o.LossLabelHeight;
                    labelY = i.BandTop + i.Band + o.LossDrop + o.LossLabelHeight / 2 + 1;
                    break;
                default:
                    int d = i.Column - 2;
                    bx = stubStart[d]; bw = i.Expanded ? labelX[d] + labelW - stubStart[d] : o.StubWidth;
                    lx = labelX[d]; lw = labelW;
                    labelY = i.LabelAbove ? i.Top + o.LabelLine / 2 : i.BandTop + i.Band / 2;
                    break;
            }
            return new FlowBox
            {
                Id = i.Id, Role = i.Role, Node = i.Node, ParentId = i.Parent?.Id, Column = i.Column, Level = i.Level, Index = i.Index,
                X = bx, Width = bw, Y = top, Height = h, FlowTop = i.BandTop, FlowHeight = i.Role == FlowRole.Mains || i.Role == FlowRole.Battery ? o.MainHeight : i.Band,
                LabelX = lx, LabelWidth = lw, LabelY = labelY, LabelAbove = i.LabelAbove,
                Watts = i.Watts, Quality = i.Quality, HasChildren = i.HasChildren, Expanded = i.Expanded,
                Percent = i.Role is FlowRole.Mains or FlowRole.Battery or FlowRole.Device || i.Watts is not double w ? null : Math.Round(w / rootWatts * 100, 1),
            };
        }
        return new FlowLayoutResult(all.Select(Box).ToList(), ribbons, width, height) { Columns = 2 + depthCount, Break = o.Break };
    }

    /// <summary>
    /// Tops for rows of one level, top first: each as near its wanted top as it can be, never overlapping the one above
    /// (with <paramref name="gap"/> between them) and staying between <paramref name="top"/> and <paramref name="bottom"/>.
    /// </summary>
    public static double[] Resolve(IReadOnlyList<double> wanted, IReadOnlyList<double> heights, double gap, double top, double bottom) =>
        Resolve(wanted, heights, Enumerable.Repeat(gap, wanted.Count).ToArray(), top, bottom);

    /// <summary>As <see cref="Resolve(IReadOnlyList{double}, IReadOnlyList{double}, double, double, double)"/>, with its own gap above each row.</summary>
    public static double[] Resolve(IReadOnlyList<double> wanted, IReadOnlyList<double> heights, IReadOnlyList<double> gaps, double top, double bottom)
    {
        int n = wanted.Count;
        var result = new double[n];
        for (int i = 0; i < n; i++)
            result[i] = Math.Max(wanted[i], i == 0 ? top : result[i - 1] + heights[i - 1] + gaps[i]);
        for (int i = n - 1; i >= 0; i--)
        {
            double limit = i == n - 1 ? bottom - heights[i] : result[i + 1] - gaps[i + 1] - heights[i];
            if (result[i] > limit) result[i] = limit;
        }
        for (int i = 0; i < n; i++)
            result[i] = Math.Max(result[i], i == 0 ? top : result[i - 1] + heights[i - 1] + gaps[i]);
        return result;
    }

    /// <summary>Opens a closed part or closes an open one. Returns true when it is open afterwards.</summary>
    public static bool Toggle(ISet<string> expanded, string id) => RiverLayout.Toggle(expanded, id);

    /// <summary>
    /// The tree for a small flow (the tray tile): the biggest <paramref name="keep"/> main parts, the rest summed into one
    /// called <paramref name="otherName"/>; the loss branch only when <paramref name="includeLoss"/>. Parts keep their own parts only
    /// with <paramref name="keepParts"/> (a tile where they can be opened).
    /// The watts are the tree's own; nothing is recalculated.
    /// </summary>
    public static PowerNode TrimForTile(PowerNode root, int keep, bool includeLoss, string otherName, bool keepParts = false)
    {
        if (!root.HasValue) return root with { Children = [] };
        keep = Math.Max(1, keep);
        var kept = new List<PowerNode>();
        List<PowerNode> parts = root.Children
            .Where(c => c.Category is not (PowerCategory.PsuLoss or PowerCategory.Battery))
            .Where(c => c.HasValue && c.Watts is double w && double.IsFinite(w) && w > 0)
            .OrderByDescending(c => c.Watts)
            .ToList();
        int take = parts.Count > keep ? keep - 1 : parts.Count;
        kept.AddRange(parts.Take(take).Select(p => keepParts ? p : p with { Children = [] }));
        List<PowerNode> rest = parts.Skip(take).ToList();
        if (rest.Count > 0)
            kept.Add(PowerNodes.Leaf(RiverTile.OtherId, otherName, PowerCategory.Unaccounted, rest.Sum(p => p.Watts!.Value), PowerNodes.Combine(rest)));
        // Parts that are not drawn (negative leftovers, no value) still belong to the device total.
        kept.AddRange(root.Children.Where(c => c.Category is not (PowerCategory.PsuLoss or PowerCategory.Battery) && !(c.HasValue && c.Watts is double w && double.IsFinite(w) && w > 0)));
        kept.AddRange(root.Children.Where(c => c.Category == PowerCategory.Battery));
        if (includeLoss) kept.AddRange(root.Children.Where(c => c.Category == PowerCategory.PsuLoss));
        return root with { Children = kept };
    }
}
