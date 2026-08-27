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
    [InlineData(1, Wmi.PerfMode.Balance)]
    [InlineData(2, Wmi.PerfMode.Quiet)]
    [InlineData(3, Wmi.PerfMode.Turbo)]
    [InlineData(4, Wmi.PerfMode.FullSpeed)]
    [InlineData(9, Wmi.PerfMode.Auto)]
    [InlineData(10, Wmi.PerfMode.Eco)]
    public void HotkeyValue_MapsLikeOemDispatcher(byte value, Wmi.PerfMode expected)
    {
        ModeUi.FromHotkeyValue(value).Should().Be(expected);
    }

    [Fact]
    public void UnknownHotkeyValue_IsRejected()
    {
        ModeUi.FromHotkeyValue(0x7F).Should().BeNull();
    }

    [Fact]
    public void EveryMode_HasDistinctKeyAndAccent()
    {
        foreach (var m in AppController.AllModes)
            ModeUi.Key(m).Should().NotBeNull($"режим {m} должен иметь ключ локализации");

        AppController.AllModes.Select(ModeUi.Key).Should().OnlyHaveUniqueItems();
        AppController.AllModes.Select(ModeUi.Accent).Should().OnlyHaveUniqueItems();
        AppController.AllModes.Select(ModeUi.Kind).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void EveryMode_UsesEmbeddedXiControlSvgAssets()
    {
        foreach (var mode in AppController.AllModes)
        {
            using Bitmap panel = SvgIcons.Render(ModeUi.SvgIcon(mode), 48);
            using Bitmap menu = SvgIcons.Render(ModeUi.MenuSvgIcon(mode), 24, Color.White);

            panel.Size.Should().Be(new Size(48, 48));
            menu.Size.Should().Be(new Size(24, 24));
            Pixels(panel).Should().Contain(pixel => pixel.A > 0);
            Pixels(menu).Should().Contain(pixel => pixel.A > 0);
        }
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
        ModeUi.SvgIcon(alien).Should().Be(SvgIcons.PerfAuto);
        ModeUi.MenuSvgIcon(alien).Should().Be(SvgIcons.MenuPerfAuto);
    }

    private static IEnumerable<Color> Pixels(Bitmap bitmap) =>
        Enumerable.Range(0, bitmap.Width).SelectMany(x =>
            Enumerable.Range(0, bitmap.Height).Select(y => bitmap.GetPixel(x, y)));
}
