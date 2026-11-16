using Portal.Core.Power;
using Portal.Core.Power.Dashboard;
using Xunit;

namespace Portal.Core.Tests;

public sealed class FlowLayoutTests
{
    private static PowerNode Desktop() => PowerNodes.Group("pc", "Whole PC", PowerCategory.Total,
    [
        PowerNodes.Leaf(RiverLayout.PsuLossId, "PSU loss", PowerCategory.PsuLoss, 12, PowerQuality.Estimated),
        PowerNodes.Group("cpu", "CPU", PowerCategory.Cpu,
        [
            PowerNodes.Leaf("cpu.cores", "Cores", PowerCategory.Cpu, 30, PowerQuality.Measured),
            PowerNodes.Leaf("cpu.soc", "SoC", PowerCategory.Cpu, 10, PowerQuality.Measured),
        ]),
        PowerNodes.Leaf("gpu", "GPU", PowerCategory.Gpu, 60, PowerQuality.Measured),
        PowerNodes.Leaf("mem", "Memory", PowerCategory.Memory, 6, PowerQuality.Estimated),
        PowerNodes.Leaf("disk", "Storage", PowerCategory.Storage, 4, PowerQuality.Estimated),
    ]);

    private static PowerNode Laptop(bool charging) => PowerNodes.Group("pc", "Whole PC", PowerCategory.Total,
        charging
            ? [PowerNodes.Leaf("cpu", "CPU", PowerCategory.Cpu, 8, PowerQuality.Measured), PowerNodes.Leaf("battery", "Battery", PowerCategory.Battery, 20, PowerQuality.Measured)]
            : [PowerNodes.Leaf("cpu", "CPU", PowerCategory.Cpu, 8, PowerQuality.Measured)]);

    [Theory]
    [InlineData(220, 50)]
    [InlineData(300, 60)]
    [InlineData(300, 110)]
    public void CollapsedLabelsReachTheRightEdge(double width, double labelWidth)
    {
        var o = new FlowOptions { Width = width, LabelWidth = labelWidth, MinLabelWidth = Math.Min(60, labelWidth), Padding = 1, StubWidth = 11, LabelGap = 4, Break = 2, SourceWidth = 26, TrunkWidth = 30, SplitWidth = 26, MaxSourceWidth = 92, MaxTrunkWidth = 100 };
        FlowLayoutResult r = FlowLayout.Compute(Desktop(), onBattery: false, new HashSet<string>(), o);
        double right = r.Boxes.Where(b => b.Role == FlowRole.Category).Max(b => b.LabelX + b.LabelWidth);
        Assert.InRange(right, width - 3, width + 0.5);
        Assert.InRange(r.Width, width - 3, width + 0.5);
    }

    [Fact]
    public void Reads_left_to_right_from_the_source_through_the_device_to_the_parts()
    {
        FlowLayoutResult r = FlowLayout.Compute(Desktop(), onBattery: false, new HashSet<string>());
        Assert.Equal(3, r.Columns);
        Assert.Equal(0, r.Find(FlowLayout.MainsId)!.Column);
        Assert.Equal(1, r.Find(FlowLayout.DeviceId)!.Column);
        Assert.Equal(FlowRole.Loss, r.Find(RiverLayout.PsuLossId)!.Role);
        Assert.Equal(new[] { "cpu", "gpu", "mem", "disk" }, r.Boxes.Where(b => b.Role == FlowRole.Category).Select(b => b.Id));
        Assert.All(r.Boxes.Where(b => b.Role == FlowRole.Category), b => Assert.Equal(2, b.Column));
        Assert.Null(r.Find(FlowLayout.BatteryId));
        // The device carries the combined load, the loss included.
        Assert.Equal(Desktop().Watts, r.Find(FlowLayout.DeviceId)!.Watts);
    }

    [Fact]
    public void A_laptop_on_battery_has_only_the_battery_as_its_source()
    {
        FlowLayoutResult r = FlowLayout.Compute(Laptop(charging: false), onBattery: true, new HashSet<string>());
        Assert.True(r.Find(FlowLayout.BatteryId)!.IsBattery);
        Assert.Null(r.Find(FlowLayout.MainsId));
    }

    [Fact]
    public void Battery_charging_is_an_endpoint_below_the_parts()
    {
        FlowLayoutResult r = FlowLayout.Compute(Laptop(charging: true), onBattery: false, new HashSet<string>());
        FlowBox charge = r.Find("battery")!;
        Assert.Equal(FlowRole.Charging, charge.Role);
        Assert.True(charge.IsBattery && charge.IsEndpoint);
        Assert.Equal(FlowLayout.DeviceId, charge.ParentId);
        Assert.Equal(2, charge.Column);
        Assert.True(charge.Y > r.Find("cpu")!.Bottom);
    }

    [Fact]
    public void Opening_a_category_adds_a_stretch_with_its_parts()
    {
        var expanded = new HashSet<string> { "cpu" };
        FlowLayoutResult r = FlowLayout.Compute(Desktop(), onBattery: false, expanded);
        Assert.Equal(4, r.Columns);
        Assert.True(r.Find("cpu")!.Expanded);
        Assert.Equal(new[] { "cpu.cores", "cpu.soc" }, r.Boxes.Where(b => b.ParentId == "cpu").Select(b => b.Id));
        Assert.All(r.Boxes.Where(b => b.ParentId == "cpu"), b => Assert.Equal(3, b.Column));
        // The open part runs on past its name, then the river breaks and splits again.
        FlowRibbon run = r.Ribbons.Single(s => s.Kind == FlowSegmentKind.Stub && s.ToId == "cpu");
        Assert.Equal(0, run.CapEnd);
        FlowRibbon next = r.Ribbons.First(s => s.Kind == FlowSegmentKind.Split && s.FromId == "cpu");
        Assert.Equal(run.X1 + r.Break, next.X0, 3);
        Assert.True(FlowLayout.Toggle(expanded, "gpu"));
        Assert.False(FlowLayout.Toggle(expanded, "cpu"));
    }

    [Fact]
    public void The_river_grows_taller_to_give_open_parts_room_for_their_names()
    {
        PowerNode many = PowerNodes.Group("pc", "Whole PC", PowerCategory.Total,
        [
            PowerNodes.Group("cpu", "CPU", PowerCategory.Cpu, [.. Enumerable.Range(0, 12).Select(i => PowerNodes.Leaf($"cpu.{i}", $"Core {i}", PowerCategory.Cpu, 1, PowerQuality.Measured))]),
            PowerNodes.Leaf("gpu", "GPU", PowerCategory.Gpu, 60, PowerQuality.Measured),
        ]);
        var options = new FlowOptions { MainHeight = 120, RowHeight = 22, RowGap = 6 };
        FlowLayoutResult closed = FlowLayout.Compute(many, false, new HashSet<string>(), options);
        FlowLayoutResult open = FlowLayout.Compute(many, false, new HashSet<string> { "cpu" }, options);
        Assert.True(open.Height > closed.Height);
        FlowBox[] rows = [.. open.Boxes.Where(b => b.ParentId == "cpu").OrderBy(b => b.Y)];
        Assert.Equal(12, rows.Length);
        Assert.All(rows, b => Assert.True(b.Height >= options.RowHeight - 0.001));
        for (int i = 1; i < rows.Length; i++) Assert.True(rows[i].Y - rows[i - 1].Bottom >= options.RowGap - 0.001);
        Assert.True(rows[^1].Bottom <= open.Height);
    }

    [Fact]
    public void A_depth_limit_keeps_parts_closed()
    {
        FlowLayoutResult r = FlowLayout.Compute(Desktop(), onBattery: false, new HashSet<string> { "cpu" }, maxDepth: 1);
        Assert.Equal(3, r.Columns);
        Assert.False(r.Find("cpu")!.Expanded);
        Assert.Null(r.Find("cpu.cores"));
    }

    [Fact]
    public void Fills_the_width_and_keeps_rows_apart()
    {
        foreach (double width in new[] { 900.0, 1200, 1500 })
        {
            FlowLayoutResult r = FlowLayout.Compute(Desktop(), onBattery: false, new HashSet<string> { "cpu" }, new FlowOptions { Width = width });
            Assert.Equal(width, r.Width, 3);
            foreach (IGrouping<int, FlowBox> column in r.Boxes.Where(b => b.IsEndpoint).GroupBy(b => b.Column))
            {
                FlowBox[] rows = [.. column.OrderBy(b => b.Y)];
                for (int i = 1; i < rows.Length; i++) Assert.True(rows[i].Y >= rows[i - 1].Bottom, $"overlap in column {column.Key}");
                Assert.All(rows, b => Assert.True(b.Y >= 0 && b.Bottom <= r.Height + 0.001));
                Assert.All(rows, b => Assert.True(b.FlowTop >= b.Y - 0.001 && b.FlowTop + b.FlowHeight <= b.Bottom + 0.001));
            }
        }
    }

    [Fact]
    public void The_river_is_continuous_across_thin_breaks()
    {
        FlowLayoutResult r = FlowLayout.Compute(Desktop(), onBattery: false, new HashSet<string> { "cpu" });
        FlowRibbon source = r.Ribbons.Single(s => s.Kind == FlowSegmentKind.Source);
        FlowRibbon trunk = r.Ribbons.Single(s => s.Kind == FlowSegmentKind.Trunk);
        FlowRibbon loss = r.Ribbons.Single(s => s.Kind == FlowSegmentKind.Loss);
        Assert.True(source.CapStart > 0);
        Assert.Equal(source.X1 + r.Break, trunk.X0, 3);
        // The trunk and the loss together are exactly as thick as the source.
        Assert.Equal(source.Top1, trunk.Top0, 3);
        Assert.Equal(trunk.Bottom0, loss.Top0, 3);
        Assert.Equal(source.Bottom1, loss.Bottom0, 3);
        Assert.True(loss.Fades && loss.Top1 > loss.Top0);
        foreach (FlowRibbon split in r.Ribbons.Where(s => s.Kind == FlowSegmentKind.Split))
        {
            FlowRibbon stub = r.Ribbons.Single(s => s.Kind == FlowSegmentKind.Stub && s.ToId == split.ToId);
            Assert.Equal(split.X1 + r.Break, stub.X0, 3);
            Assert.Equal(split.Top1, stub.Top0, 3);
            Assert.Equal(split.Bottom1, stub.Bottom0, 3);
            Assert.True(split.Bottom0 > split.Top0 && split.Bottom1 > split.Top1);
        }
        // The splits leave the trunk side by side, filling it exactly.
        FlowRibbon[] first = [.. r.Ribbons.Where(s => s.Kind == FlowSegmentKind.Split && s.FromId == FlowLayout.DeviceId).OrderBy(s => s.Top0)];
        Assert.Equal(trunk.X1 + r.Break, first[0].X0, 3);
        Assert.Equal(trunk.Top1, first[0].Top0, 3);
        for (int i = 1; i < first.Length; i++) Assert.Equal(first[i - 1].Bottom0, first[i].Top0, 3);
        Assert.Equal(trunk.Bottom1, first[^1].Bottom0, 3);
        // Closed parts end in a rounded cap with their name just past it.
        FlowBox gpu = r.Find("gpu")!;
        FlowRibbon gpuEnd = r.Ribbons.Single(s => s.Kind == FlowSegmentKind.Stub && s.ToId == "gpu");
        Assert.True(gpuEnd.CapEnd > 0);
        Assert.True(gpu.LabelX > gpuEnd.X1);
    }

    [Fact]
    public void A_stream_reads_its_top_and_bottom_along_the_way()
    {
        var ribbon = new FlowRibbon { FromId = "a", ToId = "b", X0 = 0, Top0 = 0, Bottom0 = 20, X1 = 100, Top1 = 60, Bottom1 = 70 };
        (double top0, double bottom0) = ribbon.At(0);
        (double top1, double bottom1) = ribbon.At(100);
        (double top, double bottom) = ribbon.At(50);
        Assert.Equal(0, top0, 3);
        Assert.Equal(20, bottom0, 3);
        Assert.Equal(60, top1, 3);
        Assert.Equal(70, bottom1, 3);
        Assert.Equal(30, top, 1);
        Assert.Equal(45, bottom, 1);
    }

    [Fact]
    public void No_total_gives_nothing_to_draw()
    {
        Assert.True(FlowLayout.Compute(PowerNode.Unavailable("pc", "Whole PC", PowerCategory.Total), false, new HashSet<string>()).IsEmpty);
    }

    [Fact]
    public void The_tile_keeps_the_biggest_parts_and_the_side_branches()
    {
        PowerNode trimmed = FlowLayout.TrimForTile(Desktop(), 3, includeLoss: true, "Other");
        Assert.Equal(new[] { "gpu", "cpu", RiverTile.OtherId, RiverLayout.PsuLossId }, trimmed.Children.Select(c => c.Id));
        Assert.Equal(10, trimmed.Children[2].Watts);
        Assert.Equal(Desktop().Watts, trimmed.Watts);
        Assert.DoesNotContain(FlowLayout.TrimForTile(Desktop(), 3, includeLoss: false, "Other").Children, c => c.Id == RiverLayout.PsuLossId);
    }
}
