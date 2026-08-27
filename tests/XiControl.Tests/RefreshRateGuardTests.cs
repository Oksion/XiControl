using FluentAssertions;
using Microsoft.Win32;
using XiControl.Config;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// RefreshRateGuard — только дебаунс-логика: сам Apply (живой ChangeDisplaySettings)
/// юнитами не покрываем (план 3.3). AutoRefreshRate выключен → тик безопасно no-op.
/// </summary>
public sealed class RefreshRateGuardTests
{
    private readonly FakePowerEvents _power = new();
    private readonly FakeDisplayEvents _display = new();
    private readonly FakeTimer _timer = new();
    private readonly FakeTimer _watchdog = new();
    private int _applies;

    private RefreshRateGuard Guard(AppConfig cfg) =>
        new(cfg, _power, _display, _timer, _watchdog, () => _applies++);

    [Theory]
    [InlineData(PowerModes.StatusChange)]
    [InlineData(PowerModes.Resume)]
    public void PowerChange_ArmsDebounce(PowerModes mode)
    {
        var cfg = new AppConfig { AutoRefreshRate = false };
        using var guard = Guard(cfg);

        _power.RaisePower(mode);

        _timer.Running.Should().BeTrue();
        _timer.Fire(); // AutoRefreshRate=false → ApplyForPower выходит сразу, экран не трогаем
        _timer.Running.Should().BeFalse();
    }

    [Fact]
    public void Suspend_DoesNotArmDebounce()
    {
        var cfg = new AppConfig { AutoRefreshRate = false };
        using var guard = Guard(cfg);

        _power.RaisePower(PowerModes.Suspend);

        _timer.Running.Should().BeFalse();
    }

    [Fact]
    public void MissingSystemEvent_IsRecoveredByPowerWatchdog()
    {
        var cfg = new AppConfig { RefreshRateFeature = true, AutoRefreshRate = true };
        using var guard = Guard(cfg);

        _power.IsOnline = false;
        _watchdog.Fire();

        _timer.Running.Should().BeTrue("Modern Standby machines may omit PowerModeChanged");
        _timer.Fire();
        _applies.Should().Be(1);
    }

    [Fact]
    public void PowerWatchdog_DoesNotReapplyWithoutAStateChange()
    {
        using var guard = Guard(new AppConfig { RefreshRateFeature = true, AutoRefreshRate = true });

        _watchdog.Fire();
        _watchdog.Fire();

        _timer.Running.Should().BeFalse();
        _applies.Should().Be(0);
    }

    // XIC-22: реакция на чужую смену режима экрана — только при включённой опции,
    // иначе поведение существующих установок изменилось бы молча
    [Fact]
    public void DisplayChange_ArmsDebounce_WhenHoldEnabled()
    {
        var cfg = new AppConfig { AutoRefreshRate = false, HoldRefreshRate = true };
        using var guard = Guard(cfg);

        _display.RaiseDisplayChanged();

        _timer.Running.Should().BeTrue();
    }

    [Fact]
    public void DisplayChange_Ignored_WhenHoldDisabled()
    {
        var cfg = new AppConfig { AutoRefreshRate = false, HoldRefreshRate = false };
        using var guard = Guard(cfg);

        _display.RaiseDisplayChanged();

        _timer.Running.Should().BeFalse("по умолчанию опция выключена — поведение прежнее");
    }

    [Fact]
    public void HoldDefault_IsOff()
    {
        new AppConfig().HoldRefreshRate.Should().BeFalse();
    }

    [Fact]
    public void Dispose_UnsubscribesFromEvents()
    {
        var cfg = new AppConfig { AutoRefreshRate = false, HoldRefreshRate = true };
        var guard = Guard(cfg);
        guard.Dispose();

        _power.RaisePower(PowerModes.StatusChange);
        _timer.Running.Should().BeFalse();

        _display.RaiseDisplayChanged();
        _timer.Running.Should().BeFalse("подписка на события экрана тоже снимается");

        _power.IsOnline = false;
        _watchdog.Fire();
        _timer.Running.Should().BeFalse("watchdog тоже остановлен и отписан");
    }
}
