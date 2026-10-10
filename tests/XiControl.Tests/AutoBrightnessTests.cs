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
    public void Learn_IndistinguishableLux_ReplacesNeighbour()
    {
        // глаз не люксметр: 100 и 110 лк неразличимы (меньше гистерезиса в лог-шкале) —
        // противоречивые мнения в такой близости растили бы «обрыв» на кривой
        var curve = DefaultCurve(out var pts);

        curve.Learn(100, 40);
        curve.Learn(110, 70); // тот же на глаз свет, новое настроение

        pts.Should().NotContain(p => Math.Abs(p.Lux - 100) < 0.01, "последнее слово побеждает");
        curve.Predict(110).Should().Be(70, "в этих условиях действует свежая правка");
        curve.Predict(105).Should().BeInRange(65, 70, "рядом — пологая интерполяция, а не ступенька 40→70");
    }

    [Fact]
    public void Learn_DistantPoints_Coexist()
    {
        // а вот честно различимые условия живут рядом — склейка не съедает настоящую кривую
        var curve = DefaultCurve(out var pts);

        curve.Learn(100, 40);
        curve.Learn(400, 70); // вчетверо светлее — другой мир

        pts.Should().Contain(p => Math.Abs(p.Lux - 100) < 0.01 && p.Percent == 40);
        pts.Should().Contain(p => Math.Abs(p.Lux - 400) < 0.01 && p.Percent == 70);
    }

    // ---- Файн-тюнинг: сглаживание уточняющих правок (XIC-32) ----

    [Fact]
    public void Learn_FineAdjustment_MovesHalfway()
    {
        // клавишами Windows доступны только 10-шаги, поэтому уточняющая правка означает
        // «хочу где-то между» — кривая встаёт посередине, а не прыгает за пользователем
        var curve = DefaultCurve(out _);

        curve.Learn(200, 65); // прежнее мнение об этом свете — 60

        curve.Predict(200).Should().Be(63);
    }

    [Fact]
    public void Learn_BigAdjustment_IsTakenLiterally()
    {
        // крупная правка — осознанная смена, а не поиск середины
        var curve = DefaultCurve(out _);

        curve.Learn(200, 85); // было 60, разница 25 — больше ступени

        curve.Predict(200).Should().Be(85);
    }

    [Fact]
    public void Learn_OscillationBetweenSteps_Converges()
    {
        // главный сценарий: 55 клавишами не выставить, человек скачет 65/55 —
        // без сглаживания кривая скакала бы вместе с ним на всю ступень
        var curve = DefaultCurve(out _);

        for (int i = 0; i < 4; i++)
        {
            curve.Learn(200, 65);
            curve.Learn(200, 55);
        }

        curve.Predict(200).Should().BeInRange(56, 62, "кривая держится между ступенями, а не мечется");
    }

    [Fact]
    public void Learn_Persistence_ReachesExactValue()
    {
        // настойчивость побеждает: повтор одного значения доводит кривую ровно до него
        // (округление AwayFromZero не даёт застрять на половине пути)
        var curve = DefaultCurve(out _);

        for (int i = 0; i < 6; i++) curve.Learn(200, 65);

        curve.Predict(200).Should().Be(65);
    }

    [Fact]
    public void Learn_Smoothing_KeepsCurveMonotonic()
    {
        var curve = DefaultCurve(out var pts);

        for (int i = 0; i < 3; i++) { curve.Learn(200, 65); curve.Learn(700, 75); }

        var sorted = pts.OrderBy(p => p.Lux).ToArray();
        for (int i = 1; i < sorted.Length; i++)
            sorted[i].Percent.Should().BeGreaterThan(sorted[i - 1].Percent, "инвариант монотонности");
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
    private readonly FakeTimer _converge = new();
    private readonly FakeTimer _backoff = new();
    private readonly List<(int From, int To)> _ramps = [];
    private int? _brightness = 50;
    private bool _adaptive;

    private AutoBrightnessGuard NewGuard(Func<long>? clock = null)
    {
        if (_cfg.AutoBrightnessPointsAc.Count == 0)
            _cfg.AutoBrightnessPointsAc.AddRange(BrightnessCurve.DefaultPoints());
        if (_cfg.AutoBrightnessPointsBattery.Count == 0)
            _cfg.AutoBrightnessPointsBattery.AddRange(BrightnessCurve.DefaultPoints());
        return new AutoBrightnessGuard(_cfg, _power, _settle, _learn,
            () => _brightness, (f, t, _) => { _ramps.Add((f, t)); return Task.CompletedTask; }, _ => _adaptive, (l, _) => l, clock,
            _converge, _backoff);
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
    public void SampleStream_SameLight_DoesNotRearmSettle()
    {
        // Датчик отдаёт сэмплы чаще (≈1.5 с), чем длится дебаунс стабилизации (2 с): если бы
        // каждый сэмпл перевзводил таймер, тот не дотикивал бы НИКОГДА и фича была бы мертва
        // с самого старта (база гистерезиса пуста → любой сэмпл «значим»). Живой баг XIC-36.
        using var g = NewGuard();

        g.OnLux(2000);
        g.OnLux(2000);
        g.OnLux(2000);

        _settle.Starts.Should().Be(1, "тот же свет не перевзводит дебаунс — таймер должен дотикать");
        _settle.Fire();
        _ramps.Should().Equal(new[] { (50, 100) }, "после стабилизации яркость едет к предсказанию");
    }

    [Fact]
    public void DriftingLight_RearmsSettle_UntilItSettles()
    {
        // Свет продолжает значимо меняться (рассвет) — вот ТУТ перевзвод оправдан: ждём финала
        using var g = NewGuard();

        g.OnLux(100);
        g.OnLux(400);   // значимый сдвиг относительно взведённых 100

        _settle.Starts.Should().Be(2, "продолжающееся изменение отодвигает стабилизацию");
        _settle.Fire();
        _ramps.Should().ContainSingle("реакция одна — на устоявшийся свет");
    }

    [Fact]
    public void LightReturns_BeforeSettle_ArmClears()
    {
        // Свет скакнул и вернулся к исходному до стабилизации: взвод очищается, и следующий
        // настоящий сдвиг взводит дебаунс заново (а не молчит из-за протухшей базы взвода)
        using var g = NewGuard();
        g.OnLux(100);
        _settle.Fire();      // отработали сотню: acted = 100

        g.OnLux(2000);       // скачок...
        g.OnLux(100);        // ...и назад — незначимо относительно acted
        int before = _settle.Starts;

        g.OnLux(2000);       // настоящий сдвиг после отката
        _settle.Starts.Should().Be(before + 1, "откат света очистил взвод — новый сдвиг взводит заново");
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

    // ---- Обучение выключено (XIC-37): кривая — авторитет, правки временные ----

    // Подготовка эпизода: свет 200 лк, мы подъехали к предсказанию 60, пользователь ставит 80.
    private AutoBrightnessGuard FrozenGuardWithTweak()
    {
        _cfg.AutoBrightnessLearning = false;
        var g = NewGuard();
        g.OnLux(200);
        _settle.Fire();                                  // 50 → 60 (предсказание)
        g.OnBrightness(60, own: true, settling: false);
        g.OnBrightness(80, own: false, settling: false); // «временно поярче»
        return g;
    }

    [Fact]
    public void LearningOff_Tweak_KeepsCurve_AndBargainsBack()
    {
        using var g = FrozenGuardWithTweak();

        _learn.Running.Should().BeFalse("кривая заморожена — период раздумья не взводится");
        _cfg.AutoBrightnessPointsAc.Single(p => Math.Abs(p.Lux - 200) < 0.01).Percent
            .Should().Be(60, "урок не записан — якорь остался дефолтным");
        _converge.Running.Should().BeTrue("через минуту начнётся мягкий возврат к выученному");

        _converge.Fire(); // первый шаг: разрыв 20 сокращается вдвое
        _ramps[^1].Should().Be((80, 70));
    }

    [Fact]
    public void LearningOff_SecondTweak_AfterOurStep_Yields()
    {
        using var g = FrozenGuardWithTweak();
        _converge.Fire();                                // наш шаг 80 → 70...
        g.OnBrightness(70, own: true, settling: false);  // ...доехал

        int before = _ramps.Count;
        g.OnBrightness(85, own: false, settling: false); // пользователь настоял

        _converge.Running.Should().BeFalse("уступили — торг остановлен");
        _backoff.Running.Should().BeTrue("пауза до таймера или блокировки");
        _converge.Fire();
        g.Evaluate();
        _ramps.Count.Should().Be(before, "во время уступки яркость не трогаем вообще");
    }

    [Fact]
    public void LearningOff_Unlock_ReturnsToLearned()
    {
        using var g = FrozenGuardWithTweak();
        _converge.Fire();
        g.OnBrightness(70, own: true, settling: false);
        g.OnBrightness(85, own: false, settling: false); // уступка

        g.ResetBackoff(); // разблокировка сеанса (проводка — PowerProfileGuard)
        g.Evaluate();

        _ramps[^1].Should().Be((85, 60), "после разблокировки датчик приводит к выученному");
    }

    [Fact]
    public void LearningOff_BackoffExpiry_ReturnsToLearned()
    {
        using var g = FrozenGuardWithTweak();
        _converge.Fire();
        g.OnBrightness(70, own: true, settling: false);
        g.OnBrightness(85, own: false, settling: false); // уступка

        _backoff.Fire(); // пауза вышла

        _ramps[^1].Should().Be((85, 60));
    }

    [Theory]
    [InlineData(80, 60, 70)]  // вниз: как NextStep лимита
    [InlineData(20, 60, 40)]  // вверх: симметрично
    [InlineData(62, 60, 60)]  // остаток ≤ snap — доводим сразу
    [InlineData(58, 60, 60)]
    public void StepToward_Bidirectional(int current, int want, int expected) =>
        AutoBrightnessGuard.StepToward(current, want, divisor: 2, snap: 2).Should().Be(expected);

    [Fact]
    public void RevertOff_TweakJustStays()
    {
        _cfg.AutoBrightnessRevert = "off";
        using var g = FrozenGuardWithTweak();

        _converge.Running.Should().BeFalse("возврат выключен — правка живёт до смены света");
        _cfg.AutoBrightnessPointsAc.Single(p => Math.Abs(p.Lux - 200) < 0.01).Percent
            .Should().Be(60, "и кривая по-прежнему не тронута");
    }

    [Fact]
    public void RevertBatteryOnly_SilentOnAc_ActiveOnBattery()
    {
        _cfg.AutoBrightnessRevert = "battery";
        _power.IsOnline = true;
        using var g = FrozenGuardWithTweak();
        _converge.Running.Should().BeFalse("от сети правка не возвращается");

        _power.IsOnline = false;                          // на батарее — возврат работает
        g.OnBrightness(80, own: false, settling: false);
        _converge.Running.Should().BeTrue("на батарее схождение взводится");
    }

    [Fact]
    public void RevertModeSwitch_MidEpisode_HaltsConverge()
    {
        using var g = FrozenGuardWithTweak();             // эпизод открыт (режим «всегда»)
        _cfg.AutoBrightnessRevert = "off";                // выключили возврат на ходу

        int before = _ramps.Count;
        _converge.Fire();

        _ramps.Count.Should().Be(before, "тик при выключенном возврате гасит эпизод, а не шагает");
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

    // ---- Ворота потока люксов (XIC-76) ----

    // Ворота, уже принявшие уровень: старт, пауза, кандидат продержался.
    private static LuxGate GateAt(float lux, out long now)
    {
        var gate = new LuxGate();
        gate.Restart(0);
        for (now = LuxGate.SettleMs; now <= LuxGate.SettleMs + LuxGate.ConfirmMs; now += 1500)
            gate.Accept(now, lux);
        gate.Accept(now, lux).Should().Be(lux, "предусловие: уровень принят");
        return gate;
    }

    [Fact]
    public void LuxGate_FirstLevel_NeedsSettleAndConfirm()
    {
        var gate = new LuxGate();
        gate.Restart(0);

        gate.Accept(1000, 180).Should().Be(float.NaN, "пауза после старта сенсора");
        gate.Accept(LuxGate.SettleMs, 180).Should().Be(float.NaN, "первое значение — тоже кандидат");
        gate.Accept(LuxGate.SettleMs + LuxGate.ConfirmMs, 180).Should().Be(180, "продержалось — принято");
    }

    // Живая запись с TM2424 после сна: 92 и 0 со свежими метками, мусор живёт 1–2 с.
    [Fact]
    public void LuxGate_PostResumeGarbage_NeverReachesConsumer()
    {
        var gate = GateAt(181, out long t);

        gate.Accept(t + 1000, 92).Should().Be(181);
        gate.Accept(t + 2000, 0).Should().Be(181);
        gate.Accept(t + 3000, 0).Should().Be(181, "ноль держится лишь 1 с из нужных 3");
        gate.Accept(t + 4000, 181).Should().Be(181);
    }

    [Fact]
    public void LuxGate_SteadyAlternation_StaysOnLevel()
    {
        var gate = GateAt(181, out long t);

        // худший случай из лога: мусор ~50 % потока, чередование каждую секунду, 5 минут
        for (int i = 1; i <= 300; i++)
            gate.Accept(t + i * 1000, i % 2 == 0 ? 0 : 181).Should().Be(181);
    }

    // Датчик шлёт новое чтение лишь при смене света: в погасшей комнате — одно «0»,
    // дальше опрос видит его копию. Подтверждение обязано прийти временем.
    [Fact]
    public void LuxGate_RealDarkness_FrozenReading_AcceptedByTime()
    {
        var gate = GateAt(180, out long t);

        gate.Accept(t + 1500, 0).Should().Be(180, "одиночный ноль неотличим от мусора");
        gate.Accept(t + 3000, 0).Should().Be(180);
        gate.Accept(t + 4500, 0).Should().Be(0, "ноль продержался 3 с — свет правда погас");
        gate.Accept(t + 6000, 0).Should().Be(0);
    }

    [Fact]
    public void LuxGate_SlowDrift_PassesWithoutDelay()
    {
        var gate = GateAt(200, out long t);

        // сумерки: каждое чтение в пределах допуска от предыдущего
        gate.Accept(t + 1500, 185).Should().Be(185);
        gate.Accept(t + 3000, 165).Should().Be(165);
        gate.Accept(t + 4500, 140).Should().Be(140);
    }

    [Fact]
    public void LuxGate_RealJump_ArrivesAfterConfirm()
    {
        var gate = GateAt(200, out long t);

        gate.Accept(t + 1500, 1500).Should().Be(200, "включили яркий свет — ждём подтверждения");
        gate.Accept(t + 3000, 1450).Should().Be(200, "кандидат держится (в допуске)");
        gate.Accept(t + 4500, 1520).Should().Be(1520, "3 с — принято");
    }

    // Ночная трассировка 10.10: крышку закрыли в темноте (0 лк), открыли утром — первые 8 с
    // после пробуждения датчик нёс 836 → 457 → 10 → 55 → 10 → 2 → 4 → 37, затем устоялся 48.
    [Fact]
    public void LuxGate_Restart_ForgetsStaleLevel_AndRejectsWakeTransient()
    {
        var gate = GateAt(0, out long t);

        gate.Restart(t);
        float[] transient = [836, 457, 10, 55, 10, 2, 4, 37];
        for (int i = 0; i < transient.Length; i++)
            gate.Accept(t + i * 1000, transient[i]).Should().Be(float.NaN,
                "темнота до сна устарела, а мусор старта не подтверждается — наружу ничего");

        gate.Accept(t + 8000, 48).Should().Be(float.NaN);
        gate.Accept(t + 8000 + LuxGate.ConfirmMs, 48).Should().Be(48, "свежий свет продержался 3 с");
    }

    // ---- Забыть свет после сна (XIC-76) ----

    // Трассировка 10.10: через секунду после пробуждения пересчёт по разблокировке увёл экран
    // «46% → 10% (0 лк)» — по темноте, в которой крышку закрыли вечером.
    [Fact]
    public void Guard_ForgetLux_WakeEvaluateDoesNotUseStaleDarkness()
    {
        using var g = NewGuard();
        g.OnLux(0);
        _settle.Fire();                  // вечер: под темноту уже подстроились
        _ramps.Clear();
        _brightness = 46;                // утром яркость другая

        g.ForgetLux();                   // проснулись
        g.Evaluate();                    // пересчёт по разблокировке

        _ramps.Should().BeEmpty("свет неизвестен — экран не трогаем");
        _settle.Running.Should().BeFalse();
    }

    [Fact]
    public void Guard_ForgetLux_FirstFreshLevel_IsSignificantEvenIfSameAsBefore()
    {
        using var g = NewGuard();
        g.OnLux(0);
        _settle.Fire();
        g.ForgetLux();

        // свет тот же, что до сна, но яркость за ночь могли поменять — сверка обязана случиться
        g.OnLux(0);

        _settle.Running.Should().BeTrue("база гистерезиса сброшена вместе со светом");
    }

    [Theory]
    [InlineData(1_500, false)]   // обычный проход цикла
    [InlineData(9_000, false)]   // тяжёлый затык планировщика — ещё не сон
    [InlineData(136_000, true)]  // Modern Standby 18:39:51 → 18:42:07 из трассировки
    public void AlsWatcher_SleepGap_DetectedByPollPause(int gapMs, bool slept) =>
        AlsWatcher.IsSleepGap(TimeSpan.FromMilliseconds(gapMs)).Should().Be(slept);

    // ---- Медианный фильтр («инерция») ----

    [Fact]
    public void MedianWindow_SpikeDoesNotMoveMedian()
    {
        var f = new MedianWindow();
        f.Add(0, 200, 10_000);
        f.Add(1500, 200, 10_000);
        f.Add(3000, 200, 10_000);
        f.Add(4500, 9000, 10_000); // блик фарой в датчик

        f.Median(4500, 10_000).Should().Be(200, "медиану одиночный выброс не сдвигает вообще");
    }

    [Fact]
    public void MedianWindow_SustainedChange_WinsAfterHalfWindow()
    {
        var f = new MedianWindow();
        for (long t = 0; t <= 4500; t += 1500) f.Add(t, 200, 10_000);
        for (long t = 6000; t <= 12_000; t += 1500) f.Add(t, 2000, 10_000); // свет реально включили

        f.Median(12_000, 10_000).Should().Be(2000, "устойчивое изменение прожило больше половины окна");
    }

    [Fact]
    public void MedianWindow_OldSamplesFallOut()
    {
        var f = new MedianWindow();
        f.Add(0, 9000, 5_000);
        f.Add(10_000, 200, 5_000);

        f.Median(10_000, 5_000).Should().Be(200, "старый сэмпл выпал из окна");
    }

    [Fact]
    public void Guard_GlareSpike_DoesNotTouchScreen()
    {
        _cfg.AutoBrightnessMedianSec = 10;
        long now = 0;
        using var g = NewGuard(() => now);
        for (now = 0; now <= 4500; now += 1500) g.OnLux(200); // комнатный свет устоялся
        _settle.Fire();
        g.OnBrightness(60, own: true, settling: false);
        int before = _ramps.Count;

        now = 6000;
        g.OnLux(9000); // случайный блик — один сэмпл

        _settle.Running.Should().BeFalse("медиана окна не сдвинулась — реакции нет");
        _ramps.Should().HaveCount(before);
    }

    [Fact]
    public void ClampSeam_LimitsPrediction()
    {
        _cfg.AutoBrightnessPointsAc.AddRange(BrightnessCurve.DefaultPoints());
        _cfg.AutoBrightnessPointsBattery.AddRange(BrightnessCurve.DefaultPoints());
        using var g = new AutoBrightnessGuard(_cfg, _power, _settle, _learn,
            () => _brightness, (f, t, _) => { _ramps.Add((f, t)); return Task.CompletedTask; }, _ => false,
            (level, _) => Math.Min(level, 70)); // лимит яркости как фильтр на выходе

        g.OnLux(2000); // кривая хочет 100
        _settle.Fire();

        _ramps.Should().Equal(new[] { (50, 70) }, "кривая хранит намерение, лимит — фильтр");
    }
}
