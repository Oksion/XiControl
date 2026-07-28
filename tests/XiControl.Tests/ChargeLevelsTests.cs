using FluentAssertions;
using XiControl.Config;
using XiControl.Wmi;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Уровни порога заряда (MIFS 0x10/0x02) — маппинг код↔% и клэмп конфига.
/// Таблица снята с железа (TM2424) и задокументирована в docs/12-charge-levels.md:
/// прошивка держит {0,1,4,5,6,7,8}, остальное отвергает.
/// </summary>
public sealed class ChargeLevelsTests
{
    [Theory]
    [InlineData(100, 0)]
    [InlineData(80, 1)]   // legacy-код (его же пишет OEM для 80%)
    [InlineData(70, 5)]
    [InlineData(60, 6)]
    [InlineData(50, 7)]
    [InlineData(40, 8)]
    public void ChargeCodeForPercent_MapsSupportedLevels(int percent, byte expected)
        => Mifs.ChargeCodeForPercent(percent).Should().Be(expected);

    [Theory]
    [InlineData(90)]   // код 3 прошивка отвергает — 90% недоступно
    [InlineData(75)]
    [InlineData(0)]
    [InlineData(-10)]
    [InlineData(120)]
    public void ChargeCodeForPercent_RejectsUnsupported(int percent)
        => Mifs.ChargeCodeForPercent(percent).Should().BeNull("прошивка держит только фиксированный набор уровней");

    [Theory]
    [InlineData((byte)0, 100)]
    [InlineData((byte)1, 80)]
    [InlineData((byte)4, 80)]   // granular-дубль legacy-единицы
    [InlineData((byte)5, 70)]
    [InlineData((byte)8, 40)]
    public void ChargePercentForCode_MapsBack(byte code, int expected)
        => Mifs.ChargePercentForCode(code).Should().Be(expected);

    [Theory]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    [InlineData((byte)9)]
    public void ChargePercentForCode_UnknownCode_IsNull(byte code)
        => Mifs.ChargePercentForCode(code).Should().BeNull();

    [Fact]
    public void Presets_AreAllWritable()
        => Mifs.ChargeCarePresets.Should().OnlyContain(p => Mifs.ChargeCodeForPercent(p) != null,
            "каждый пресет UI должен быть записываемым уровнем");

    [Fact]
    public void CarePercent_DefaultsTo80_ForFreshConfig()
        => new AppConfig().CarePercent().Should().Be(80, "дефолт = прежнее поведение (миграция старых config.json)");

    [Fact]
    public void CarePercent_ClampsHandEditedGarbage()
    {
        var cfg = new AppConfig { CareLimitPercent = 73 };   // правка руками мимо набора

        cfg.CarePercent().Should().Be(80, "неподдержанное значение гасим фолбэком, а не пишем в прошивку");
    }

    [Fact]
    public void CarePercent_KeepsSupportedValue()
        => new AppConfig { CareLimitPercent = 60 }.CarePercent().Should().Be(60);
}
