using FluentAssertions;
using XiControl.Config;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Авто-яркость (XIC-30): обучаемая кривая (интерполяция, обучение с вытеснением конфликтов,
/// гистерезис) и машина состояний guard-а на фейках. Датчик и WMI — глазами.
/// </summary>
public sealed class AutoBrightnessTests
{
    public AutoBrightnessTests() => Log.Enabled = false;

    // ---- Кривая ----

    private static BrightnessCurve DefaultCurve(out List<BrightnessPoint> pts)
    {
        pts = BrightnessCurve.DefaultPoints();
        return new BrightnessCurve(pts);
    }

    [Fact]
    public void Predict_AtAnchors_ReturnsAnchorValues()
    {
        var curve = DefaultCurve(out _);

        curve.Predict(0).Should().Be(10);
        curve.Predict(200).Should().Be(60);
        curve.Predict(2000).Should().Be(100);
    }

    [Fact]
    public void Predict_BeyondEdges_ClampsToEdgeAnchors()
    {
        var curve = DefaultCurve(out _);

        curve.Predict(0).Should().Be(10, "темнее нуля не бывает");
        curve.Predict(50_000).Should().Be(100, "прямое солнце — всё равно максимум кривой");
    }

    [Fact]
    public void Predict_BetweenAnchors_InterpolatesMonotonically()
    {
        var curve = DefaultCurve(out _);

        int mid = curve.Predict(100); // между (50→40) и (200→60)
        mid.Should().BeInRange(41, 59);
        // монотонность по всей шкале — светлее не может значить темнее
        int prev = -1;
        foreach (var lux in new float[] { 0, 1, 5, 10, 30, 50, 100, 200, 400, 700, 1200, 2000, 5000 })
        {
            int p = curve.Predict(lux);
            p.Should().BeGreaterThanOrEqualTo(prev, $"кривая обязана быть монотонной (лк={lux})");
            prev = p;
        }
    }

    [Fact]
    public void Learn_AddsPoint_AndPredictsIt()
    {
        var curve = DefaultCurve(out _);

        curve.Learn(100, 33);

        curve.Predict(100).Should().Be(33, "в выученных условиях — ровно выученное значение");
    }

    [Fact]
    public void Learn_EvictsConflictingPoints_CurveStaysMonotonic()
    {
        var curve = DefaultCurve(out var pts);

        curve.Learn(50, 100); // «при 50 лк хочу максимум» — все точки правее с меньшей яркостью врут

        pts.Should().NotContain(p => p.Lux >= 50 && p.Percent < 100);
        curve.Predict(200).Should().Be(100);
        curve.Predict(2000).Should().Be(100);
        curve.Predict(0).Should().Be(10, "тёмный край не пострадал");
    }

    [Fact]
    public void Learn_SameLux_ReplacesOldPoint()
    {
        var curve = DefaultCurve(out var pts);

        curve.Learn(200, 30);
        curve.Learn(200, 70);

        pts.Count(p => Math.Abs(p.Lux - 200) < 0.01).Should().Be(1, "две точки на одном свете — противоречие");
        curve.Predict(200).Should().Be(70);
    }

    [Theory]
    [InlineData(100, 100, false)]  // не изменился
    [InlineData(100, 110, false)]  // ±26% в лог-шкале не набралось
    [InlineData(100, 200, true)]   // вдвое светлее — значимо
    [InlineData(100, 40, true)]    // сильно темнее — значимо
    public void Significant_LogHysteresis(float from, float to, bool expected) =>
        BrightnessCurve.Significant(from, to).Should().Be(expected);

    [Fact]
    public void Significant_FromUnknown_AlwaysTrue() =>
        BrightnessCurve.Significant(float.NaN, 5).Should().BeTrue("первые люксы всегда значимы");

    // ---- Guard на фейках ----

    private readonly AppConfig _cfg = new() { AutoBrightness = true };
    private readonly FakePowerEvents _power = new();
    private readonly FakeTimer _settle = new();
    private readonly FakeTimer _learn = new();
    private readonly List<(int From, int To)> _ramps = [];
    private int? _brightness = 50;
    private bool _adaptive;

    private AutoBrightnessGuard NewGuard()
    {
        if (_cfg.AutoBrightnessPointsAc.Count == 0)
            _cfg.AutoBrightnessPointsAc.AddRange(BrightnessCurve.DefaultPoints());
        if (_cfg.AutoBrightnessPointsBattery.Count == 0)
            _cfg.AutoBrightnessPointsBattery.AddRange(BrightnessCurve.DefaultPoints());
        return new AutoBrightnessGuard(_cfg, _power, _settle, _learn,
            () => _brightness, (f, t, _) => _ramps.Add((f, t)), _ => _adaptive, (l, _) => l);
    }

    [Fact]
    public void LuxChange_AfterSettle_RampsToPrediction()
    {
        using var g = NewGuard();

        g.OnLux(2000);                       // из комнаты — на «улицу»
        _ramps.Should().BeEmpty("свет должен устояться");
        _settle.Running.Should().BeTrue();
        _settle.Fire();

        _ramps.Should().Equal((50, 100));
    }

    [Fact]
    public void SmallLuxFlutter_DoesNotArmSettle()
    {
        using var g = NewGuard();
        g.OnLux(100);
        _settle.Fire(); // отработали сотню

        g.OnLux(108);   // мигнуло облако — в лог-шкале это мелочь

        _settle.Running.Should().BeFalse("гистерезис гасит дрожь — экран не «дышит»");
    }

    [Fact]
    public void Deadband_SuppressesTinyMoves()
    {
        _brightness = 59; // предсказание для 200 лк = 60 — разница меньше мёртвой зоны (5)
        using var g = NewGuard();

        g.OnLux(200);
        _settle.Fire();

        _ramps.Should().BeEmpty("ради 1% экран не трогаем");
    }

    [Fact]
    public void UserAdjustment_LearnsAfterCooldown_AndStopsFighting()
    {
        using var g = NewGuard();
        g.OnLux(200);
        _settle.Fire();                       // мы поставили 60
        g.OnBrightness(60, own: true, settling: false);

        g.OnBrightness(80, own: false, settling: false); // пользователь: «мне ярче»
        _learn.Running.Should().BeTrue("правка ждёт период раздумья");
        _learn.Fire();

        _cfg.AutoBrightnessPointsAc.Should().Contain(p => Math.Abs(p.Lux - 200) < 0.01 && p.Percent == 80);
        _cfg.AutoBrightnessPointsBattery.Should().NotContain(p => Math.Abs(p.Lux - 200) < 0.01 && p.Percent == 80,
            "правка на сети учит только сетевую кривую");
        int before = _ramps.Count;
        g.OnLux(200); // тот же свет — предсказание теперь 80, спорить не о чем
        _settle.Fire();
        g.Evaluate();
        _ramps.Count.Should().Be(before, "выученное значение уже стоит — не воюем");
    }

    [Fact]
    public void SeriesOfAdjustments_LearnsOnlyFinalValue()
    {
        using var g = NewGuard();
        g.OnLux(200);
        _settle.Fire();

        g.OnBrightness(65, own: false, settling: false); // крутит ползунок
        g.OnBrightness(72, own: false, settling: false);
        g.OnBrightness(78, own: false, settling: false);
        _learn.Fire();

        _cfg.AutoBrightnessPointsAc.Count(p => Math.Abs(p.Lux - 200) < 0.01).Should().Be(1);
        _cfg.AutoBrightnessPointsAc.Should().Contain(p => Math.Abs(p.Lux - 200) < 0.01 && p.Percent == 78);
    }

    [Fact]
    public void Curves_ArePerPowerSource()
    {
        using var g = NewGuard();
        _power.IsOnline = true;
        g.OnLux(200);
        _settle.Fire();                                        // сеть: подъехали к 60
        g.OnBrightness(60, own: true, settling: false);
        g.OnBrightness(90, own: false, settling: false);       // на сети хочу ярко
        _learn.Fire();

        _power.IsOnline = false;                               // перешли на батарею
        _ramps.Clear();
        g.Evaluate();                                          // как после смены питания

        _ramps.Should().Equal(new[] { (90, 60) },
            "батарейная кривая не училась — предсказание по её якорям, не по выученным 90");
    }

    [Fact]
    public void OwnEvents_DoNotTriggerLearning()
    {
        using var g = NewGuard();
        g.OnLux(200);
        _settle.Fire();

        g.OnBrightness(55, own: true, settling: false); // наш же шаг плавного хода

        _learn.Running.Should().BeFalse("свои записи — не правка человека");
    }

    [Fact]
    public void AdaptiveBrightness_SuppressesActions()
    {
        _adaptive = true;
        using var g = NewGuard();

        g.OnLux(2000);
        _settle.Fire();

        _ramps.Should().BeEmpty("с адаптивной яркостью Windows не спорим");
    }

    [Fact]
    public void Disabled_DoesNothing()
    {
        _cfg.AutoBrightness = false;
        using var g = NewGuard();

        g.OnLux(2000);

        _settle.Running.Should().BeFalse();
        _ramps.Should().BeEmpty();
    }

    [Fact]
    public void ClampSeam_LimitsPrediction()
    {
        _cfg.AutoBrightnessPointsAc.AddRange(BrightnessCurve.DefaultPoints());
        _cfg.AutoBrightnessPointsBattery.AddRange(BrightnessCurve.DefaultPoints());
        using var g = new AutoBrightnessGuard(_cfg, _power, _settle, _learn,
            () => _brightness, (f, t, _) => _ramps.Add((f, t)), _ => false,
            (level, _) => Math.Min(level, 70)); // лимит яркости как фильтр на выходе

        g.OnLux(2000); // кривая хочет 100
        _settle.Fire();

        _ramps.Should().Equal(new[] { (50, 70) }, "кривая хранит намерение, лимит — фильтр");
    }
}
