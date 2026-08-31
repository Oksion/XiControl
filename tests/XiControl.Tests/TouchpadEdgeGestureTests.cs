using FluentAssertions;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Жест «ползунок у края тачпада» (XIC-61): чистая логика без Win32. Живое чтение сырых
/// касаний — глазами и пробником reference/touchpad-edges.
/// </summary>
public sealed class TouchpadEdgeGestureTests
{
    // Полоса 10% ширины, шаг 6% высоты — те же значения, что и дефолт в проде.
    private readonly TouchpadEdgeGesture _g = new(stripFraction: 0.1, stepFraction: 0.06);

    private static TouchContact[] One(double x, double y, int id = 1) => [new(id, x, y)];

    // ---- Где начинается жест ----

    [Theory]
    [InlineData(0.00, TouchpadEdge.Left)]
    [InlineData(0.10, TouchpadEdge.Left)]
    [InlineData(0.11, TouchpadEdge.None)]
    [InlineData(0.50, TouchpadEdge.None)]
    [InlineData(0.89, TouchpadEdge.None)]
    [InlineData(0.90, TouchpadEdge.Right)]
    [InlineData(1.00, TouchpadEdge.Right)]
    public void EdgeOf_ДелитПанельНаТриЗоны(double x, TouchpadEdge expected) =>
        _g.EdgeOf(x).Should().Be(expected);

    [Fact]
    public void КасаниеВПолосе_НачинаетЖест()
    {
        var (edge, steps) = _g.Update(One(0.05, 0.5));

        edge.Should().Be(TouchpadEdge.Left);
        steps.Should().Be(0, "первый кадр только фиксирует точку отсчёта");
        _g.Active.Should().Be(TouchpadEdge.Left);
    }

    // Ключевая стыковка с curtain-зоной (XIC-24): она гасит ИНИЦИАЦИЮ касания в полосе.
    // Палец, начатый в середине, курсор двигать продолжает — крутить им ещё и ползунок
    // значило бы делать два действия одним движением.
    [Fact]
    public void КасаниеИзвне_ЗаехавшееВПолосу_ЖестНеНачинает()
    {
        _g.Update(One(0.5, 0.9));      // начали в середине
        var (edge, steps) = _g.Update(One(0.03, 0.2)); // доехали до левого края и вверх

        edge.Should().Be(TouchpadEdge.None);
        steps.Should().Be(0);
        _g.Active.Should().Be(TouchpadEdge.None);
    }

    // ---- Набор шагов ----

    [Fact]
    public void ДвижениеВверх_ДаётПоложительныеШаги()
    {
        _g.Update(One(0.05, 0.80));

        var (edge, steps) = _g.Update(One(0.05, 0.62)); // прошли 18% высоты = три шага по 6%

        edge.Should().Be(TouchpadEdge.Left);
        steps.Should().Be(3);
    }

    [Fact]
    public void ДвижениеВниз_ДаётОтрицательныеШаги()
    {
        _g.Update(One(0.95, 0.20));

        var (edge, steps) = _g.Update(One(0.95, 0.33)); // 13% вниз = два шага

        edge.Should().Be(TouchpadEdge.Right);
        steps.Should().Be(-2);
    }

    [Fact]
    public void ДвижениеМеньшеШага_ШагаНеДаёт()
    {
        _g.Update(One(0.05, 0.50));

        _g.Update(One(0.05, 0.47)).Steps.Should().Be(0, "3% меньше порога в 6%");
    }

    // Остаток переносится, а не теряется: иначе медленное непрерывное движение не давало бы
    // шагов вовсе — каждый кадр по отдельности меньше порога.
    [Fact]
    public void ОстатокПереносится_МедленноеДвижениеНеТеряется()
    {
        _g.Update(One(0.05, 0.50));

        // Значения намеренно не попадают ровно на границу шага: на точном кратном
        // результат зависел бы от округления double, а не от логики.
        _g.Update(One(0.05, 0.46)).Steps.Should().Be(0);   // прошли 4% — меньше порога
        _g.Update(One(0.05, 0.43)).Steps.Should().Be(1);   // всего 7% — первый шаг, остаток 1%
        _g.Update(One(0.05, 0.40)).Steps.Should().Be(0);   // от новой точки 4% — снова мало
        _g.Update(One(0.05, 0.37)).Steps.Should().Be(1);   // 7% от неё же — второй шаг
    }

    // ---- Отмена и сброс ----

    [Fact]
    public void ВторойПалец_ОтменяетЖест()
    {
        _g.Update(One(0.05, 0.80));

        _g.Update([new(1, 0.05, 0.80), new(2, 0.40, 0.50)]).Steps.Should().Be(0);
        _g.Active.Should().Be(TouchpadEdge.None, "два пальца — это прокрутка Windows, не наш ползунок");
    }

    [Fact]
    public void ПослеОтмены_ОдинПалецНеВозобновляетЖест()
    {
        _g.Update(One(0.05, 0.80));
        _g.Update([new(1, 0.05, 0.80), new(2, 0.40, 0.50)]);

        // второй палец убрали, первый ведём вверх — до полного отрыва жест не оживает
        _g.Update(One(0.05, 0.50)).Steps.Should().Be(0);
        _g.Active.Should().Be(TouchpadEdge.None);
    }

    [Fact]
    public void ОтрывПальца_СбрасываетСостояние()
    {
        _g.Update(One(0.05, 0.80));
        _g.Update([]);

        _g.Active.Should().Be(TouchpadEdge.None);
        _g.Update(One(0.05, 0.80)).Edge.Should().Be(TouchpadEdge.Left, "после отрыва жест начинается заново");
    }

    [Fact]
    public void НовоеКасаниеВПолосе_ОтсчётСНуля()
    {
        _g.Update(One(0.05, 0.80, id: 1));
        _g.Update([]);

        _g.Update(One(0.05, 0.20, id: 2)).Steps.Should().Be(0,
            "точка отсчёта берётся у нового касания, а не тянется от прошлого");
    }

    // ---- Клэмпы настроек: config.json правится руками ----

    [Fact]
    public void НулеваяПолоса_КлэмпитсяИЖестОстаётсяВозможен()
    {
        var g = new TouchpadEdgeGesture(stripFraction: 0, stepFraction: 0.06);

        g.EdgeOf(0.01).Should().Be(TouchpadEdge.Left, "иначе фича молча перестала бы работать");
    }

    [Fact]
    public void ОгромнаяПолоса_НеСъедаетВсюПанель()
    {
        var g = new TouchpadEdgeGesture(stripFraction: 0.9, stepFraction: 0.06);

        g.EdgeOf(0.5).Should().Be(TouchpadEdge.None, "середина панели обязана остаться свободной");
    }
}
