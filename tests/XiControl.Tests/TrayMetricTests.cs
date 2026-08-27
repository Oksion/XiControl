using FluentAssertions;
using XiControl.SystemIntegration;
using XiControl.Ui;
using Xunit;

namespace XiControl.Tests;

/// <summary>Индикатор в трее (XIC-35): чистая логика форматирования и парсинга метрики.</summary>
public class TrayMetricTests
{
    // ---- ParseKind: строка конфига → метрика ----

    [Theory]
    [InlineData("power", TrayMetric.Power)]
    [InlineData("cpu", TrayMetric.Cpu)]
    [InlineData("gpu", TrayMetric.Gpu)]
    [InlineData("ram", TrayMetric.Ram)]
    [InlineData("temp", TrayMetric.Temp)]
    [InlineData("TEMP", TrayMetric.Temp)] // регистр не важен (руками правленный config.json)
    public void ParseKind_понимает_все_метрики(string s, TrayMetric expected) =>
        TrayMetricFormat.ParseKind(s).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bogus")]
    public void ParseKind_неизвестное_даёт_дефолт_power(string? s) =>
        TrayMetricFormat.ParseKind(s).Should().Be(TrayMetric.Power);

    [Theory]
    [InlineData(TrayMetric.Power, "power")]
    [InlineData(TrayMetric.Cpu, "cpu")]
    [InlineData(TrayMetric.Gpu, "gpu")]
    [InlineData(TrayMetric.Ram, "ram")]
    [InlineData(TrayMetric.Temp, "temp")]
    public void Key_обратен_ParseKind(TrayMetric m, string key)
    {
        TrayMetricFormat.Key(m).Should().Be(key);
        TrayMetricFormat.ParseKind(key).Should().Be(m);
    }

    // ---- IconText: компактный текст на значке ----

    [Fact]
    public void IconText_NaN_это_прочерк() =>
        TrayMetricFormat.IconText(TrayMetric.Power, float.NaN).Should().Be("—");

    [Theory]
    [InlineData(12.4f, "12")]
    [InlineData(-8.6f, "9")]    // разряд: направление тока на значке не показываем — модуль
    [InlineData(145f, "145")]   // мощные БП: три знака допустимы (шрифт мельче)
    public void IconText_ватты_целое_по_модулю(float w, string expected) =>
        TrayMetricFormat.IconText(TrayMetric.Power, w).Should().Be(expected);

    [Theory]
    [InlineData(0f, "0")]
    [InlineData(37.4f, "37")]
    [InlineData(99.5f, "100")]
    [InlineData(120f, "100")] // кривые данные клэмпятся к шкале процентов
    public void IconText_проценты_клэмпятся(float pct, string expected)
    {
        TrayMetricFormat.IconText(TrayMetric.Cpu, pct).Should().Be(expected);
        TrayMetricFormat.IconText(TrayMetric.Gpu, pct).Should().Be(expected);
        TrayMetricFormat.IconText(TrayMetric.Ram, pct).Should().Be(expected);
    }

    [Theory]
    [InlineData(67.2f, "67°")]
    [InlineData(104f, "104°")] // температура процентами не ограничена
    public void IconText_температура_с_градусом(float c, string expected) =>
        TrayMetricFormat.IconText(TrayMetric.Temp, c).Should().Be(expected);

    // ---- CpuPct: математика GetSystemTimes ----

    [Fact]
    public void CpuPct_половина_времени_занято() =>
        TrayMetricFormat.CpuPct(deltaIdle: 50, deltaBusy: 100).Should().Be(50f);

    [Fact]
    public void CpuPct_без_простоя_это_100() =>
        TrayMetricFormat.CpuPct(deltaIdle: 0, deltaBusy: 100).Should().Be(100f);

    [Fact]
    public void CpuPct_простой_больше_занятого_клэмп_к_нулю() =>
        TrayMetricFormat.CpuPct(deltaIdle: 150, deltaBusy: 100).Should().Be(0f);

    [Fact]
    public void CpuPct_нулевой_интервал_безопасен() =>
        TrayMetricFormat.CpuPct(deltaIdle: 0, deltaBusy: 0).Should().Be(0f);

    [Theory]
    [InlineData(18_750UL, -18.75f)]
    [InlineData(30_000UL, -30f)]
    public void EnergyMeter_MilliwattsBecomeConsumptionWatts(ulong milliwatts, float expected) =>
        EnergyMeterPower.ToConsumptionWatts(milliwatts).Should().Be(expected);

    [Fact]
    public void EnergyMeter_ZeroMeansUnavailable() =>
        EnergyMeterPower.ToConsumptionWatts(0).Should().Be(float.NaN);

    [Fact]
    public void Monitor_GpuDetail_IncludesFrequencyAndPower()
    {
        string detail = MonitorMetricFormat.Detail(TrayMetric.Gpu,
            new TrayMetricReading(8f, 2.4f, 900f));

        detail.Should().Contain("900 MHz").And.Contain("2.4 W");
    }

    [Fact]
    public void Monitor_RamDetail_IncludesUsedAndTotalMemory()
    {
        string detail = MonitorMetricFormat.Detail(TrayMetric.Ram,
            new TrayMetricReading(54f, 17.3f, 31.5f));

        detail.Should().Contain("17.3").And.Contain("31.5 GB");
    }

    [Theory]
    [InlineData(12f, 25f)]
    [InlineData(25.1f, 30f)]
    [InlineData(47f, 50f)]
    public void Monitor_PowerGraphScale_IsReadableAndNeverBelow25Watts(float sample, float expected) =>
        MonitorMetricFormat.GraphMaximum(TrayMetric.Power, [sample]).Should().Be(expected);

    [Theory]
    [InlineData(-7.6f, "8 W")]
    [InlineData(12.4f, "12 W")]
    public void Monitor_PowerWidget_UsesTheSingleLargeRoundedValue(float watts, string expected) =>
        MonitorMetricFormat.PowerWidgetValue(watts).Should().Be(expected);
}
