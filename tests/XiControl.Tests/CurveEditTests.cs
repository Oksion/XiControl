using FluentAssertions;
using XiControl.Config;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Правила ручной правки кривой авто-яркости (XIC-33/XIC-66): зажим между соседями,
/// пределы числа точек, различимость соседних условий. Сам график — глазами.
/// </summary>
public sealed class CurveEditTests
{
    private static List<BrightnessPoint> Curve() => BrightnessCurve.DefaultPoints();

    // ---- Перетаскивание ----

    [Fact]
    public void Move_KeepsMonotonic_ClampingPercentBetweenNeighbours()
    {
        var pts = Curve(); // ..., 50 лк → 40%, 200 лк → 60%, 700 лк → 80%, ...

        // тащим среднюю точку вниз, к самому полу
        var moved = CurveEdit.Move(pts, 3, 200, 0);

        moved[3].Percent.Should().Be(40, "ниже левого соседа кривая стала бы немонотонной");
    }

    [Fact]
    public void Move_KeepsOrder_ClampingLuxBetweenNeighbours()
    {
        var pts = Curve();

        // тащим точку 200 лк далеко вправо, за 2000 лк
        var moved = CurveEdit.Move(pts, 3, 9000, 60);

        moved[3].Lux.Should().BeLessThan(700, "правого соседа обгонять нельзя");
        moved.Should().BeInAscendingOrder(p => p.Lux);
        moved.Should().HaveCount(pts.Count, "перетаскивание точек не создаёт и не теряет");
    }

    [Fact]
    public void Move_LeavesGapToNeighbour_SoLearningWillNotEatThePoint()
    {
        var pts = Curve();

        var moved = CurveEdit.Move(pts, 3, 700, 60, minGap: 0.1);

        double gap = Math.Log10(1 + moved[4].Lux) - Math.Log10(1 + moved[3].Lux);
        gap.Should().BeApproximately(0.1, 0.001);
    }

    [Fact]
    public void Move_EdgePoints_CanReachFloorAndCeiling()
    {
        var pts = Curve();

        CurveEdit.Move(pts, 0, 0, 0)[0].Percent.Should().Be(0, "у первой точки соседа слева нет");
        CurveEdit.Move(pts, pts.Count - 1, 2000, 100)[^1].Percent.Should().Be(100);
    }

    [Fact]
    public void Move_DoesNotTouchTheCallersList()
    {
        var pts = Curve();

        CurveEdit.Move(pts, 3, 300, 70);

        pts[3].Lux.Should().Be(200, "правка становится общей только после сохранения");
        pts[3].Percent.Should().Be(60);
    }

    // ---- Добавление ----

    [Fact]
    public void Add_InsertsInOrder()
    {
        var pts = Curve();

        var grown = CurveEdit.Add(pts, 100, 50);

        grown.Should().NotBeNull().And.HaveCount(pts.Count + 1);
        grown!.Should().BeInAscendingOrder(p => p.Lux);
        grown!.Single(p => p.Lux == 100).Percent.Should().Be(50);
    }

    [Fact]
    public void Add_ClampsPercentBetweenNeighbours()
    {
        var pts = Curve(); // 50 лк → 40%, 200 лк → 60%

        var grown = CurveEdit.Add(pts, 100, 5); // ткнули мышью у самого пола

        grown!.Single(p => p.Lux == 100).Percent.Should().Be(40, "иначе «светлее — темнее»");
    }

    [Fact]
    public void Add_RefusesNextToAnExistingPoint()
    {
        var pts = Curve();

        CurveEdit.Add(pts, 210, 60, minGap: 0.1).Should().BeNull(
            "точки ближе гистерезиса — два мнения об одних условиях, обучение съест одно");
    }

    [Fact]
    public void Add_RefusesBeyondTheLimit()
    {
        // набиваем кривую до предела с заведомо различимым шагом
        var pts = new List<BrightnessPoint>();
        for (int i = 0; i < CurveEdit.MaxPoints; i++)
            pts.Add(new BrightnessPoint { Lux = (float)(Math.Pow(10, i * 0.3) - 1), Percent = i * 8 });

        CurveEdit.Add(pts, 9000, 100).Should().BeNull("дюжины точек на графике уже не развести");
    }

    // ---- Удаление ----

    [Fact]
    public void Remove_DropsThePoint()
    {
        var pts = Curve();

        var left = CurveEdit.Remove(pts, 2);

        left.Should().NotBeNull().And.HaveCount(pts.Count - 1);
        left!.Should().NotContain(p => p.Lux == 50);
    }

    [Fact]
    public void Remove_RefusesToLeaveFewerThanTwoPoints()
    {
        List<BrightnessPoint> two =
        [
            new() { Lux = 0, Percent = 10 },
            new() { Lux = 500, Percent = 90 },
        ];

        CurveEdit.Remove(two, 0).Should().BeNull("с одной точкой кривая перестаёт быть кривой");
    }

    [Fact]
    public void Remove_IgnoresNonsenseIndex()
    {
        CurveEdit.Remove(Curve(), 42).Should().BeNull();
    }
}
