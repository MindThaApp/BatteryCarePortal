namespace Portal.Core.Power.Dashboard;

/// <summary>Sizes for the power river. All values are in device-independent pixels.</summary>
public sealed record RiverOptions
{
    /// <summary>Width the river may use; the result is wider when the columns would get too narrow.</summary>
    public double Width { get; init; } = 900;
    /// <summary>Thickness of the whole stream at the source.</summary>
    public double SourceThickness { get; init; } = 140;
    /// <summary>Thinnest stream that is still drawn, so a 1 W part stays visible next to a 300 W one.</summary>
    public double MinThickness { get; init; } = 3;
    /// <summary>Least vertical room per visible stream; one label line fits in it.</summary>
    public double RowHeight { get; init; } = 26;
    /// <summary>Height of one label line.</summary>
    public double LabelHeight { get; init; } = 18;
    /// <summary>Space between neighbouring streams after they split.</summary>
    public double Gap { get; init; } = 6;
    /// <summary>Room right of the river for the labels of the streams that end there.</summary>
    public double LabelWidth { get; init; } = 180;
    /// <summary>Length of the source before the first split.</summary>
    public double SourceLength { get; init; } = 72;
    public double MinColumnWidth { get; init; } = 110;
    public double MaxColumnWidth { get; init; } = 280;
    /// <summary>Share of a column used by the curve of a split; the rest is calm water.</summary>
    public double CurveShare { get; init; } = 0.55;
    /// <summary>A stream that ends keeps this share of its thickness at its mouth.</summary>
    public double TailTaper { get; init; } = 0.82;
}

public readonly record struct RiverPoint(double X, double Y);

/// <summary>One cubic Bezier piece: two control points and the end point.</summary>
public readonly record struct RiverCubic(RiverPoint C1, RiverPoint C2, RiverPoint End);

/// <summary>A path of cubic pieces from <see cref="Start"/>. <see cref="Closed"/> paths are filled.</summary>
public sealed record RiverPath(RiverPoint Start, IReadOnlyList<RiverCubic> Segments, bool Closed);

/// <summary>Where a label goes: its left edge and the vertical centre of its line.</summary>
public readonly record struct RiverLabel(double X, double CenterY, double Height)
{
    public double Top => CenterY - Height / 2;
    public double Bottom => CenterY + Height / 2;
}

/// <summary>One stream of the river: the source, a part, or a part of a part.</summary>
public sealed record RiverStream
{
    public required PowerNode Node { get; init; }
    public string Id => Node.Id;
    public string? ParentId { get; init; }
    /// <summary>0 for the source, 1 for the parts of the PC, 2 and more below.</summary>
    public int Depth { get; init; }
    /// <summary>Place among the drawn siblings, top first.</summary>
    public int Index { get; init; }
    public double Thickness { get; init; }
    /// <summary>Share of the whole PC in percent, from the node's own watts.</summary>
    public double? Percent { get; init; }
    public bool HasChildren { get; init; }
    public bool Expanded { get; init; }
    /// <summary>Power supply loss: a thin stream that leaves first and fades out.</summary>
    public bool Evaporates { get; init; }
    /// <summary>Left end of the stream, where it leaves its parent.</summary>
    public double StartX { get; init; }
    public double EndX { get; init; }
    /// <summary>Vertical centre of the stream's calm part (after the split curve).</summary>
    public double CenterY { get; init; }
    public required RiverPath Outline { get; init; }
    public required RiverPath TopBank { get; init; }
    public required RiverPath BottomBank { get; init; }
    /// <summary>The middle of the stream, for the current lines.</summary>
    public required RiverPath Centerline { get; init; }
    public double CenterlineLength { get; init; }
    public RiverLabel Label { get; init; }
}

public sealed record RiverLayoutResult(IReadOnlyList<RiverStream> Streams, double Width, double Height)
{
    public static readonly RiverLayoutResult Empty = new([], 0, 0);
    /// <summary>Right end of the water (before the labels), where the sinks end.</summary>
    public double MouthX { get; init; }
    public bool IsEmpty => Streams.Count == 0;
    public RiverStream? Find(string id) => Streams.FirstOrDefault(s => s.Id == id);
}

/// <summary>
/// Lays out the whole-PC power tree as a river: the source on the left splits into a stream per part, each as thick as its
/// share of the watts, and an expanded part splits again further downstream. Pure maths, no UI types.
/// </summary>
public static class RiverLayout
{
    /// <summary>Id of the power supply loss node, drawn as the stream that evaporates.</summary>
    public const string PsuLossId = "psu.loss";

    /// <summary>
    /// Lays out the visible streams. <paramref name="expanded"/> holds the ids whose children are shown (the root's children
    /// always are). <paramref name="shownWatts"/> can hold eased watts per id for the widths; labels use the node's watts.
    /// </summary>
    public static RiverLayoutResult Compute(PowerNode root, ISet<string> expanded, RiverOptions? options = null, IReadOnlyDictionary<string, double>? shownWatts = null)
    {
        RiverOptions o = options ?? new RiverOptions();
        double W(PowerNode n) => shownWatts is not null && shownWatts.TryGetValue(n.Id, out double v) ? v : n.Watts ?? 0;
        if (!root.HasValue || !(W(root) > 0)) return RiverLayoutResult.Empty;

        // Pass 1: thickness of every visible stream, top-down; each family sums to its parent.
        var thickness = new Dictionary<string, double> { [root.Id] = o.SourceThickness };
        var children = new Dictionary<string, IReadOnlyList<PowerNode>>();
        int maxDepth = 0;
        void Size(PowerNode n, int depth)
        {
            IReadOnlyList<PowerNode> kids = Drawable(n, W);
            children[n.Id] = kids;
            bool open = depth == 0 || expanded.Contains(n.Id);
            if (!open || kids.Count == 0) return;
            maxDepth = Math.Max(maxDepth, depth + 1);
            double[] t = Distribute(kids.Select(W).ToArray(), thickness[n.Id], o.MinThickness);
            for (int i = 0; i < kids.Count; i++)
            {
                thickness[kids[i].Id] = t[i];
                Size(kids[i], depth + 1);
            }
        }
        Size(root, 0);
        if (maxDepth == 0) maxDepth = 1;

        bool IsOpen(PowerNode n, int depth) => children[n.Id].Count > 0 && (depth == 0 || expanded.Contains(n.Id));

        // Pass 2: vertical room of every visible stream, bottom-up. An open stream keeps a label line above its water.
        var extent = new Dictionary<string, double>();
        double Extent(PowerNode n, int depth)
        {
            double t = thickness[n.Id];
            double e;
            if (IsOpen(n, depth))
            {
                IReadOnlyList<PowerNode> kids = children[n.Id];
                double sum = kids.Sum(k => Extent(k, depth + 1)) + o.Gap * (kids.Count - 1);
                e = Math.Max(depth == 0 ? t : t + 2 * (o.LabelHeight + 2), sum);
            }
            else e = Math.Max(t, o.RowHeight);
            extent[n.Id] = e;
            return e;
        }
        double rootExtent = Extent(root, 0);

        double available = o.Width - o.LabelWidth - o.SourceLength;
        double colW = Math.Clamp(available / maxDepth, o.MinColumnWidth, o.MaxColumnWidth);
        double curve = colW * o.CurveShare;
        double Split(int depth) => o.SourceLength + depth * colW;
        double mouth = Split(maxDepth);
        double top = o.LabelHeight + 6;
        double total = W(root);
        double? realTotal = root.Watts is > 0 ? root.Watts : null;

        var streams = new List<RiverStream>();
        double rootTop = top + (rootExtent - o.SourceThickness) / 2;
        streams.Add(Straight(root, null, 0, 0, o.SourceThickness, 0, Split(0), rootTop, realTotal, true, false,
            new RiverLabel(0, o.LabelHeight / 2 + 2, o.LabelHeight)));

        // Pass 3: positions. Children leave their parent stacked edge to edge and curve out to their own room.
        void Place(PowerNode parent, int depth, double bandTop, double roomTop)
        {
            IReadOnlyList<PowerNode> kids = children[parent.Id];
            double block = kids.Sum(k => extent[k.Id]) + o.Gap * (kids.Count - 1);
            double cursor = roomTop + (extent[parent.Id] - block) / 2;
            double startY = bandTop;
            double x0 = Split(depth);
            for (int i = 0; i < kids.Count; i++)
            {
                PowerNode k = kids[i];
                double t = thickness[k.Id];
                double e = extent[k.Id];
                double destTop = cursor + (e - t) / 2;
                bool open = IsOpen(k, depth + 1);
                double x1 = x0 + curve;
                double end = open ? Split(depth + 1) : mouth;
                RiverLabel label = open
                    ? new RiverLabel(x1 + 4, destTop - 2 - o.LabelHeight / 2, o.LabelHeight)
                    : new RiverLabel(mouth + 14, destTop + t / 2, o.LabelHeight);
                streams.Add(Branch(k, parent.Id, depth + 1, i, t, x0, startY, x1, destTop, end, open, realTotal, children[k.Id].Count > 0, label, o));
                if (open) Place(k, depth + 1, destTop, cursor);
                cursor += e + o.Gap;
                startY += t;
            }
        }
        Place(root, 0, rootTop, top);

        double height = top + rootExtent + 4;
        SpreadMouthLabels(streams, top, height);
        return new RiverLayoutResult(streams, mouth + o.LabelWidth, height) { MouthX = mouth };
    }

    /// <summary>
    /// Children that get a stream: a value above zero, in one fixed order whatever order the tree lists them in (the order of
    /// <see cref="CategoryRank"/>, so the diagram reads the same every time). Power supply loss comes first so it leaves the
    /// source first. Streams of one category keep the tree's own order.
    /// </summary>
    private static IReadOnlyList<PowerNode> Drawable(PowerNode n, Func<PowerNode, double> w) =>
        n.Children.Where(c => c.HasValue && w(c) > 0 && double.IsFinite(w(c)))
            .OrderBy(c => c.Id == PsuLossId ? -1 : CategoryRank(c.Category))
            .ToList();

    /// <summary>Place of a category among its siblings, top first. The battery sink sits below the parts of the PC.</summary>
    public static int CategoryRank(PowerCategory category) => category switch
    {
        PowerCategory.PsuLoss => 0,
        PowerCategory.Cpu => 1,
        PowerCategory.Gpu => 2,
        PowerCategory.Memory => 3,
        PowerCategory.Storage => 4,
        PowerCategory.Fans => 5,
        PowerCategory.Motherboard => 6,
        PowerCategory.Peripherals => 7,
        PowerCategory.Battery => 8,
        PowerCategory.Unaccounted => 9,
        _ => 10,
    };

    /// <summary>
    /// Splits <paramref name="total"/> pixels in proportion to <paramref name="watts"/>, giving no share less than
    /// <paramref name="min"/> and scaling the rest down so the shares still add up to the total.
    /// </summary>
    public static double[] Distribute(IReadOnlyList<double> watts, double total, double min)
    {
        int n = watts.Count;
        var result = new double[n];
        if (n == 0 || !(total > 0)) return result;
        if (min * n >= total)
        {
            Array.Fill(result, total / n);
            return result;
        }
        var pinned = new bool[n];
        for (int round = 0; round <= n; round++)
        {
            int count = pinned.Count(p => p);
            double free = total - min * count;
            double freeWatts = 0;
            for (int i = 0; i < n; i++) if (!pinned[i]) freeWatts += Math.Max(0, watts[i]);
            bool again = false;
            for (int i = 0; i < n; i++)
            {
                if (pinned[i]) { result[i] = min; continue; }
                result[i] = freeWatts > 0 ? free * Math.Max(0, watts[i]) / freeWatts : free / (n - count);
            }
            for (int i = 0; i < n; i++)
                if (!pinned[i] && result[i] < min - 1e-9) { pinned[i] = true; again = true; }
            if (!again) break;
        }
        return result;
    }

    /// <summary>Opens a closed stream or closes an open one. Returns true when it is open afterwards.</summary>
    public static bool Toggle(ISet<string> expanded, string id)
    {
        if (expanded.Remove(id)) return false;
        expanded.Add(id);
        return true;
    }

    /// <summary>Drops ids that are no longer in the tree, so a part that went away does not reopen by itself later.</summary>
    public static void Prune(ISet<string> expanded, PowerNode root)
    {
        HashSet<string> live = [.. root.Descendants().Select(n => n.Id)];
        foreach (string id in expanded.Where(id => !live.Contains(id)).ToList()) expanded.Remove(id);
    }

    /// <summary>
    /// Moves label centres apart so no two lines overlap, keeping their order and staying between
    /// <paramref name="top"/> and <paramref name="bottom"/> when there is room.
    /// </summary>
    public static double[] SpreadLabels(IReadOnlyList<double> centers, double lineHeight, double top, double bottom)
    {
        int n = centers.Count;
        double[] y = [.. centers];
        if (n == 0) return y;
        int[] order = Enumerable.Range(0, n).OrderBy(i => centers[i]).ToArray();
        double half = lineHeight / 2;
        double prev = double.NegativeInfinity;
        foreach (int i in order)
        {
            y[i] = Math.Max(Math.Max(y[i], top + half), prev + lineHeight);
            prev = y[i];
        }
        // Pushed past the bottom: walk back up, still keeping a line between neighbours.
        double next = double.PositiveInfinity;
        for (int j = n - 1; j >= 0; j--)
        {
            int i = order[j];
            double limit = Math.Min(bottom - half, next - lineHeight);
            if (y[i] > limit) y[i] = limit;
            next = y[i];
        }
        return y;
    }

    private static void SpreadMouthLabels(List<RiverStream> streams, double top, double bottom)
    {
        var idx = new List<int>();
        for (int i = 0; i < streams.Count; i++)
            if (streams[i].Depth > 0 && !streams[i].Expanded) idx.Add(i);
        if (idx.Count == 0) return;
        double line = streams[idx[0]].Label.Height + 2;
        double[] spread = SpreadLabels(idx.Select(i => streams[i].Label.CenterY).ToArray(), line, top, bottom);
        // Only push down when there is room; never above the first line.
        for (int k = 0; k < idx.Count; k++)
        {
            RiverStream s = streams[idx[k]];
            streams[idx[k]] = s with { Label = s.Label with { CenterY = spread[k] } };
        }
    }

    private static RiverStream Straight(PowerNode node, string? parentId, int depth, int index, double t, double x0, double x1, double y,
        double? total, bool open, bool hasChildren, RiverLabel label)
    {
        var a = new RiverPoint(x0, y);
        var b = new RiverPoint(x1, y);
        var c = new RiverPoint(x1, y + t);
        var d = new RiverPoint(x0, y + t);
        double mid = y + t / 2;
        return new RiverStream
        {
            Node = node, ParentId = parentId, Depth = depth, Index = index, Thickness = t,
            Percent = node.HasValue && total is double tw ? node.Watts!.Value / tw * 100.0 : null,
            HasChildren = hasChildren || node.Children.Count > 0, Expanded = open,
            StartX = x0, EndX = x1, CenterY = mid,
            Outline = new RiverPath(a, [Line(a, b), Line(b, c), Line(c, d), Line(d, a)], true),
            TopBank = new RiverPath(a, [Line(a, b)], false),
            BottomBank = new RiverPath(d, [Line(d, c)], false),
            Centerline = new RiverPath(new RiverPoint(x0, mid), [Line(new RiverPoint(x0, mid), new RiverPoint(x1, mid))], false),
            CenterlineLength = x1 - x0,
            Label = label,
        };
    }

    private static RiverStream Branch(PowerNode node, string parentId, int depth, int index, double t, double x0, double y0, double x1, double y1,
        double xEnd, bool open, double? total, bool hasChildren, RiverLabel label, RiverOptions o)
    {
        // The split: an S-curve from the slot in the parent to the stream's own room.
        RiverCubic SCurve(RiverPoint from, RiverPoint to)
        {
            double dx = (to.X - from.X) * 0.5;
            return new RiverCubic(new RiverPoint(from.X + dx, from.Y), new RiverPoint(to.X - dx, to.Y), to);
        }
        double mid1 = y1 + t / 2;
        // A stream that ends narrows a little towards its mouth and gets a rounded end; an open one runs on at full width.
        double tail = open ? t : Math.Max(Math.Min(t, o.MinThickness), t * o.TailTaper);
        var topA = new RiverPoint(x0, y0);
        var topB = new RiverPoint(x1, y1);
        var topC = new RiverPoint(xEnd, mid1 - tail / 2);
        var botA = new RiverPoint(x0, y0 + t);
        var botB = new RiverPoint(x1, y1 + t);
        var botC = new RiverPoint(xEnd, mid1 + tail / 2);
        double run = xEnd - x1;
        RiverCubic Calm(RiverPoint from, RiverPoint to, double bow) =>
            new(new RiverPoint(from.X + run / 3, from.Y + bow), new RiverPoint(to.X - run / 3, to.Y + bow), to);
        // A slight bow in the calm part, outwards on both banks, so the water does not look ruled.
        double bow = open ? 0 : Math.Min(2.0, t * 0.08);

        var top = new List<RiverCubic> { SCurve(topA, topB), Calm(topB, topC, -bow) };
        var bottom = new List<RiverCubic> { SCurve(botA, botB), Calm(botB, botC, bow) };
        var outline = new List<RiverCubic>(top);
        if (open) outline.Add(Line(topC, botC));
        else
        {
            double r = Math.Min(tail * 0.7, 12);
            outline.Add(new RiverCubic(new RiverPoint(xEnd + r, topC.Y), new RiverPoint(xEnd + r, botC.Y), botC));
        }
        outline.Add(Reverse(botB, Calm(botB, botC, bow)));
        outline.Add(Reverse(botA, SCurve(botA, botB)));
        outline.Add(Line(botA, topA));

        var cA = new RiverPoint(x0, y0 + t / 2);
        var cB = new RiverPoint(x1, mid1);
        var cC = new RiverPoint(xEnd, mid1);
        double length = Math.Sqrt((x1 - x0) * (x1 - x0) + (mid1 - cA.Y) * (mid1 - cA.Y)) + (xEnd - x1);
        return new RiverStream
        {
            Node = node, ParentId = parentId, Depth = depth, Index = index, Thickness = t,
            Percent = node.HasValue && total is double tw ? node.Watts!.Value / tw * 100.0 : null,
            HasChildren = hasChildren, Expanded = open, Evaporates = node.Id == PsuLossId,
            StartX = x0, EndX = xEnd, CenterY = mid1,
            Outline = new RiverPath(topA, outline, true),
            TopBank = new RiverPath(topA, top, false),
            BottomBank = new RiverPath(botA, bottom, false),
            Centerline = new RiverPath(cA, [SCurve(cA, cB), Line(cB, cC)], false),
            CenterlineLength = length,
            Label = label,
        };
    }

    private static RiverCubic Line(RiverPoint a, RiverPoint b) =>
        new(new RiverPoint(a.X + (b.X - a.X) / 3, a.Y + (b.Y - a.Y) / 3), new RiverPoint(a.X + 2 * (b.X - a.X) / 3, a.Y + 2 * (b.Y - a.Y) / 3), b);

    /// <summary>The same piece walked backwards, ending at <paramref name="start"/>.</summary>
    private static RiverCubic Reverse(RiverPoint start, RiverCubic c) => new(c.C2, c.C1, start);
}
