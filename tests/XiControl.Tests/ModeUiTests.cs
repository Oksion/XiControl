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
