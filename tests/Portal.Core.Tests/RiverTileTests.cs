using Portal.Core.Power;
using Portal.Core.Power.Dashboard;
using Xunit;

namespace Portal.Core.Tests;

public sealed class RiverTileTests
{
    private static PowerNode Tree() => PowerNodes.Group("pc", "Whole PC", PowerCategory.Total,
    [
        PowerNodes.Leaf(RiverLayout.PsuLossId, "PSU loss", PowerCategory.PsuLoss, 12, PowerQuality.Estimated),
        PowerNodes.Group("cpu", "CPU", PowerCategory.Cpu, [PowerNodes.Leaf("cpu.core", "Cores", PowerCategory.Cpu, 40, PowerQuality.Measured)]),
        PowerNodes.Leaf("gpu", "GPU", PowerCategory.Gpu, 60, PowerQuality.Measured),
        PowerNodes.Leaf("mem", "Memory", PowerCategory.Memory, 6, PowerQuality.Estimated),
        PowerNodes.Leaf("disk", "Storage", PowerCategory.Storage, 4, PowerQuality.Estimated),
        PowerNodes.Leaf("fans", "Fans", PowerCategory.Fans, 3, PowerQuality.Estimated),
        PowerNode.Unavailable("board", "Board", PowerCategory.Motherboard),
    ]);

    [Fact]
    public void Keeps_the_biggest_parts_and_sums_the_rest_into_other()
    {
        PowerNode trimmed = RiverTile.Trim(Tree(), 3, includePsuLoss: true, "Other");
        Assert.Equal(new[] { "gpu", "cpu", RiverTile.OtherId }, trimmed.Children.Select(c => c.Id));
        Assert.Equal(12 + 6 + 4 + 3, trimmed.Children[2].Watts);
        Assert.Equal(125, trimmed.Watts);           // the total stays the whole PC
        Assert.All(trimmed.Children, c => Assert.Empty(c.Children));
    }

    [Fact]
    public void Can_leave_out_the_power_supply_loss()
    {
        PowerNode trimmed = RiverTile.Trim(Tree(), 5, includePsuLoss: false, "Other");
        Assert.DoesNotContain(trimmed.Children, c => c.Id == RiverLayout.PsuLossId);
        Assert.Equal(new[] { "gpu", "cpu", "mem", "disk", "fans" }, trimmed.Children.Select(c => c.Id));
    }

    [Fact]
    public void An_unknown_total_gives_no_streams()
    {
        PowerNode trimmed = RiverTile.Trim(PowerNode.Unavailable("pc", "Whole PC", PowerCategory.Total), 3, true, "Other");
        Assert.Empty(trimmed.Children);
        Assert.True(RiverLayout.Compute(trimmed, new HashSet<string>()).IsEmpty);
    }
}
