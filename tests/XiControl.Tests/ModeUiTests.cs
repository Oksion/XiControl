using FluentAssertions;
using XiControl.Ui;
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
}
