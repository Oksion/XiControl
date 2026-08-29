using FluentAssertions;
using XiControl.SystemIntegration;
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

    // Индикатор «Потребление» показывает ватты батареи. RAPL — мощность пакета CPU, другая
    // величина, и она допустима только там, где датчика батареи нет вовсе: обещанный в
    // настройках прочерк «от сети без заряда» подменять ей нельзя. Проверено на TM2424:
    // в розетке без заряда IOCTL отдаёт Rate=0, то есть NaN при RateUnknown=false.
    [Theory]
    [InlineData(true, false, false, "датчик ответил — верим ему, даже если это NaN")]
    [InlineData(true, true, true, "прошивка не сообщает ток — только тогда RAPL")]
    [InlineData(false, false, true, "Battery API недоступен целиком — только тогда RAPL")]
    public void PowerFallback_OnlyWhenBatterySensorHasNothingToSay(
        bool sensorAlive, bool rateUnknown, bool expectFallback, string because) =>
        TrayMetricFormat.UsesCpuPackageFallback(sensorAlive, rateUnknown)
            .Should().Be(expectFallback, because);

    // ---- ACPI-термозона: фолбэк температуры на машинах без Intel DPTF (XIC-41) ----

    // MSAcpi_ThermalZoneTemperature отдаёт десятые Кельвина. Опорные значения сняты с живого
    // железа: TM2424 — 3011 (зона-заглушка), TM2113 из отчёта владельца — 3223 ≈ 49,1 °C.
    [Theory]
    [InlineData(3011, 27.95)]
    [InlineData(3223, 49.15)]
    [InlineData(2731.5, 0)]
    public void ThermalZone_ПереводитДесятыеКельвинаВЦельсии(double deciKelvin, double celsius) =>
        ThermalZone.ToCelsius(deciKelvin).Should().BeApproximately((float)celsius, 0.01f);

    // Диапазон общий с DPTF: ноль — неинициализированный датчик, выше 130 °C железо не живёт.
    [Theory]
    [InlineData(0f, false)]
    [InlineData(-40f, false)]
    [InlineData(27.9f, true)]
    [InlineData(129.9f, true)]
    [InlineData(130f, false)]
    public void ThermalZone_ОтбраковываетНеправдоподобное(float celsius, bool ok) =>
        ThermalZone.Plausible(celsius).Should().Be(ok);

    [Fact]
    public void ThermalZone_БерётМаксимумСредиГодныхЗон() =>
        // 2731.5 → ровно 0 °C (неинициализированная зона) и 5000 → 226 °C выпадают из диапазона
        ThermalZone.MaxCelsius([3011, 2731.5, 3223, 5000])
            .Should().BeApproximately(49.15f, 0.01f);

    [Fact]
    public void ThermalZone_БезГодныхЗон_ЭтоNaN() =>
        // ни одной живой зоны — «—» в UI, а не выдуманный ноль
        float.IsNaN(ThermalZone.MaxCelsius([2731.5, 5000])).Should().BeTrue();

    [Fact]
    public void ThermalZone_ПустойСписок_ЭтоNaN() =>
        float.IsNaN(ThermalZone.MaxCelsius([])).Should().BeTrue();
}
