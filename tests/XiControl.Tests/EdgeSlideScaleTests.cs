using FluentAssertions;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Пересчёт шагов краевого жеста в яркость и громкость (XIC-61). Главное, что здесь
/// проверяется, — обе шкалы проходятся за одно и то же число проходов вдоль края:
/// именно это разъехалось в первой версии (яркость за два прохода, громкость за четыре).
/// </summary>
public sealed class EdgeSlideScaleTests
{
    // Полный проход вдоль края = StepsPerSwipe шагов жеста.
    private static int Sum(Func<int, int> take, int steps)
    {
        int total = 0;
        for (int i = 0; i < steps; i++) total += take(1);
        return total;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ЯркостьИГромкость_ПроходятШкалуЗаОдноЧислоПроходов(int swipes)
    {
        var bright = new EdgeSlideScale(swipes);
        var volume = new EdgeSlideScale(swipes);
        int steps = EdgeSlideScale.StepsPerSwipe * swipes;

        int brightPercent = Sum(bright.Brightness, steps);
        int volumePercent = Sum(volume.VolumeTaps, steps) * 2; // нажатие громкости — 2% шкалы

        brightPercent.Should().Be(100, "полная шкала за заявленное число проходов");
        volumePercent.Should().Be(100, "громкость обязана идти в ногу с яркостью");
    }

    [Fact]
    public void ОдинПроход_ЭтоВсяШкала()
    {
        var s = new EdgeSlideScale(1);

        Sum(s.Brightness, EdgeSlideScale.StepsPerSwipe).Should().Be(100);
    }

    [Fact]
    public void ТриПрохода_ЗаОдинПроходТреть()
    {
        var s = new EdgeSlideScale(3);

        Sum(s.Brightness, EdgeSlideScale.StepsPerSwipe).Should().BeCloseTo(33, 1);
    }

    // Без переноса остатка медленное движение не давало бы ничего: каждый шаг по отдельности
    // меньше одного процента при плавной чувствительности.
    [Fact]
    public void ОстатокПереносится_ПлавнаяЧувствительностьНеТеряетДвижение()
    {
        var s = new EdgeSlideScale(3);

        s.Brightness(1).Should().Be(0, "два трети процента — меньше целого");
        s.Brightness(1).Should().Be(1, "накопилось больше процента");
    }

    [Fact]
    public void Сброс_ЗабываетНакопленное()
    {
        var s = new EdgeSlideScale(3);
        s.Brightness(1);

        s.Reset();

        s.Brightness(1).Should().Be(0, "после отрыва пальца счёт начинается заново");
    }

    [Fact]
    public void ДвижениеВниз_ДаётОтрицательныеЗначения()
    {
        var s = new EdgeSlideScale(1);

        s.Brightness(-10).Should().Be(-20, "десять шагов вниз при шаге в 2% шкалы");
    }

    // config.json правится руками: ноль проходов — деление на ноль, десять — мёртвый ползунок.
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(99, 3)]
    public void Normalize_ДержитРазумныйДиапазон(int given, int expected) =>
        EdgeSlideScale.Normalize(given).Should().Be(expected);
}
