using FluentAssertions;
using XiControl.Config;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Лимит яркости (XIC-29): арифметика схождения, отличение своих записей от пользовательских
/// (OwnWrites), машина состояний guard-а на фейках — схождение, протест → пауза, сбросы.
/// Живая плавность хода и WMI — глазами.
/// </summary>
public sealed class BrightnessCapGuardTests
{
    private readonly AppConfig _cfg = new() { BrightnessCapEnabled = true, BrightnessCapAc = 60, BrightnessCapBattery = 60 };
    private readonly FakePowerEvents _power = new();
    private readonly FakeTimer _converge = new();
    private readonly FakeTimer _backoff = new();
    private readonly List<(int From, int To)> _ramps = [];
    private int? _brightness = 50;   // что вернёт «чтение яркости»
    private bool _adaptive;

    public BrightnessCapGuardTests() => Log.Enabled = false;

    private BrightnessCapGuard NewGuard() =>
        new(_cfg, _power, _converge, _backoff,
            () => _brightness, (f, t, _) => _ramps.Add((f, t)), _ => _adaptive);

    // ---- Чистая арифметика схождения ----

    [Theory]
    [InlineData(80, 60, 70)]  // разрыв 20 → половина
    [InlineData(70, 60, 65)]
    [InlineData(65, 60, 63)]  // ceil(5/2) = 3
    [InlineData(63, 60, 62)]
    [InlineData(62, 60, 60)]  // остаток ≤ 2 — доводим сразу
    [InlineData(61, 60, 60)]
    public void NextStep_HalvesGapAndSnaps(int current, int cap, int expected) =>
        BrightnessCapGuard.NextStep(current, cap, divisor: 2, snap: 2).Should().Be(expected);

    [Fact]
    public void NextStep_DivisorOne_StillConverges() =>
        // делитель 1 не сокращал бы разрыв — клэмпится к 2, схождение не стоит на месте
        BrightnessCapGuard.NextStep(80, 60, divisor: 1, snap: 2).Should().Be(70);

    // ---- Метки своих записей ----

    [Fact]
    public void OwnWrites_DuplicateEvent_IsStillOurs()
    {
        // WMI-события приходят с пула вразнобой и дублируются: метка живёт по TTL, а не
        // снимается первой проверкой — иначе дубль нашей записи читался бы как «пользователь
        // поднял» и замораживал схождение ложной паузой (поймано вживую на TM2424)
        var own = new OwnWrites();
        own.Note(70, nowMs: 1000);

        own.IsOwn(70, nowMs: 2000).Should().BeTrue("помеченное значение — наша запись");
        own.IsOwn(70, nowMs: 2500).Should().BeTrue("дубль события — всё ещё наша запись, не протест");
        own.IsOwn(55, nowMs: 2000).Should().BeFalse("чужое значение — пользователь");
    }

    [Fact]
    public void OwnWrites_ExpiredMark_IsNotOurs()
    {
        // метка живёт недолго: протухшая не должна проглотить настоящий
        // пользовательский выбор того же значения
        var own = new OwnWrites();
        own.Note(70, nowMs: 1000);

        own.IsOwn(70, nowMs: 1000 + 60_000).Should().BeFalse();
    }

    [Fact]
    public void OwnWrites_PassedRampStep_StopsBeingOursQuickly()
    {
        // Регрессия: шаг схождения 100 → 93 помечал КАЖДОЕ пройденное значение полным TTL,
        // и весь диапазон на десять секунд становился слепым. Клавиша «ярче» возвращает
        // яркость ровно в него — правка читалась как наша запись: ход не отменялся, уступка
        // не срабатывала, и «вверх» переставало работать вообще (поймано вживую на TM2424).
        var own = new OwnWrites();
        own.NoteStep(100, nowMs: 1000);   // ход вышел из 100 и ушёл ниже

        own.IsOwn(100, nowMs: 1200).Should().BeTrue("запоздалое эхо нашей же записи — ещё наше");
        own.IsOwn(100, nowMs: 4000).Should().BeFalse("ход давно прошёл мимо — это уже человек");
    }

    [Fact]
    public void OwnWrites_RampTarget_KeepsFullTtl()
    {
        // на цели хода мы стоим: дубли и запоздалые события по ней приходят и через секунды
        var own = new OwnWrites();
        own.NoteStep(93, nowMs: 1000);
        own.Note(93, nowMs: 1000);        // финальный шаг помечается обоими способами

        own.IsOwn(93, nowMs: 1000 + 8_000).Should().BeTrue("метку не укорачивает шаговая");
    }

    // ---- Машина состояний guard-а ----

    [Fact]
    public void UserAboveCap_ArmsConverge_ButDoesNotTouchYet()
    {
        using var g = NewGuard();

        g.OnBrightness(80, own: false, settling: false);

        _converge.Running.Should().BeTrue("превышение — ждём минуту, потом первый шаг");
        _ramps.Should().BeEmpty("не отматываем сразу — вежливый торг");
    }

    [Fact]
    public void ConvergeTicks_HalveGap_ThenFinishAtCap()
    {
        using var g = NewGuard();
        g.OnBrightness(80, own: false, settling: false);

        _converge.Fire();   // 80 → 70
        g.OnBrightness(70, own: true, settling: false); // событие нашего хода
        _converge.Fire();   // 70 → 65
        g.OnBrightness(65, own: true, settling: false);
        _converge.Fire();   // 65 → 63
        g.OnBrightness(63, own: true, settling: false);
        _converge.Fire();   // 63 → 62
        g.OnBrightness(62, own: true, settling: false);
        _converge.Fire();   // 62 → 60: финал

        _ramps.Should().Equal((80, 70), (70, 65), (65, 63), (63, 62), (62, 60));
        _converge.Running.Should().BeFalse("сошлись — следить больше нечего");
    }

    [Fact]
    public void RaiseAfterOurStep_BacksOff()
    {
        using var g = NewGuard();
        g.OnBrightness(80, own: false, settling: false);
        _converge.Fire(); // наш шаг 80 → 70 состоялся
        g.OnBrightness(70, own: true, settling: false);

        g.OnBrightness(90, own: false, settling: false); // «мне правда нужно ярче»

        _backoff.Running.Should().BeTrue("осознанный протест — отступаем");
        _converge.Running.Should().BeFalse();
        int before = _ramps.Count;
        _converge.Fire();
        _ramps.Should().HaveCount(before, "во время паузы яркость не трогаем");
    }

    [Fact]
    public void RaiseBeforeFirstStep_IsNotProtest()
    {
        using var g = NewGuard();
        g.OnBrightness(80, own: false, settling: false);

        g.OnBrightness(90, own: false, settling: false); // мы ещё ничего не отняли — просто ждём дальше

        _backoff.Running.Should().BeFalse();
        _converge.Running.Should().BeTrue();
    }

    [Fact]
    public void BackoffExpiry_ResumesConvergence()
    {
        using var g = NewGuard();
        g.OnBrightness(80, own: false, settling: false);
        _converge.Fire();
        g.OnBrightness(70, own: true, settling: false);
        g.OnBrightness(90, own: false, settling: false); // пауза

        _brightness = 90;
        _backoff.Fire(); // 2 часа вышли

        _backoff.Running.Should().BeFalse();
        _converge.Running.Should().BeTrue("всё ещё выше лимита — торг заново");
    }

    [Fact]
    public void ResetBackoff_ClearsPause()
    {
        using var g = NewGuard();
        g.OnBrightness(80, own: false, settling: false);
        _converge.Fire();
        g.OnBrightness(70, own: true, settling: false);
        g.OnBrightness(90, own: false, settling: false); // пауза

        g.ResetBackoff();          // блокировка/сон/смена питания
        _brightness = 90;
        g.Evaluate();

        _backoff.Running.Should().BeFalse();
        _converge.Running.Should().BeTrue("условия сменились — снова сходимся");
    }

    [Fact]
    public void UserBelowCap_NeverTouched()
    {
        using var g = NewGuard();

        g.OnBrightness(40, own: false, settling: false);
        g.OnBrightness(20, own: false, settling: false); // понижение — тем более не наше дело

        _converge.Running.Should().BeFalse();
        _ramps.Should().BeEmpty("мы никогда не поднимаем яркость");
    }

    [Fact]
    public void UserLoweringButStillAboveCap_KeepsConverging()
    {
        using var g = NewGuard();
        g.OnBrightness(80, own: false, settling: false);
        _converge.Fire();
        g.OnBrightness(70, own: true, settling: false);

        g.OnBrightness(65, own: false, settling: false); // сам пошёл навстречу — не протест

        _backoff.Running.Should().BeFalse();
        _converge.Running.Should().BeTrue();
        _converge.Fire();
        _ramps[^1].Should().Be((65, 63), "схождение продолжается от нового уровня");
    }

    [Fact]
    public void UserDropsBelowCap_EndsEpisode()
    {
        using var g = NewGuard();
        g.OnBrightness(80, own: false, settling: false);

        g.OnBrightness(55, own: false, settling: false);

        _converge.Running.Should().BeFalse("яркость в норме — эпизод закрыт");
    }

    [Fact]
    public void SettlingRaise_IsNotProtest()
    {
        // после смены питания яркость поднимает сама Windows — это не «мне нужно ярче»
        using var g = NewGuard();
        g.OnBrightness(80, own: false, settling: false);
        _converge.Fire();
        g.OnBrightness(70, own: true, settling: false);

        g.OnBrightness(95, own: false, settling: true);

        _backoff.Running.Should().BeFalse();
        _converge.Running.Should().BeTrue("превышение сводим, но без обид");
    }

    [Fact]
    public void AdaptiveBrightness_DisablesFeature()
    {
        _adaptive = true;
        using var g = NewGuard();

        _brightness = 90;
        g.Evaluate();

        _converge.Running.Should().BeFalse("с адаптивной яркостью вышла бы качель — не работаем");
    }

    [Fact]
    public void Disabled_DoesNothing()
    {
        _cfg.BrightnessCapEnabled = false;
        using var g = NewGuard();

        g.OnBrightness(95, own: false, settling: false);
        _brightness = 95;
        g.Evaluate();

        _converge.Running.Should().BeFalse();
        _ramps.Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_OnStart_ConvergesExistingExcess()
    {
        using var g = NewGuard();

        _brightness = 85; // старт приложения: яркость уже выше лимита
        g.Evaluate();

        _converge.Running.Should().BeTrue();
        _ramps.Should().BeEmpty("тем же вежливым механизмом — первый шаг через минуту");
    }

    // ---- Фильтр для «Запоминать яркость» ----

    [Fact]
    public void AllowsRemember_RejectsAboveCap_DoesNotClamp()
    {
        using var g = NewGuard();
        _power.IsOnline = true;

        g.AllowsRemember(55).Should().BeTrue();
        g.AllowsRemember(60).Should().BeTrue("ровно лимит — легально");
        g.AllowsRemember(80).Should().BeFalse("превышение не запоминается вообще (и не обрезается)");
    }

    [Fact]
    public void AllowsRemember_UsesCapOfCurrentPowerSource()
    {
        _cfg.BrightnessCapAc = 90;
        _cfg.BrightnessCapBattery = 50;
        using var g = NewGuard();

        _power.IsOnline = true;
        g.AllowsRemember(80).Should().BeTrue();
        _power.IsOnline = false;
        g.AllowsRemember(80).Should().BeFalse();
    }

    [Fact]
    public void AllowsRemember_CapDisabled_AllowsEverything()
    {
        _cfg.BrightnessCapEnabled = false;
        using var g = NewGuard();

        g.AllowsRemember(100).Should().BeTrue();
    }

    [Fact]
    public void ClampRestore_OldSlotDoesNotPierceNewCap()
    {
        using var g = NewGuard();

        g.ClampRestore(85, online: true).Should().Be(60, "слот из времён высокого лимита клампится при восстановлении");
        g.ClampRestore(55, online: true).Should().Be(55);
        _cfg.BrightnessCapEnabled = false;
        g.ClampRestore(85, online: true).Should().Be(85, "лимит выключен — слот восстанавливается как есть");
    }

    [Fact]
    public void Cap_ClampsHandEditedConfig()
    {
        _cfg.BrightnessCapAc = 0;    // погасило бы экран совсем
        _cfg.BrightnessCapBattery = 250;
        using var g = NewGuard();

        g.Cap(online: true).Should().Be(10);
        g.Cap(online: false).Should().Be(100);
    }
}
