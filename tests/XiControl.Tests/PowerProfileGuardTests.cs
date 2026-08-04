using FluentAssertions;
using Microsoft.Win32;
using XiControl.Config;
using XiControl.SystemIntegration;
using XiControl.Wmi;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// PowerProfileGuard — применение режима по питанию (план 3.2). Применение уходит в
/// Task.Run, поэтому ждём сигнал фейка (PerfModeHit) с таймаутом. BrightnessWatcher
/// внутри guard-а реальный, но его Start безопасен (try/catch) и яркость не трогается,
/// пока RememberBrightness выключен.
/// </summary>
public sealed class PowerProfileGuardTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly FakeMifsClient _mifs = new();
    private readonly FakePowerEvents _power = new();
    private readonly FakeTimer _timer = new();

    public PowerProfileGuardTests() => Log.Enabled = false; // не сорим в реальный log.txt

    // Лимит и авто-яркость на фейках: без WMI-чтений и без реальных таймеров (сами они
    // проверяются своими тестами, здесь — лишь обязательные зависимости guard-а профилей)
    private BrightnessCapGuard NewCap(AppConfig cfg) =>
        new(cfg, _power, new FakeTimer(), new FakeTimer(), () => null, (_, _, _) => { }, _ => false);

    private AutoBrightnessGuard NewAuto(AppConfig cfg) =>
        new(cfg, _power, new FakeTimer(), new FakeTimer(), () => null, (_, _, _) => { }, _ => false, (l, _) => l);

    [Fact]
    public void Reapply_OnAc_AppliesAcMode()
    {
        var cfg = new AppConfig { PowerProfiles = true, AcPerfMode = PerfMode.Turbo, BatteryPerfMode = PerfMode.Quiet };
        _power.IsOnline = true;
        using var guard = new PowerProfileGuard(_mifs, cfg, _power, NewCap(cfg), NewAuto(cfg), _timer);

        guard.Reapply();

        _mifs.PerfModeHit.Wait(Wait).Should().BeTrue("применение уходит в фон и должно случиться");
        _mifs.PerfModeCalls.Should().Equal(PerfMode.Turbo);
    }

    [Fact]
    public void Reapply_OnBattery_AppliesBatteryMode()
    {
        var cfg = new AppConfig { PowerProfiles = true, AcPerfMode = PerfMode.Turbo, BatteryPerfMode = PerfMode.Quiet };
        _power.IsOnline = false;
        using var guard = new PowerProfileGuard(_mifs, cfg, _power, NewCap(cfg), NewAuto(cfg), _timer);

        guard.Reapply();

        _mifs.PerfModeHit.Wait(Wait).Should().BeTrue();
        _mifs.PerfModeCalls.Should().Equal(PerfMode.Quiet);
    }

    [Fact]
    public void RejectedMode_FallsBackToAuto()
    {
        // прошивка не приняла режим (напр. Full-speed на батарее) → guard откатывается на Auto
        var cfg = new AppConfig { PowerProfiles = true, BatteryPerfMode = PerfMode.FullSpeed };
        _power.IsOnline = false;
        _mifs.SetPerfModeResult = false;
        using var guard = new PowerProfileGuard(_mifs, cfg, _power, NewCap(cfg), NewAuto(cfg), _timer);

        guard.Reapply();

        _mifs.PerfModeHit.Wait(Wait).Should().BeTrue();
        _mifs.PerfModeHit.Wait(Wait).Should().BeTrue("после отказа должен уйти второй вызов — Auto");
        _mifs.PerfModeCalls.Should().Equal(PerfMode.FullSpeed, PerfMode.Auto);
    }

    [Fact]
    public void ProfilesDisabled_ReapplyDoesNothing()
    {
        var cfg = new AppConfig { PowerProfiles = false, RememberBrightness = false, AcPerfMode = PerfMode.Turbo };
        using var guard = new PowerProfileGuard(_mifs, cfg, _power, NewCap(cfg), NewAuto(cfg), _timer);

        guard.Reapply(); // ранний выход до Task.Run — синхронно

        _mifs.PerfModeCalls.Should().BeEmpty();
    }

    [Fact]
    public void PowerChange_AppliesModeAfterDebounce()
    {
        var cfg = new AppConfig { PowerProfiles = true, AcPerfMode = PerfMode.Auto };
        _power.IsOnline = true;
        using var guard = new PowerProfileGuard(_mifs, cfg, _power, NewCap(cfg), NewAuto(cfg), _timer);

        _power.RaisePower(PowerModes.StatusChange);
        _mifs.PerfModeCalls.Should().BeEmpty(); // до тика дебаунса — тишина
        _timer.Running.Should().BeTrue();

        _timer.Fire();

        _mifs.PerfModeHit.Wait(Wait).Should().BeTrue();
        _mifs.PerfModeCalls.Should().Equal(PerfMode.Auto);
    }

    [Fact]
    public void Suspend_DoesNotTriggerApply()
    {
        var cfg = new AppConfig { PowerProfiles = true, AcPerfMode = PerfMode.Turbo };
        using var guard = new PowerProfileGuard(_mifs, cfg, _power, NewCap(cfg), NewAuto(cfg), _timer);

        _power.RaisePower(PowerModes.Suspend); // профили реагируют только на Resume/StatusChange

        _timer.Running.Should().BeFalse();
        _mifs.PerfModeCalls.Should().BeEmpty();
    }
}
