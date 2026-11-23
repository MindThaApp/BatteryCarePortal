using Portal.Core.Tray;
using Xunit;

namespace Portal.Core.Tests;

public class QuietDisplayTests
{
    [Fact]
    public void SingleDisplayPicksNothing() =>
        Assert.Equal(-1, QuietDisplay.PickNonPrimary(new[] { new PxRect(0, 0, 1920, 1080) }));

    [Fact]
    public void SkipsThePrimaryWhateverItsPosition() =>
        Assert.Equal(0, QuietDisplay.PickNonPrimary(new[] { new PxRect(-1920, 0, 0, 1080), new PxRect(0, 0, 1920, 1080) }));

    [Fact]
    public void PicksFirstNonPrimary() =>
        Assert.Equal(1, QuietDisplay.PickNonPrimary(new[] { new PxRect(0, 0, 1920, 1080), new PxRect(1920, 0, 3840, 1080), new PxRect(3840, 0, 5760, 1080) }));

    [Fact]
    public void CentersInWorkArea() =>
        Assert.Equal((2220, 140), QuietDisplay.CenterIn(new PxRect(1920, 0, 3840, 1040), 1320, 760));

    [Fact]
    public void OversizeStaysAtWorkAreaOrigin() =>
        Assert.Equal((1920, 0), QuietDisplay.CenterIn(new PxRect(1920, 0, 3840, 1040), 5000, 5000));
}
