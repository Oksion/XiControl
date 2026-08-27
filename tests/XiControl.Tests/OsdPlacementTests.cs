using FluentAssertions;
using XiControl.Ui;
using Xunit;

namespace XiControl.Tests;

public sealed class OsdPlacementTests
{
    [Fact]
    public void BottomCenter_UsesVisualCenterAtEightyPercent()
    {
        var workingArea = new Rectangle(0, 0, 1920, 1040);

        OsdPlacement.BottomCenter(workingArea, new Size(280, 280))
            .Should().Be(new Point(820, 692));
    }

    [Fact]
    public void BottomCenter_AccountsForOffsetAndNeverLeavesWorkingArea()
    {
        var workingArea = new Rectangle(-1920, 40, 1920, 500);

        OsdPlacement.BottomCenter(workingArea, new Size(280, 600))
            .Should().Be(new Point(-1100, 40));
    }
}
