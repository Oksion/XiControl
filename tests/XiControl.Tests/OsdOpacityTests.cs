using FluentAssertions;
using XiControl.Ui;
using Xunit;

namespace XiControl.Tests;

/// <summary>Прозрачность OSD (XIC-98): зажим ручной правки, базовая непрозрачность и затухание.</summary>
public class OsdOpacityTests
{
    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(40, 40)]
    [InlineData(80, 80)]
    [InlineData(95, 80)]     // правили config.json руками — карточка не должна исчезнуть
    public void Процент_зажимается_в_0_80(int raw, int expected) =>
        OsdOpacity.Clamp(raw).Should().Be(expected);

    [Fact]
    public void По_умолчанию_карточка_непрозрачна() =>
        OsdOpacity.Base(new Config.AppConfig().OsdTransparencyPercent).Should().Be(1.0);

    [Theory]
    [InlineData(40, 0.6)]
    [InlineData(80, 0.2)]
    [InlineData(200, 0.2)]   // выше потолка — как 80%
    public void Базовая_непрозрачность(int percent, double expected) =>
        OsdOpacity.Base(percent).Should().BeApproximately(expected, 1e-9);

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(80)]
    public void Затухание_длится_одинаково_при_любой_прозрачности(int percent)
    {
        // сколько тиков от базовой непрозрачности до нуля — столько же, сколько у непрозрачной
        double b = OsdOpacity.Base(percent);
        (b / OsdOpacity.FadeStep(b)).Should().BeApproximately(1.0 / OsdOpacity.FullFadeStep, 1e-9);
    }
}
