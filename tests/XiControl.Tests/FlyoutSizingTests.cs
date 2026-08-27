using FluentAssertions;
using XiControl.Ui;
using Xunit;

namespace XiControl.Tests;

public sealed class FlyoutSizingTests
{
    [Theory]
    [InlineData(420, 96, 420)]
    [InlineData(420, 120, 525)]
    [InlineData(420, 144, 630)]
    [InlineData(420, 192, 840)]
    [InlineData(420, 0, 420)]
    public void DipsToPixelsPreservesTheLogicalFlyoutSize(int dips, int dpi, int expected) =>
        ScreenMetrics.DipsToPixels(dips, dpi).Should().Be(expected);

    [Theory]
    [InlineData(0, 0, 1920, 1040, 640, 282, 640, 550)]
    [InlineData(-1920, 0, 1920, 1040, 640, 282, -1280, 550)]
    [InlineData(0, 600, 2400, 720, 640, 282, 880, 894)]
    public void QuickPanelIsCenteredWithItsBottomAtEightyPercent(
        int left, int top, int width, int height, int panelWidth, int panelHeight, int expectedX, int expectedY) =>
        QuickPanelPlacement.ForWorkArea(
                new Rectangle(left, top, width, height), new Size(panelWidth, panelHeight))
            .Should().Be(new Point(expectedX, expectedY));

    [Theory]
    [InlineData(0, 0, 1920, 1040, 510, 64, 705, 488)]
    [InlineData(-1920, 40, 1920, 1040, 136, 54, -1028, 533)]
    [InlineData(100, 200, 500, 300, 700, 400, 100, 200)]
    public void MonitorViewIsCenteredInsideItsCurrentWorkingArea(
        int left, int top, int width, int height, int viewWidth, int viewHeight, int expectedX, int expectedY) =>
        MonitorPlacement.Center(
                new Rectangle(left, top, width, height), new Size(viewWidth, viewHeight))
            .Should().Be(new Point(expectedX, expectedY));

    [Theory]
    [InlineData(null, 460, 600)]
    [InlineData("mini", 510, 64)]
    [InlineData("power", 136, 54)]
    public void MonitorViewsUseTheReferenceAspectRatios(
        string? stored, int expectedWidth, int expectedHeight) =>
        MonitorLayout.ViewSize(MonitorLayout.Parse(stored))
            .Should().Be(new Size(expectedWidth, expectedHeight));

    [Theory]
    [InlineData(null)]
    [InlineData("mini")]
    [InlineData("power")]
    public void MonitorViewConfigRoundTrips(string? stored) =>
        MonitorLayout.ConfigValue(MonitorLayout.Parse(stored)).Should().Be(stored);

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void WindowRegionCanExcludeTheUnpaintedCustomTitlebarScanline(bool clipTop, int expectedTop)
    {
        Rectangle bounds = WindowRegionGeometry.Bounds(805, 1050, clipTop);

        bounds.Should().Be(Rectangle.FromLTRB(0, expectedTop, 806, 1051));
    }
}
