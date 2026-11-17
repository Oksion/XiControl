using FluentAssertions;
using XiControl.Ui;
using XiControl.Wmi;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Полнота UI-маппинга режимов: панель и меню строятся из AppController.AllModes через ModeUi —
/// новый режим без ключа/акцента молча отрисуется «как Авто», тест это ловит.
/// </summary>
public sealed class ModeUiTests
{
    [Theory]
    [InlineData(0, Wmi.PerfMode.Turbo)]
    [InlineData((byte)Wmi.PerfMode.Balance, Wmi.PerfMode.Balance)]
    [InlineData((byte)Wmi.PerfMode.Quiet, Wmi.PerfMode.Quiet)]
    [InlineData((byte)Wmi.PerfMode.Turbo, Wmi.PerfMode.Turbo)]
    [InlineData((byte)Wmi.PerfMode.FullSpeed, Wmi.PerfMode.FullSpeed)]
    [InlineData((byte)Wmi.PerfMode.Auto, Wmi.PerfMode.Auto)]
    [InlineData((byte)Wmi.PerfMode.Eco, Wmi.PerfMode.Eco)]
    public void PerformanceHotkeyValue_MapsToFirmwareMode(byte value, Wmi.PerfMode expected) =>
        ModeUi.FromHotkeyValue(value).Should().Be(expected);

    [Fact]
    public void UnknownPerformanceHotkeyValue_IsRejected() =>
        ModeUi.FromHotkeyValue(0x7F).Should().BeNull();

    [Fact]
    public void EveryMode_HasDistinctKeyAndAccent()
    {
        foreach (var m in AppController.AllModes)
            ModeUi.Key(m).Should().NotBeNull($"режим {m} должен иметь ключ локализации");

        AppController.AllModes.Select(ModeUi.Key).Should().OnlyHaveUniqueItems();
        AppController.AllModes.Select(ModeUi.Accent).Should().OnlyHaveUniqueItems();
        AppController.AllModes.Select(ModeUi.Kind).Should().OnlyHaveUniqueItems();
    }

    // Чужая модель может прислать код режима, которого у нас нет: подписи для него не
    // существует (рисовать нечего), но цвет и вид OSD обязаны деградировать к «Авто»,
    // а не уронить отрисовку панели.
    [Fact]
    public void UnknownMode_DegradesToAuto()
    {
        var alien = (Wmi.PerfMode)0x7F;

        ModeUi.Key(alien).Should().BeNull();
        ModeUi.Kind(alien).Should().Be(ModeUi.Kind(Wmi.PerfMode.Auto));
        ModeUi.Accent(alien).Should().Be(ModeUi.Accent(Wmi.PerfMode.Auto));
    }

    // XIC-110: каждое 0x16 — строкой журнала, и неизвестные коды тоже
    [Fact]
    public void EventLine_Обычная() =>
        ModeUi.EventLine(0x0A, PerfMode.Eco, online: true)
            .Should().Be("Режим: событие 0x0A (Eco), прошивка Eco, сеть");

    [Fact]
    public void EventLine_НеизвестныйКод() =>
        ModeUi.EventLine(0x07, PerfMode.Auto, online: false)
            .Should().Be("Режим: событие 0x07 (неизвестно), прошивка Auto, батарея");

    [Fact]
    public void EventLine_ПрошивкаНеСогласна() =>
        ModeUi.EventLine(0x03, PerfMode.Quiet, online: true)
            .Should().Be("Режим: событие 0x03 (Turbo), прошивка Quiet, сеть — верим прошивке");

    [Fact]
    public void EventLine_ПрошивкаМолчит() =>
        ModeUi.EventLine(0x09, null, online: true)
            .Should().Be("Режим: событие 0x09 (Auto), прошивка не ответила, сеть");

    [Fact]
    public void PowerLine_СбросВиден() =>
        ModeUi.PowerLine(online: true, PerfMode.Turbo, PerfMode.Auto, 3000)
            .Should().Be("Режим: питание → сеть, сразу Turbo, через 3 с Auto");
}
