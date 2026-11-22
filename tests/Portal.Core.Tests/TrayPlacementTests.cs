using Portal.Core.Tray;
using Xunit;

namespace Portal.Core.Tests;

public class TrayPlacementTests
{
    private const int Gap = 8;
    private static readonly PxRect Work = new(0, 0, 1920, 1040);

    [Fact]
    public void BottomTaskbarOpensAboveTheIcon()
    {
        var icon = new PxRect(900, 1040, 930, 1080);
        PxRect r = TrayPlacement.Place(icon, Work, 400, 300, Gap);
        Assert.Equal(TaskbarEdge.Bottom, TrayPlacement.EdgeOf(icon, Work));
        Assert.Equal(1040 - Gap - 300, r.Top);
        Assert.Equal(1040 - Gap, r.Bottom);
        Assert.Equal(915 - 200, r.Left);
        Assert.Equal(1920 - Gap - 400, TrayPlacement.Place(new PxRect(1800, 1040, 1830, 1080), Work, 400, 300, Gap).Left);
    }

    [Fact]
    public void TopTaskbarOpensBelowTheIcon()
    {
        var work = new PxRect(0, 40, 1920, 1080);
        var icon = new PxRect(1000, 0, 1030, 40);
        PxRect r = TrayPlacement.Place(icon, work, 400, 300, Gap);
        Assert.Equal(TaskbarEdge.Top, TrayPlacement.EdgeOf(icon, work));
        Assert.Equal(40 + Gap, r.Top);
    }

    [Fact]
    public void LeftTaskbarOpensBesideTheIcon()
    {
        var work = new PxRect(60, 0, 1920, 1080);
        var icon = new PxRect(10, 500, 50, 540);
        PxRect r = TrayPlacement.Place(icon, work, 400, 300, Gap);
        Assert.Equal(TaskbarEdge.Left, TrayPlacement.EdgeOf(icon, work));
        Assert.Equal(60 + Gap, r.Left);
        Assert.Equal(520 - 150, r.Top);
    }

    [Fact]
    public void RightTaskbarOpensBesideTheIcon()
    {
        var work = new PxRect(0, 0, 1860, 1080);
        var icon = new PxRect(1870, 500, 1910, 540);
        PxRect r = TrayPlacement.Place(icon, work, 400, 300, Gap);
        Assert.Equal(TaskbarEdge.Right, TrayPlacement.EdgeOf(icon, work));
        Assert.Equal(1860 - Gap, r.Right);
    }

    [Fact]
    public void SecondMonitorWithOffsetStaysOnThatMonitor()
    {
        var work = new PxRect(-1920, 0, 0, 1040);
        var icon = new PxRect(-40, 1040, -10, 1080);
        PxRect r = TrayPlacement.Place(icon, work, 400, 300, Gap);
        Assert.True(r.Left >= -1920 + Gap);
        Assert.Equal(0 - Gap, r.Right);
        Assert.Equal(1040 - Gap, r.Bottom);
    }

    [Fact]
    public void TallContentIsClampedToTheWorkArea()
    {
        var icon = new PxRect(1800, 1040, 1830, 1080);
        PxRect r = TrayPlacement.Place(icon, Work, 400, 5000, Gap);
        Assert.Equal(Gap, r.Top);
        Assert.Equal(1040 - Gap, r.Bottom);
        Assert.Equal(1040 - 2 * Gap, r.Height);
    }

    [Fact]
    public void IconInsideWorkAreaOpensAboveAndFlipsBelowWhenCramped()
    {
        var icon = new PxRect(900, 20, 930, 50);
        PxRect r = TrayPlacement.Place(icon, Work, 400, 300, Gap);
        Assert.Equal(TaskbarEdge.None, TrayPlacement.EdgeOf(icon, Work));
        Assert.Equal(50 + Gap, r.Top);
    }

    [Fact]
    public void WidthStaysBetweenLimitsAndWithinTheRoom()
    {
        Assert.Equal(300, TrayPlacement.WidthDip(100, 2000));
        Assert.Equal(560, TrayPlacement.WidthDip(900, 2000));
        Assert.Equal(350, TrayPlacement.WidthDip(900, 350));
        Assert.Equal(700, TrayPlacement.HeightDip(700, 900));
        Assert.Equal(900, TrayPlacement.HeightDip(1500, 900));
    }

    [Fact]
    public void LayoutRoundTripsAndRepairsBadText()
    {
        var layout = TrayTileLayout.Parse("limit,-heat,bogus,limit");
        Assert.Equal("limit,-heat,battery,topup,health", layout.Serialize());
        Assert.False(layout.IsVisible("heat"));
        Assert.Equal(TrayTileLayout.DefaultOrder, TrayTileLayout.Default().Items.Select(i => i.Id));
    }

    [Fact]
    public void MoveSkipsHiddenTilesAndStopsAtTheEnds()
    {
        var layout = TrayTileLayout.Parse("battery,limit,topup,heat,health");
        string[] shown = { "battery", "limit", "heat", "health" };
        Assert.True(layout.Move("heat", -1, shown));
        Assert.Equal("battery,heat,limit,topup,health", layout.Serialize());
        Assert.False(layout.Move("battery", -1, shown));
    }

    [Fact]
    public void TileViewport_ends_between_whole_rows()
    {
        double[] rows = { 100, 100, 100 };
        // pad 2 + (100+10) + (100+10) = 222 fits; a third row would reach 332
        Assert.Equal(222, TrayPlacement.TileViewportDip(300, rows, 10, 2, 6));
        Assert.Equal(112, TrayPlacement.TileViewportDip(221, rows, 10, 2, 6));
    }

    [Fact]
    public void TileViewport_uses_the_full_content_when_everything_fits()
    {
        Assert.Equal(2 + 6 + 200 + 10, TrayPlacement.TileViewportDip(500, new double[] { 100, 100 }, 10, 2, 6));
        Assert.Equal(8, TrayPlacement.TileViewportDip(500, Array.Empty<double>(), 10, 2, 6));
    }

    [Fact]
    public void TileViewport_keeps_the_available_height_when_not_even_one_row_fits()
    {
        Assert.Equal(80, TrayPlacement.TileViewportDip(80, new double[] { 100, 100 }, 10, 2, 6));
    }
}
