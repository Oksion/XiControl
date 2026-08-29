using FluentAssertions;
using XiControl.Config;
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

    [Theory]
    [InlineData(true, 60, 90)]
    [InlineData(false, 120, 90)]
    public void RememberCycleForHold_UpdatesOnlyCurrentPowerProfile(bool online, int unchanged, int selected)
    {
        var store = new CountingStore();
        var cfg = new AppConfig
        {
            RefreshRateFeature = true,
            AutoRefreshRate = true,
            HoldRefreshRate = true,
            AcRefreshRate = 120,
            BatteryRefreshRate = 60,
            Store = store,
        };

        RefreshRate.RememberCycleForHold(cfg, online, selected).Should().BeTrue();

        (online ? cfg.AcRefreshRate : cfg.BatteryRefreshRate).Should().Be(selected);
        (online ? cfg.BatteryRefreshRate : cfg.AcRefreshRate).Should().Be(unchanged);
        store.Saves.Should().Be(1);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void RememberCycleForHold_DisabledPolicyDoesNotRewriteProfiles(
        bool feature, bool automatic, bool hold)
    {
        var store = new CountingStore();
        var cfg = new AppConfig
        {
            RefreshRateFeature = feature,
            AutoRefreshRate = automatic,
            HoldRefreshRate = hold,
            AcRefreshRate = 120,
            Store = store,
        };

        RefreshRate.RememberCycleForHold(cfg, online: true, hz: 90).Should().BeFalse();

        cfg.AcRefreshRate.Should().Be(120);
        store.Saves.Should().Be(0);
    }

    [Theory]
    [InlineData(120)]
    [InlineData(1)]
    [InlineData(0)]
    public void RememberCycleForHold_SameOrInvalidRateDoesNotSave(int selected)
    {
        var store = new CountingStore();
        var cfg = new AppConfig
        {
            RefreshRateFeature = true,
            AutoRefreshRate = true,
            HoldRefreshRate = true,
            AcRefreshRate = 120,
            Store = store,
        };

        RefreshRate.RememberCycleForHold(cfg, online: true, selected).Should().BeFalse();

        cfg.AcRefreshRate.Should().Be(120);
        store.Saves.Should().Be(0);
    }

    private sealed class CountingStore : IConfigStore
    {
        public int Saves { get; private set; }

        public AppConfig Load() => throw new NotSupportedException();
        public void Save(AppConfig cfg) => Saves++;
    }
}
