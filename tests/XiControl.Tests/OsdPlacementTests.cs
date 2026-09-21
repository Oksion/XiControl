using System.Drawing;
using FluentAssertions;
using XiControl.Ui;
using Xunit;

namespace XiControl.Tests;

/// <summary>Размещение OSD (XIC-67): чистый расчёт координат по сетке 3×3.
/// Выбор монитора (<c>TargetScreen</c>) здесь не проверить — он про живые окна и курсор.</summary>
public class OsdPlacementTests
{
    // типовой экран 1920×1080 с панелью задач снизу
    private static readonly Rectangle Wa = new(0, 0, 1920, 1040);
    private static readonly Size Osd = new(300, 150);
    private const int M = 24;

    [Fact]
    public void Center_оставляет_историческое_положение()
    {
        // по центру ширины и на 60% высоты — ровно там, где OSD висел до появления настройки:
        // у тех, кто ничего не менял, плашка не должна переехать
        var p = OsdPlacement.Locate(Wa, Osd, OsdPosition.Center, M);
        p.X.Should().Be((1920 - 300) / 2);
        p.Y.Should().Be((int)(1040 * 0.60));
    }

    [Theory]
    [InlineData(OsdPosition.TopLeft, 24, 24)]
    [InlineData(OsdPosition.Top, (1920 - 300) / 2, 24)]
    [InlineData(OsdPosition.TopRight, 1920 - 300 - 24, 24)]
    [InlineData(OsdPosition.BottomLeft, 24, 1040 - 150 - 24)]
    [InlineData(OsdPosition.Bottom, (1920 - 300) / 2, 1040 - 150 - 24)]
    [InlineData(OsdPosition.BottomRight, 1920 - 300 - 24, 1040 - 150 - 24)]
    public void Углы_и_края_прижимаются_с_отступом(OsdPosition pos, int x, int y)
    {
        var p = OsdPlacement.Locate(Wa, Osd, pos, M);
        p.Should().Be(new Point(x, y));
    }

    [Theory]
    [InlineData(OsdPosition.Left, 24)]
    [InlineData(OsdPosition.Right, 1920 - 300 - 24)]
    public void Средний_ряд_держит_ту_же_высоту_что_и_центр(OsdPosition pos, int x)
    {
        var p = OsdPlacement.Locate(Wa, Osd, pos, M);
        p.X.Should().Be(x);
        p.Y.Should().Be(OsdPlacement.Locate(Wa, Osd, OsdPosition.Center, M).Y);
    }

    [Fact]
    public void Рабочая_область_второго_монитора_учитывает_смещение()
    {
        // монитор справа от основного: координаты рабочей области не начинаются с нуля,
        // и раньше именно это терялось — OSD считался от PrimaryScreen
        var second = new Rectangle(1920, 0, 1280, 1000);
        var p = OsdPlacement.Locate(second, Osd, OsdPosition.TopLeft, M);
        p.Should().Be(new Point(1920 + 24, 24));
    }

    [Fact]
    public void Низкий_экран_не_даёт_карточке_свеситься_за_край()
    {
        // 60% высоты у низкого экрана уводят карточку за нижнюю границу — подпираем
        var low = new Rectangle(0, 0, 1024, 240);
        var p = OsdPlacement.Locate(low, Osd, OsdPosition.Center, M);
        (p.Y + Osd.Height).Should().BeLessThanOrEqualTo(low.Bottom - M);
    }

    [Fact]
    public void Карточка_больше_экрана_прижимается_к_началу_а_не_уезжает()
    {
        // вырожденный случай (огромный DPI, крошечная рабочая область): показать целиком
        // нельзя, но верхний левый угол должен остаться видимым
        var tiny = new Rectangle(0, 0, 200, 100);
        var p = OsdPlacement.Locate(tiny, Osd, OsdPosition.BottomRight, M);
        p.Should().Be(new Point(0, 0));
    }
}
