using FluentAssertions;
using XiControl.Ui;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Рендер встроенных SVG. Иконки проверяются глазами (IconPreview), здесь — только то, что
/// ломается молча: пропала ли картинка из ресурсов и не растянута ли неквадратная кнопка.
/// </summary>
public sealed class SvgIconsTests
{
    [Fact]
    public void BuyMeACoffee_IsEmbedded_AndKeepsAspectRatio()
    {
        // ассет 545×153: квадратный Render его бы сплющил, поэтому рендерим по высоте
        using var bmp = SvgIcons.RenderByHeight(SvgIcons.BuyMeACoffee, 34);

        bmp.Height.Should().Be(34);
        bmp.Width.Should().Be((int)Math.Round(34 * 545.0 / 153), "ширина считается из viewBox");
    }

    [Fact]
    public void BuyMeACoffee_IsActuallyDrawn_NotBlank()
    {
        // Svg.NET на неподдерживаемых конструкциях (mask/clipPath) молча отдаёт пустоту —
        // проверяем, что фирменный жёлтый фон реально нарисован
        using var bmp = SvgIcons.RenderByHeight(SvgIcons.BuyMeACoffee, 40);

        var center = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
        center.A.Should().Be(255, "кнопка непрозрачная");

        var corner = bmp.GetPixel(bmp.Width / 2, 3); // верхняя кромка — фон кнопки
        corner.R.Should().BeGreaterThan(200);
        corner.G.Should().BeGreaterThan(180);
        corner.B.Should().BeLessThan(100, "фирменный фон BMC — жёлтый #FFDD00");
    }

    [Fact]
    public void RenderByHeight_CachesBitmap()
    {
        // кэш общий на (имя, высота) — окно настроек пересобирается часто
        SvgIcons.RenderByHeight(SvgIcons.BuyMeACoffee, 28)
            .Should().BeSameAs(SvgIcons.RenderByHeight(SvgIcons.BuyMeACoffee, 28));
    }
}
