using FluentAssertions;
using XiControl.Ui;
using Xunit;

namespace XiControl.Tests;

/// <summary>Резолвер темы флайаутов (Фаза 6.4): настройка → флаг Dark. Вариант "system"
/// не тестируем — он читает реестр Windows.</summary>
public sealed class FlyoutPaletteTests
{
    [Fact]
    public void Apply_MapsSettingToDarkFlag()
    {
        FlyoutPalette.Apply("light");
        FlyoutPalette.Dark.Should().BeFalse("явная светлая");

        FlyoutPalette.Apply("какая-то дичь из конфига");
        FlyoutPalette.Dark.Should().BeTrue("неизвестное значение = тёмная (безопасный дефолт)");

        FlyoutPalette.Apply(null);
        FlyoutPalette.Dark.Should().BeTrue("null = тёмная (историческое поведение)");
    }
}
