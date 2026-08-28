using FluentAssertions;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

public sealed class RefreshRateTests
{
    [Theory]
    [InlineData(60, 90)]
    [InlineData(90, 120)]
    [InlineData(120, 60)]
    [InlineData(61, 90)]
    public void NextRate_CyclesAscendingAndWraps(int current, int expected) =>
        RefreshRate.NextRate(current, [120, 60, 90, 90, 1]).Should().Be(expected);

    [Fact]
    public void NextRate_EmptySet_ReturnsNull() =>
        RefreshRate.NextRate(60, [0, 1]).Should().BeNull();

    [Theory]
    [InlineData(new[] { 60 }, false)]
    [InlineData(new[] { 60, 120 }, true)]
    [InlineData(new[] { 60, 60, 1 }, false)]
    public void HasMultipleRates_RequiresTwoDistinctRealRates(int[] rates, bool expected) =>
        RefreshRate.HasMultipleRates(rates).Should().Be(expected);
}
