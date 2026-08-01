using FluentAssertions;
using XiControl.Config;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Мёртвая зона тачпада (XIC-24) — чистая логика: пересчёт мм в единицы PTP и приведение
/// значения из config.json. Саму запись в реестр и перезапуск узла юнитами не трогаем.
/// </summary>
public sealed class TouchpadDeadZoneTests
{
    [Theory]
    [InlineData(8, 800)]
    [InlineData(12, 1200)]
    [InlineData(20, 2000)]
    public void ToHimetric_IsMillimetersTimesHundred(int mm, int expected)
    {
        TouchpadDeadZone.ToHimetric(mm).Should().Be(expected, "единицы настройки — сотые доли мм");
    }

    [Theory]
    [InlineData(12, 12)]   // обычное значение проходит как есть
    [InlineData(25, 25)]   // нестандартное из config.json уважаем — как нестандартную частоту на «Экране»
    [InlineData(0, 1)]     // ноль выключил бы зону молча, оставляя опцию включённой
    [InlineData(-5, 1)]
    [InlineData(200, 40)]  // 200 мм «съели» бы всю панель — выглядело бы как сломанный тачпад
    public void NormalizeMm_ClampsToSaneRange(int input, int expected)
    {
        TouchpadDeadZone.NormalizeMm(input).Should().Be(expected);
    }

    [Fact]
    public void Presets_AreAscendingAndContainDefault()
    {
        TouchpadDeadZone.PresetsMm.Should().BeInAscendingOrder().And.Contain(12);
    }

    [Fact]
    public void DeadZone_IsOffByDefault_WithTwelveMillimeters()
    {
        var cfg = new AppConfig();

        cfg.TouchpadDeadZone.Should().BeFalse("опция пишет машинную настройку Windows — сама не включается");
        cfg.TouchpadDeadZoneMm.Should().Be(12);
    }
}
