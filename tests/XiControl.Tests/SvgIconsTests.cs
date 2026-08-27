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
    [Theory]
    [InlineData(SvgIcons.MenuBattery)]
    [InlineData(SvgIcons.MenuTravel)]
    [InlineData(SvgIcons.MenuOwl)]
    [InlineData(SvgIcons.MenuRefreshRate)]
    [InlineData(SvgIcons.MenuMonitor)]
    [InlineData(SvgIcons.MenuPerformance)]
    [InlineData(SvgIcons.MenuPerfEco)]
    [InlineData(SvgIcons.MenuPerfQuiet)]
    [InlineData(SvgIcons.MenuPerfAuto)]
    [InlineData(SvgIcons.MenuPerfBalance)]
    [InlineData(SvgIcons.MenuPerfTurbo)]
    [InlineData(SvgIcons.MenuPerfFull)]
    [InlineData(SvgIcons.MenuSettings)]
    [InlineData(SvgIcons.MenuExit)]
    public void TrayMenuIcons_AreEmbedded_AndAcceptThemeColor(string name)
    {
        var tint = Color.FromArgb(88, 166, 255);
        var bmp = SvgIcons.Render(name, 20, tint);

        bmp.Size.Should().Be(new Size(20, 20));
        var pixels = Enumerable.Range(0, bmp.Width).SelectMany(x => Enumerable.Range(0, bmp.Height)
            .Select(y => bmp.GetPixel(x, y))).Where(c => c.A > 0).ToArray();
        pixels.Should().NotBeEmpty();
        pixels.Should().Contain(c => c.B > c.R, "currentColor должен заменяться цветом темы");
    }

    [Theory]
    [InlineData(SvgIcons.CapsLockOn)]
    [InlineData(SvgIcons.CapsLockOff)]
    public void CapsLockIcons_AreEmbedded_AndDrawn(string name)
    {
        using var bmp = SvgIcons.Render(name, 64);

        bmp.Width.Should().Be(64);
        bmp.Height.Should().Be(64);
        Enumerable.Range(0, bmp.Width).SelectMany(x => Enumerable.Range(0, bmp.Height)
            .Select(y => bmp.GetPixel(x, y).A)).Should().Contain(a => a > 0);
    }

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

    [Fact]
    public void OpenPng_ReturnsACompleteIndependentPngStream()
    {
        using MemoryStream stream = SvgIcons.OpenPng(SvgIcons.PerfAuto, 64);

        stream.Position.Should().Be(0);
        stream.Length.Should().BeGreaterThan(100);
        byte[] signature = new byte[8];
        stream.ReadExactly(signature);
        signature.Should().Equal(137, 80, 78, 71, 13, 10, 26, 10);
    }
}
