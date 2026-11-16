using Portal.Core.Power;
using Portal.Core.Power.Dashboard;
using Xunit;

namespace Portal.Core.Tests;

public class RiverLayoutTests
{
    private static PowerNode Tree(double cores = 50, double gpu = 120) => PowerNodes.Group("pc", "Whole PC", PowerCategory.Total,
    [
        PowerNodes.Leaf("psu.loss", "Power supply loss", PowerCategory.PsuLoss, 18, PowerQuality.Estimated),
        PowerNodes.Group("cpu", "CPU", PowerCategory.Cpu,
        [
            PowerNodes.Leaf("cpu.cores", "Cores", PowerCategory.Cpu, cores, PowerQuality.Measured),
            PowerNodes.Leaf("cpu.soc", "SoC", PowerCategory.Cpu, 8, PowerQuality.Measured),
            PowerNodes.Leaf("cpu.other", "Other", PowerCategory.Cpu, 0.4, PowerQuality.Estimated),
        ]),
        PowerNodes.Group("gpu", "Graphics", PowerCategory.Gpu,
        [
            PowerNodes.Group("gpu.0", "Card", PowerCategory.Gpu,
            [
                PowerNodes.Leaf("gpu.0.core", "Core", PowerCategory.Gpu, gpu, PowerQuality.Measured),
                PowerNodes.Leaf("gpu.0.memory", "Memory", PowerCategory.Gpu, 20, PowerQuality.Measured),
            ]),
        ]),
        PowerNodes.Leaf("memory", "Memory", PowerCategory.Memory, 6, PowerQuality.Estimated),
        PowerNode.Unavailable("fans", "Fans", PowerCategory.Fans),
        PowerNodes.Leaf("storage", "Storage", PowerCategory.Storage, 0, PowerQuality.Estimated),
    ]);

    [Fact]
    public void Distribute_SumsToTheParent_AndKeepsTheMinimum()
    {
        double[] t = RiverLayout.Distribute([1000, 1, 1], 100, 3);
        Assert.Equal(100, t.Sum(), 6);
        Assert.Equal(3, t[1], 6);
        Assert.Equal(3, t[2], 6);
        Assert.Equal(94, t[0], 6);

        double[] plain = RiverLayout.Distribute([60, 30, 10], 100, 3);
        Assert.Equal([60.0, 30.0, 10.0], plain.Select(v => Math.Round(v, 6)).ToArray());

        // Not enough room for every minimum: equal shares that still add up.
        double[] tight = RiverLayout.Distribute([5, 1, 1, 1], 8, 3);
        Assert.All(tight, v => Assert.Equal(2, v, 6));
        Assert.Empty(RiverLayout.Distribute([], 100, 3));
        Assert.All(RiverLayout.Distribute([1, 2], 0, 3), v => Assert.Equal(0, v));
    }

    [Fact]
    public void Streams_SumToTheirParent_AtEveryLevel()
    {
        var expanded = new HashSet<string> { "cpu", "gpu", "gpu.0" };
        RiverLayoutResult r = RiverLayout.Compute(Tree(), expanded, new RiverOptions { SourceThickness = 120, MinThickness = 3 });
        Assert.False(r.IsEmpty);
        foreach (RiverStream parent in r.Streams.Where(s => s.Expanded))
        {
            var kids = r.Streams.Where(s => s.ParentId == parent.Id).ToList();
            Assert.NotEmpty(kids);
            Assert.Equal(parent.Thickness, kids.Sum(k => k.Thickness), 6);
            Assert.All(kids, k => Assert.True(k.Thickness >= 3 - 1e-9 || kids.Count * 3 > parent.Thickness));
            // Children leave the parent edge to edge, in order.
            Assert.Equal(kids.OrderBy(k => k.Index).Select(k => k.Id), kids.OrderBy(k => k.Outline.Start.Y).Select(k => k.Id));
        }
        Assert.Equal(120, r.Streams[0].Thickness);
        Assert.Equal("psu.loss", r.Streams.First(s => s.Depth == 1).Id);
        Assert.True(r.Find("psu.loss")!.Evaporates);
    }

    [Fact]
    public void ZeroAndUnavailableParts_GetNoStream()
    {
        RiverLayoutResult r = RiverLayout.Compute(Tree(), new HashSet<string>());
        Assert.Null(r.Find("fans"));
        Assert.Null(r.Find("storage"));
        Assert.NotNull(r.Find("memory"));
        Assert.False(r.Find("memory")!.HasChildren);
        Assert.True(r.Find("cpu")!.HasChildren);
        Assert.False(r.Find("cpu")!.Expanded);
        Assert.Null(r.Find("cpu.cores"));

        Assert.True(RiverLayout.Compute(PowerNode.Unavailable("pc", "Whole PC", PowerCategory.Total), new HashSet<string>()).IsEmpty);
        PowerNode allZero = PowerNodes.Group("pc", "Whole PC", PowerCategory.Total, [PowerNodes.Leaf("cpu", "CPU", PowerCategory.Cpu, 0, PowerQuality.Estimated)]);
        Assert.True(RiverLayout.Compute(allZero, new HashSet<string>()).IsEmpty);

        // A part whose children are all unknown cannot be opened.
        PowerNode unknownKids = PowerNodes.Group("pc", "Whole PC", PowerCategory.Total,
        [
            new PowerNode { Id = "fans", Name = "Fans", Category = PowerCategory.Fans, Watts = 4, Quality = PowerQuality.Estimated,
                Children = [PowerNode.Unavailable("fan.1", "Fan 1", PowerCategory.Fans)] },
        ]);
        RiverLayoutResult u = RiverLayout.Compute(unknownKids, new HashSet<string> { "fans" });
        Assert.False(u.Find("fans")!.HasChildren);
        Assert.False(u.Find("fans")!.Expanded);
    }

    [Fact]
    public void ExpandState_SurvivesATick_AndToggleCloses()
    {
        var expanded = new HashSet<string>();
        Assert.True(RiverLayout.Toggle(expanded, "cpu"));
        RiverLayoutResult first = RiverLayout.Compute(Tree(cores: 50), expanded);
        RiverLayout.Prune(expanded, Tree(cores: 75));
        RiverLayoutResult second = RiverLayout.Compute(Tree(cores: 75), expanded);
        Assert.NotNull(first.Find("cpu.cores"));
        Assert.NotNull(second.Find("cpu.cores"));
        Assert.True(second.Find("cpu")!.Expanded);
        Assert.True(second.Find("cpu.cores")!.Thickness > first.Find("cpu.cores")!.Thickness);

        RiverLayout.Prune(expanded, PowerNodes.Group("pc", "Whole PC", PowerCategory.Total, [PowerNodes.Leaf("memory", "Memory", PowerCategory.Memory, 5, PowerQuality.Estimated)]));
        Assert.Empty(expanded);
        expanded.Add("cpu");
        Assert.False(RiverLayout.Toggle(expanded, "cpu"));
        Assert.Null(RiverLayout.Compute(Tree(), expanded).Find("cpu.cores"));
    }

    [Fact]
    public void ShownWatts_DriveTheWidths()
    {
        var shown = new Dictionary<string, double> { ["pc"] = 200, ["gpu"] = 20, ["cpu"] = 150 };
        RiverLayoutResult r = RiverLayout.Compute(Tree(), new HashSet<string>(), null, shown);
        Assert.True(r.Find("cpu")!.Thickness > r.Find("gpu")!.Thickness);
        // Labels still read the real values.
        Assert.Equal(r.Find("gpu")!.Node.Watts!.Value / Tree().Watts!.Value * 100, r.Find("gpu")!.Percent!.Value, 6);
    }

    [Fact]
    public void Labels_DoNotOverlap_ForTwentyParts()
    {
        var rnd = new Random(7);
        var parts = Enumerable.Range(0, 12).Select(i => PowerNodes.Leaf("p" + i, "Part " + i, PowerCategory.Peripherals, i == 0 ? 300 : rnd.NextDouble() * 4 + 0.1, PowerQuality.Estimated)).ToList();
        var drives = Enumerable.Range(0, 8).Select(i => PowerNodes.Leaf("d" + i, "Drive " + i, PowerCategory.Storage, i % 3 == 0 ? 6 : 0.3, PowerQuality.Estimated)).ToList();
        parts.Add(PowerNodes.Group("storage", "Storage", PowerCategory.Storage, drives));
        PowerNode root = PowerNodes.Group("pc", "Whole PC", PowerCategory.Total, parts);
        RiverLayoutResult r = RiverLayout.Compute(root, new HashSet<string> { "storage" }, new RiverOptions { SourceThickness = 160 });

        var labels = r.Streams.Where(s => s.Depth > 0).Select(s => s.Label).ToList();
        Assert.Equal(20, r.Streams.Count(s => s.Depth > 0 && !s.Expanded));
        var mouth = r.Streams.Where(s => s.Depth > 0 && !s.Expanded).Select(s => s.Label).OrderBy(l => l.CenterY).ToList();
        for (int i = 1; i < mouth.Count; i++) Assert.True(mouth[i].Top >= mouth[i - 1].Bottom - 1e-6, $"labels {i - 1} and {i} overlap");
        Assert.All(labels, l => Assert.InRange(l.CenterY, 0, r.Height));
        Assert.All(r.Streams, s => Assert.True(s.EndX <= r.Width));
    }

    [Fact]
    public void SpreadLabels_KeepsOrderAndRoom()
    {
        double[] y = RiverLayout.SpreadLabels([10, 11, 12, 50, 49], 20, 0, 400);
        double[] sorted = [.. y.OrderBy(v => v)];
        for (int i = 1; i < sorted.Length; i++) Assert.True(sorted[i] - sorted[i - 1] >= 20 - 1e-9);
        Assert.True(y[0] < y[1] && y[1] < y[2]);
        Assert.True(y.Min() >= 10);
        double[] squeezed = RiverLayout.SpreadLabels([95, 96, 97], 20, 0, 100);
        Assert.True(squeezed.Max() <= 90 + 1e-9);
    }

    [Fact]
    public void NarrowWidth_ScrollsInsteadOfSquashing()
    {
        var o = new RiverOptions { Width = 300, MinColumnWidth = 110 };
        RiverLayoutResult r = RiverLayout.Compute(Tree(), new HashSet<string> { "gpu", "gpu.0" }, o);
        Assert.True(r.Width > 300);
        RiverLayoutResult wide = RiverLayout.Compute(Tree(), new HashSet<string>(), new RiverOptions { Width = 1000 });
        Assert.True(wide.Width <= 1000 + 1e-6);
    }

    private static readonly string[] FixedOrder = ["psu.loss", "cpu", "gpu", "memory", "storage", "fans", "board", "usb", "battery", "unaccounted"];

    private static PowerNode Part(string id, PowerCategory c, double w) => PowerNodes.Leaf(id, id, c, w, PowerQuality.Estimated);

    private static PowerNode AllParts(Random? shuffle = null, double scale = 1)
    {
        List<PowerNode> parts =
        [
            Part("unaccounted", PowerCategory.Unaccounted, 3 * scale), Part("battery", PowerCategory.Battery, 15 * scale),
            Part("usb", PowerCategory.Peripherals, 5 * scale), Part("board", PowerCategory.Motherboard, 20 * scale),
            Part("fans", PowerCategory.Fans, 2 * scale), Part("storage", PowerCategory.Storage, 4 * scale),
            Part("memory", PowerCategory.Memory, 8 * scale), Part("gpu", PowerCategory.Gpu, 90 * scale),
            Part("cpu", PowerCategory.Cpu, 60 * scale), Part("psu.loss", PowerCategory.PsuLoss, 12 * scale),
        ];
        if (shuffle is not null) parts = [.. parts.OrderBy(_ => shuffle.Next())];
        return PowerNodes.Group("pc", "Whole PC", PowerCategory.Total, parts);
    }

    [Fact]
    public void CollapsedStreams_AlwaysComeInTheSameOrder()
    {
        string[]? first = null;
        for (int seed = 1; seed <= 6; seed++)
        {
            RiverLayoutResult r = RiverLayout.Compute(AllParts(new Random(seed), 1 + seed), new HashSet<string>());
            string[] order = [.. r.Streams.Where(s => s.Depth == 1).OrderBy(s => s.CenterY).Select(s => s.Id)];
            Assert.Equal(FixedOrder, order);
            Assert.Equal(FixedOrder, r.Streams.Where(s => s.Depth == 1).OrderBy(s => s.Index).Select(s => s.Id));
            first ??= order;
        }
        Assert.NotNull(first);
    }

    [Fact]
    public void CollapsedLayout_IsRepeatable_AndTheOrderSurvivesWattsChanging()
    {
        RiverLayoutResult a = RiverLayout.Compute(AllParts(), new HashSet<string>());
        RiverLayoutResult b = RiverLayout.Compute(AllParts(), new HashSet<string>());
        Assert.Equal(a.Streams.Select(s => (s.Id, s.CenterY, s.Thickness)), b.Streams.Select(s => (s.Id, s.CenterY, s.Thickness)));

        // A different mix of watts moves the sizes, never the order.
        PowerNode other = PowerNodes.Group("pc", "Whole PC", PowerCategory.Total, AllParts().Children.Select(c => c.Id == "cpu" ? Part("cpu", PowerCategory.Cpu, 400) : c).ToList());
        RiverLayoutResult c2 = RiverLayout.Compute(other, new HashSet<string>());
        Assert.Equal(FixedOrder, c2.Streams.Where(s => s.Depth == 1).OrderBy(s => s.CenterY).Select(s => s.Id));
    }

    [Fact]
    public void ExpandingAStream_KeepsTheOtherStreamsInOrder()
    {
        PowerNode tree = PowerNodes.Group("pc", "Whole PC", PowerCategory.Total,
        [
            Part("storage", PowerCategory.Storage, 6),
            PowerNodes.Group("cpu", "CPU", PowerCategory.Cpu, [Part("cpu.cores", PowerCategory.Cpu, 40), Part("cpu.soc", PowerCategory.Cpu, 10)]),
            Part("psu.loss", PowerCategory.PsuLoss, 8),
            Part("memory", PowerCategory.Memory, 7),
        ]);
        string[] closed = [.. RiverLayout.Compute(tree, new HashSet<string>()).Streams.Where(s => s.Depth == 1).OrderBy(s => s.CenterY).Select(s => s.Id)];
        RiverLayoutResult open = RiverLayout.Compute(tree, new HashSet<string> { "cpu" });
        string[] opened = [.. open.Streams.Where(s => s.Depth == 1).OrderBy(s => s.CenterY).Select(s => s.Id)];
        Assert.Equal(["psu.loss", "cpu", "memory", "storage"], closed);
        Assert.Equal(closed, opened);
        Assert.Equal(2, open.Streams.Count(s => s.ParentId == "cpu"));
    }

    private static PowerTreeSnapshot Laptop(double? batteryW, WallReading? wall = null) => PowerReconciler.Reconcile(
        [PowerNodes.Leaf("cpu", "CPU", PowerCategory.Cpu, 20, PowerQuality.Measured), PowerNodes.Leaf("board", "Board", PowerCategory.Motherboard, 6, PowerQuality.Estimated),
         PowerNodes.Leaf("usb", "USB", PowerCategory.Peripherals, 2, PowerQuality.Estimated)],
        PsuMeasurement.None, wall, new PowerModelSettings().Normalize(), new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero), batteryW);

    [Fact]
    public void BatteryCharging_IsASinkStream_BelowTheParts()
    {
        PowerTreeSnapshot s = Laptop(18);
        RiverLayoutResult r = RiverLayout.Compute(s.Root, new HashSet<string>());
        RiverStream battery = r.Find("battery")!;
        Assert.Equal(1, battery.Depth);
        Assert.Equal("pc", battery.ParentId);
        Assert.Equal(PowerCategory.Battery, battery.Node.Category);
        Assert.False(battery.Evaporates);
        Assert.Equal(18 / s.TotalWatts!.Value * 100, battery.Percent!.Value, 4);
        string[] order = [.. r.Streams.Where(x => x.Depth == 1).OrderBy(x => x.CenterY).Select(x => x.Id)];
        Assert.Equal("battery", order[^1]);
        Assert.True(battery.CenterY > r.Find("usb")!.CenterY);
        Assert.Equal(r.Find("pc")!.Thickness, r.Streams.Where(x => x.Depth == 1).Sum(x => x.Thickness), 6);
        Assert.True(battery.Label.X >= r.MouthX);
    }

    [Fact]
    public void OnBattery_TheBatteryIsTheSource_AndThereIsNoSupplyLossStream()
    {
        PowerTreeSnapshot s = Laptop(-22);
        Assert.Equal(PowerModelMode.Battery, s.Mode);
        RiverLayoutResult r = RiverLayout.Compute(s.Root, new HashSet<string>());
        RiverStream source = r.Streams[0];
        Assert.Equal(0, source.Depth);
        Assert.Equal(22, source.Node.Watts!.Value, 1);
        Assert.Equal(0, source.StartX);
        Assert.Null(r.Find("psu.loss"));
        Assert.Null(r.Find("battery"));
        Assert.All(r.Streams.Where(x => x.Depth == 1), x => Assert.True(x.StartX >= source.EndX - 1e-9));
        Assert.Equal(source.Thickness, r.Streams.Where(x => x.Depth == 1).Sum(x => x.Thickness), 6);
    }

    [Fact]
    public void Desktop_LayoutHasNoBatteryStream()
    {
        RiverLayoutResult r = RiverLayout.Compute(Laptop(null).Root, new HashSet<string>());
        Assert.Null(r.Find("battery"));
        Assert.NotNull(r.Find("psu.loss"));
    }

    [Fact]
    public void MouthX_MarksTheEndOfTheWater()
    {
        RiverLayoutResult r = RiverLayout.Compute(Tree(), new HashSet<string>());
        Assert.True(r.MouthX > 0);
        Assert.Equal(r.MouthX, r.Streams.Where(s => !s.Expanded && s.Depth > 0).Max(s => s.EndX), 6);
        Assert.True(r.Width > r.MouthX);
    }

    [Fact]
    public void LiveChips_CoverTheFoldedRow_InAFixedOrder()
    {
        PowerTreeSnapshot s = PowerReconciler.Reconcile(
            [PowerNodes.Leaf("cpu", "CPU", PowerCategory.Cpu, 20, PowerQuality.Measured), PowerNodes.Leaf("gpu0", "GPU", PowerCategory.Gpu, 40, PowerQuality.Measured),
             PowerNodes.Leaf("board", "Board", PowerCategory.Motherboard, 6, PowerQuality.Estimated), PowerNodes.Leaf("usb", "USB", PowerCategory.Peripherals, 4, PowerQuality.Estimated)],
            PsuMeasurement.None, null, new PowerModelSettings().Normalize(), new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero), 10);
        IReadOnlyList<LiveChip> chips = LiveChips.Build(s);
        Assert.Equal([LiveChipKind.Total, LiveChipKind.Cpu, LiveChipKind.Gpu, LiveChipKind.Battery, LiveChipKind.Other], chips.Select(c => c.Kind));
        Assert.Equal(s.TotalWatts, chips[0].Watts);
        Assert.Equal(20, chips[1].Watts);
        Assert.Equal(PowerQuality.Measured, chips[2].Quality);
        Assert.Equal(10, chips[3].Watts);
        Assert.Equal(10, chips[4].Watts);
        Assert.Equal(PowerQuality.Estimated, chips[4].Quality);

        PowerTreeSnapshot discharging = PowerReconciler.Reconcile(
            [PowerNodes.Leaf("cpu", "CPU", PowerCategory.Cpu, 20, PowerQuality.Measured)],
            PsuMeasurement.None, null, new PowerModelSettings().Normalize(), new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero), -9);
        Assert.Equal(-9, LiveChips.Build(discharging).Single(c => c.Kind == LiveChipKind.Battery).Watts);

        PowerTreeSnapshot desktop = Laptop(null);
        Assert.DoesNotContain(LiveChips.Build(desktop), c => c.Kind == LiveChipKind.Battery);
        Assert.Empty(LiveChips.Build(new PowerTreeSnapshot()));
    }

    [Fact]
    public void RiverTint_StartsAtTheAccent_AndDriftsOnlyALittle()
    {
        var accent = new Portal.Core.Theming.Rgb(39, 201, 227);
        Assert.Equal(accent, RiverTint.At(accent, 0));
        Assert.Equal(accent, RiverTint.At(accent, double.NaN));
        double prevL = Portal.Core.Theming.ColorMath.ToHsl(accent).L;
        foreach (double t in new[] { 0.25, 0.5, 0.75, 1 })
        {
            (double h, _, double l) = Portal.Core.Theming.ColorMath.ToHsl(RiverTint.At(accent, t));
            Assert.True(l >= prevL - 1e-9, "water only gets lighter downstream");
            prevL = l;
            double dh = Math.Abs(h - Portal.Core.Theming.ColorMath.ToHsl(accent).H);
            Assert.True(dh <= RiverTint.MaxHueShift + 1.5, $"hue moved {dh}");
        }
        Assert.InRange(prevL - Portal.Core.Theming.ColorMath.ToHsl(accent).L, 0.0, 0.12);
        // A grey accent stays grey.
        var grey = new Portal.Core.Theming.Rgb(128, 128, 128);
        Portal.Core.Theming.Rgb end = RiverTint.At(grey, 1);
        Assert.Equal(end.R, end.G);
        Assert.Equal(end.G, end.B);
    }
}
