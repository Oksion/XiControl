using FluentAssertions;
using XiControl.Config;
using XiControl.Localization;
using XiControl.SystemIntegration;
using XiControl.Ui;
using XiControl.Wmi;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// AppController — командный слой на фейках (план 2.1). Покрыта чистая логика:
/// заряд/«в дорогу»/режимы/стратегии старта/профили. Ветки, дёргающие реестр,
/// schtasks, яркость и живой ChangeDisplaySettings, юнитами не покрываем (план 3.3).
/// </summary>
public sealed class AppControllerTests
{
    private readonly FakeMifsClient _mifs = new();
    private readonly FakePowerEvents _power = new();
    private readonly AppConfig _cfg = new();
    private readonly List<string> _events = [];
    private readonly AppController _c;

    public AppControllerTests()
    {
        Log.Enabled = false;
        _c = new AppController(_mifs, _cfg, _power, new Localizer(),
            new ChargeGuard(_mifs, _power, () => _cfg.ChargeCare ? _cfg.CarePercent() : 100, new FakeTimer()),
            new RefreshRateGuard(_cfg, _power, new FakeTimer()),
            new PowerProfileGuard(_mifs, _cfg, _power, new FakeTimer()),
            new TravelChargeMonitor(_cfg, _power, new FakeTimer()),
            new TouchpadControl(_cfg), new TouchscreenControl(_cfg))
        {
            CareChanged = on => _events.Add($"care:{on}"),
            TravelChanged = on => _events.Add($"travel:{on}"),
            TravelCancelled = () => _events.Add("travel-cancelled"),
            ModeSet = m => _events.Add($"set:{m}"),
            ModeCycled = m => _events.Add($"cycle:{m}"),
            ProfileModeApplied = () => _events.Add("profile-applied"),
            ModesReloaded = () => _events.Add("modes-reloaded"),
        };
    }

    // ---- Заряд / «в дорогу» ----

    [Fact]
    public void ToggleCare_WritesFirmwareAndConfig_AndNotifies()
    {
        _c.ToggleCare(true);

        _mifs.ChargeLimitCalls.Should().Equal(80);
        _cfg.ChargeCare.Should().BeTrue();
        _events.Should().Equal("care:True");
    }

    [Fact]
    public void ToggleCare_CancelsActiveTravel()
    {
        _cfg.TravelMode = true;

        _c.ToggleCare(false);

        _cfg.TravelMode.Should().BeFalse("ручная смена лимита отменяет «в дорогу»");
    }

    [Fact]
    public void SetTravel_RequiresChargeCare()
    {
        _cfg.ChargeCare = false;

        _c.SetTravel(true);

        _cfg.TravelMode.Should().BeFalse();
        _events.Should().BeEmpty();
    }

    [Fact]
    public void SetTravel_On_DropsFirmwareProtection()
    {
        _cfg.ChargeCare = true;

        _c.SetTravel(true);

        _mifs.ChargeLimitCalls.Should().Equal(100); // снять защиту — заряд до 100%
        _cfg.TravelMode.Should().BeTrue();
        _events.Should().Equal("travel:True");
    }

    [Fact]
    public void SetTravel_Off_RestoresBaseCare()
    {
        _cfg.ChargeCare = true;
        _cfg.TravelMode = true;

        _c.SetTravel(false);

        _mifs.ChargeLimitCalls.Should().Equal(80); // вернуть «беречь 80%»
        _events.Should().Equal("travel:False");
    }

    [Fact]
    public void SetCareLimit_WhenCareActive_AppliesToFirmware()
    {
        _cfg.ChargeCare = true;

        _c.SetCareLimit(60);

        _mifs.ChargeLimitCalls.Should().Equal(60);
        _cfg.CareLimitPercent.Should().Be(60);
        _events.Should().Equal("care:True");
    }

    [Fact]
    public void SetCareLimit_WhenCareOff_OnlyRemembers()
    {
        _cfg.ChargeCare = false;

        _c.SetCareLimit(50);

        _mifs.ChargeLimitCalls.Should().BeEmpty("защита выключена — железо не трогаем, порог ждёт включения");
        _cfg.CareLimitPercent.Should().Be(50);
    }

    [Fact]
    public void SetCareLimit_DuringTravel_OnlyRemembers()
    {
        _cfg.ChargeCare = true;
        _cfg.TravelMode = true;   // «в дорогу» временно держит 100% — не перебиваем его

        _c.SetCareLimit(40);

        _mifs.ChargeLimitCalls.Should().BeEmpty();
        _cfg.CareLimitPercent.Should().Be(40);
    }

    [Fact]
    public void SetCareLimit_UnsupportedPercent_IsIgnored()
    {
        _c.SetCareLimit(90);   // прошивка такого уровня не держит (docs/12)

        _mifs.ChargeLimitCalls.Should().BeEmpty("неизвестный уровень не пишем вслепую");
        _cfg.CareLimitPercent.Should().Be(80, "выбор не изменился");
    }

    [Fact]
    public void SetCareLimit_FirmwareRejects_KeepsPreviousChoice()
    {
        bool failed = false;
        _c.FirmwareFailed = () => failed = true;
        _cfg.ChargeCare = true;
        _mifs.SetChargeLimitResult = false;   // модель без granular — прошивка отвергла уровень

        _c.SetCareLimit(40);

        _cfg.CareLimitPercent.Should().Be(80, "откатываем выбор — на железе он не применился");
        failed.Should().BeTrue();
    }

    // ---- Примирение порога с прошивкой (XIC-17) ----

    [Fact]
    public void SyncCare_AdoptsExternalLimit_Silently()
    {
        _cfg.ChargeCare = true;
        _mifs.ChargeLimit = 50;   // порог сменили снаружи (Xiaomi PC Manager)

        _c.SyncCareFromFirmware().Should().BeTrue();

        _cfg.CareLimitPercent.Should().Be(50, "источник истины — прошивка (docs/12)");
        _cfg.ChargeCare.Should().BeTrue();
        _events.Should().BeEmpty("подхват чужого значения — не действие пользователя: OSD не показываем");
    }

    [Fact]
    public void SyncCare_ProtectionOffOutside_KeepsChosenPercent()
    {
        _cfg.ChargeCare = true;
        _mifs.ChargeLimit = 100;   // снаружи защиту выключили

        _c.SyncCareFromFirmware().Should().BeTrue();

        _cfg.ChargeCare.Should().BeFalse();
        _cfg.CareLimitPercent.Should().Be(80, "100% — это «выключено», а не порог: выбранный X храним");
    }

    [Fact]
    public void SyncCare_FirmwareSilent_KeepsConfig()
    {
        _cfg.ChargeCare = true;
        _mifs.ChargeLimit = null;   // прошивка не ответила

        _c.SyncCareFromFirmware().Should().BeFalse();

        _cfg.ChargeCare.Should().BeTrue("лучше показать своё значение, чем затереть его нулём");
        _cfg.CareLimitPercent.Should().Be(80);
    }

    [Fact]
    public void SyncCare_DuringTravel_DoesNothing()
    {
        _cfg.ChargeCare = true;
        _cfg.TravelMode = true;
        _mifs.ChargeLimit = 100;   // «в дорогу» и держит 100% — это наше состояние, не внешнее

        _c.SyncCareFromFirmware().Should().BeFalse();

        _cfg.ChargeCare.Should().BeTrue("иначе примирение сломало бы «в дорогу»");
        _cfg.TravelMode.Should().BeTrue();
    }

    [Fact]
    public void SyncCare_UnknownPercent_KeepsChoice()
    {
        _cfg.ChargeCare = true;
        _mifs.ChargeLimit = 90;   // такого уровня в наборе нет (docs/12)

        _c.SyncCareFromFirmware().Should().BeFalse();

        _cfg.CareLimitPercent.Should().Be(80, "неизвестный уровень в конфиг не принимаем");
    }

    [Fact]
    public void SyncCare_AlreadyInSync_ReportsNoChange()
    {
        _cfg.ChargeCare = true;
        _mifs.ChargeLimit = 80;   // ровно то, что в конфиге

        _c.SyncCareFromFirmware().Should().BeFalse("без отличий не пишем конфиг на каждое открытие панели");
    }

    [Fact]
    public void DisableTravel_IsSilentReset()
    {
        _cfg.TravelMode = true;

        _c.DisableTravel();

        _cfg.TravelMode.Should().BeFalse();
        _mifs.ChargeLimitCalls.Should().BeEmpty("защиту вернёт ChargeGuard по событию питания");
        _events.Should().Equal("travel-cancelled");
    }

    // ---- Режимы ----

    [Fact]
    public void SetMode_AppliesRemembersAndNotifies()
    {
        _cfg.RestoreMode = true;

        _c.SetMode(PerfMode.Turbo);

        _mifs.PerfModeCalls.Should().Equal(PerfMode.Turbo);
        _cfg.StartPerfMode.Should().Be(PerfMode.Turbo);
        _events.Should().Equal("set:Turbo");
    }

    [Fact]
    public void CycleMode_AdvancesByPowerOrder()
    {
        _mifs.Mode = PerfMode.Quiet; // Eco, Quiet, Auto, Turbo, FullSpeed

        _c.CycleMode();

        _mifs.PerfModeCalls.Should().Equal(PerfMode.Auto);
        _events.Should().Equal("cycle:Auto");
    }

    [Fact]
    public void CycleMode_WrapsAround()
    {
        _mifs.Mode = PerfMode.FullSpeed;

        _c.CycleMode();

        _mifs.PerfModeCalls.Should().Equal(PerfMode.Eco);
    }

    [Fact]
    public void CycleMode_SkipsHiddenModes()
    {
        _c.ToggleModeVisibility(eco: false, full: false); // остаются Quiet, Auto, Turbo
        _events.Clear();
        _mifs.Mode = PerfMode.Turbo;

        _c.CycleMode();

        _mifs.PerfModeCalls.Should().Equal(PerfMode.Quiet); // wrap мимо скрытых
    }

    [Fact]
    public void CycleMode_UnknownCurrent_TreatedAsAuto()
    {
        _mifs.Mode = null; // прошивка не ответила

        _c.CycleMode();

        _mifs.PerfModeCalls.Should().Equal(PerfMode.Turbo); // после Auto
    }

    [Fact]
    public void ToggleModeVisibility_UpdatesVisibleModes_AndNotifies()
    {
        _c.ToggleModeVisibility(eco: false, full: true);

        _c.VisibleModes.Should().Equal(PerfMode.Quiet, PerfMode.Auto, PerfMode.Turbo, PerfMode.FullSpeed);
        _cfg.EcoMode.Should().BeFalse();
        _events.Should().Equal("modes-reloaded");
    }

    // ---- Честная обратная связь (Фаза 6.2): прошивка отказала → конфиг не трогаем,
    // «успех» не показываем, UI получает FirmwareFailed ----

    [Fact]
    public void ToggleCare_FirmwareFailure_KeepsConfigAndReportsError()
    {
        bool failed = false;
        _c.FirmwareFailed = () => failed = true;
        _mifs.ThrowOnSetChargeLimit = true;
        _cfg.TravelMode = true;

        _c.ToggleCare(true);

        _cfg.ChargeCare.Should().BeFalse("состояние прошивки не изменилось — конфиг не трогаем");
        _cfg.TravelMode.Should().BeTrue("«в дорогу» не отменяем, если команда не прошла");
        _events.Should().BeEmpty("оптимистичный «успех» не показываем");
        failed.Should().BeTrue();
    }

    [Fact]
    public void SetTravel_FirmwareFailure_KeepsTravelOff()
    {
        bool failed = false;
        _c.FirmwareFailed = () => failed = true;
        _cfg.ChargeCare = true;
        _mifs.ThrowOnSetChargeLimit = true;

        _c.SetTravel(true);

        _cfg.TravelMode.Should().BeFalse();
        _events.Should().BeEmpty();
        failed.Should().BeTrue();
    }

    [Fact]
    public void SetMode_FirmwareRejects_DoesNotRememberAndReportsError()
    {
        bool failed = false;
        _c.FirmwareFailed = () => failed = true;
        _mifs.SetPerfModeResult = false; // прошивка вернула отказ (напр. Full-speed на батарее)
        _cfg.RestoreMode = true;

        _c.SetMode(PerfMode.Turbo);

        _cfg.StartPerfMode.Should().BeNull("непринятый режим не запоминаем");
        _events.Should().BeEmpty();
        failed.Should().BeTrue();
    }

    // ---- Стратегии старта ----

    [Fact]
    public void StartStrategy_AreMutuallyExclusive()
    {
        _mifs.Mode = PerfMode.Turbo;

        _c.SetStartStrategy(StartStrategy.Restore);
        (_cfg.RestoreMode, _cfg.ForceStartMode, _cfg.PowerProfiles).Should().Be((true, null, false));
        _cfg.StartPerfMode.Should().Be(PerfMode.Turbo, "при первом включении запоминаем текущий режим");

        _c.SetStartStrategy(StartStrategy.Pin);
        (_cfg.RestoreMode, _cfg.ForceStartMode, _cfg.PowerProfiles).Should().Be((false, PerfMode.Turbo, false));

        _c.SetStartStrategy(StartStrategy.Profiles);
        (_cfg.RestoreMode, _cfg.ForceStartMode, _cfg.PowerProfiles).Should().Be((false, null, true));

        _c.SetStartStrategy(StartStrategy.None);
        (_cfg.RestoreMode, _cfg.ForceStartMode, _cfg.PowerProfiles).Should().Be((false, null, false));
    }

    [Fact]
    public void CurrentStartStrategy_MirrorsSetStrategy()
    {
        _mifs.Mode = PerfMode.Turbo;
        _c.CurrentStartStrategy.Should().Be(StartStrategy.None);

        foreach (var s in new[] { StartStrategy.Restore, StartStrategy.Pin, StartStrategy.Profiles, StartStrategy.None })
        {
            _c.SetStartStrategy(s);
            _c.CurrentStartStrategy.Should().Be(s, "радио-карточки настроек рисуются по этому свойству");
        }
    }

    [Fact]
    public void PinStrategy_CannotPinAuto()
    {
        _mifs.Mode = PerfMode.Auto;

        _c.SetStartStrategy(StartStrategy.Pin);

        _cfg.ForceStartMode.Should().BeNull("Авто закреплять нечего");
    }

    // ---- Профили ----

    [Fact]
    public void SetProfileMode_CurrentPower_AppliesImmediately()
    {
        _power.IsOnline = true;

        _c.SetProfileMode(ac: true, PerfMode.Turbo);

        _cfg.AcPerfMode.Should().Be(PerfMode.Turbo);
        _mifs.PerfModeCalls.Should().Equal(PerfMode.Turbo);
        _events.Should().Equal("profile-applied");
    }

    [Fact]
    public void SetProfileMode_OtherPower_OnlySaves()
    {
        _power.IsOnline = true;

        _c.SetProfileMode(ac: false, PerfMode.Quiet); // профиль батареи, а мы на сети

        _cfg.BatteryPerfMode.Should().Be(PerfMode.Quiet);
        _mifs.PerfModeCalls.Should().BeEmpty();
        _events.Should().BeEmpty();
    }

    [Fact]
    public void SetProfileMode_Rejected_FallsBackToAuto()
    {
        _power.IsOnline = false;
        _mifs.SetPerfModeResult = false;

        _c.SetProfileMode(ac: false, PerfMode.FullSpeed); // Full-speed на батарее не примут

        _mifs.PerfModeCalls.Should().Equal(PerfMode.FullSpeed, PerfMode.Auto);
    }

    // ---- Герцовка (только выключение — включение дёргает живой ChangeDisplaySettings) ----

    [Fact]
    public void ToggleAutoHz_Off_SavesAndNotifies()
    {
        _cfg.AutoRefreshRate = true;
        _c.AutoHzChanged = on => _events.Add($"hz:{on}");

        _c.ToggleAutoHz(false);

        _cfg.AutoRefreshRate.Should().BeFalse();
        _events.Should().Equal("hz:False");
    }

    // «Управление частотой» как фича — только флаг + колбэк; AutoRefreshRate держим выключенным,
    // чтобы не дёрнуть живой ChangeDisplaySettings (тот же приём, что и с ToggleAutoHz выше).
    [Fact]
    public void ToggleRefreshRateFeature_Off_SavesAndNotifies()
    {
        _cfg.RefreshRateFeature = true;
        _cfg.AutoRefreshRate = false;
        bool changed = false;
        _c.RefreshRateFeatureChanged = () => changed = true;

        _c.ToggleRefreshRateFeature(false);

        _cfg.RefreshRateFeature.Should().BeFalse();
        changed.Should().BeTrue();
    }

    [Fact]
    public void ToggleRefreshRateFeature_On_SavesAndNotifies()
    {
        _cfg.RefreshRateFeature = false;
        _cfg.AutoRefreshRate = false;
        bool changed = false;
        _c.RefreshRateFeatureChanged = () => changed = true;

        _c.ToggleRefreshRateFeature(true);

        _cfg.RefreshRateFeature.Should().BeTrue();
        changed.Should().BeTrue();
    }

    // Скрытие фичи гасит активную авто-герцовку (прецедент ToggleOwlFeature/Awake): иначе
    // флаг оставался бы «взведённым» без UI — OSD питания врал бы «• N Гц», а повторное
    // включение фичи молча возобновляло бы переключения. AcRefreshRate=0 — чтобы фоновое
    // восстановление сеть-частоты упёрлось в guard Apply (hz<=0) и не тронуло живой экран.
    [Fact]
    public void ToggleRefreshRateFeature_Off_DisarmsActiveAutoHz()
    {
        _cfg.RefreshRateFeature = true;
        _cfg.AutoRefreshRate = true;
        _cfg.AcRefreshRate = 0;

        _c.ToggleRefreshRateFeature(false);

        _cfg.RefreshRateFeature.Should().BeFalse();
        _cfg.AutoRefreshRate.Should().BeFalse("скрытая фича не должна оставлять авто-герцовку взведённой");
    }
}
