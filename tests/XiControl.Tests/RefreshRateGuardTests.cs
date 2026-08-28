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
    private readonly FakeTimer _debounce = new();
    private readonly FakeTimer _watchdog = new();
    private int _applies;

    private RefreshRateGuard Guard(AppConfig cfg) =>
        new(cfg, _power, _display, _debounce, _watchdog, () => _applies++);

    [Theory]
    [InlineData(PowerModes.StatusChange)]
    [InlineData(PowerModes.Resume)]
    public void PowerChange_ArmsDebounce(PowerModes mode)
    {
        var cfg = new AppConfig { AutoRefreshRate = false };
        using var guard = Guard(cfg);

        _power.RaisePower(mode);

        _debounce.Running.Should().BeTrue();
        _debounce.Fire();
        _debounce.Running.Should().BeFalse();
        _applies.Should().Be(1);
    }

    [Fact]
    public void Suspend_DoesNotArmDebounce()
    {
        var cfg = new AppConfig { AutoRefreshRate = false };
        using var guard = Guard(cfg);

        _power.RaisePower(PowerModes.Suspend);

        _debounce.Running.Should().BeFalse();
    }

    // XIC-22: реакция на чужую смену режима экрана — только при включённой опции,
    // иначе поведение существующих установок изменилось бы молча
    [Fact]
    public void DisplayChange_ArmsDebounce_WhenHoldEnabled()
    {
        var cfg = new AppConfig { AutoRefreshRate = false, HoldRefreshRate = true };
        using var guard = Guard(cfg);

        _display.RaiseDisplayChanged();

        _debounce.Running.Should().BeTrue();
    }

    [Fact]
    public void DisplayChange_Ignored_WhenHoldDisabled()
    {
        var cfg = new AppConfig { AutoRefreshRate = false, HoldRefreshRate = false };
        using var guard = Guard(cfg);

        _display.RaiseDisplayChanged();

        _debounce.Running.Should().BeFalse("по умолчанию опция выключена — поведение прежнее");
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
        _debounce.Running.Should().BeFalse();

        _display.RaiseDisplayChanged();
        _debounce.Running.Should().BeFalse("подписка на события экрана тоже снимается");
        _watchdog.Running.Should().BeFalse();
    }

    [Fact]
    public void Watchdog_IsIdleUntilResume()
    {
        using var guard = Guard(new AppConfig());

        _watchdog.Running.Should().BeFalse();
        _watchdog.Interval.Should().Be(15_000);
    }

    [Fact]
    public void Resume_StartsOnePostResumeProbe()
    {
        using var guard = Guard(new AppConfig());

        _power.RaisePower(PowerModes.Resume);

        _watchdog.Running.Should().BeTrue();
        _watchdog.Starts.Should().Be(1);
    }

    [Fact]
    public void UnchangedPostResumeProbe_StopsWithoutBecomingPersistent()
    {
        using var guard = Guard(new AppConfig());
        _power.RaisePower(PowerModes.Resume);

        _watchdog.Fire();

        _watchdog.Running.Should().BeFalse();
        _debounce.Fire();
        _applies.Should().Be(1, "resume itself still schedules the normal reapply");
    }

    [Fact]
    public void MissedPowerTransition_MakesWatchdogPersistent()
    {
        using var guard = Guard(new AppConfig());
        _power.RaisePower(PowerModes.Resume);
        _debounce.Fire();
        _power.IsOnline = false; // Windows не прислала StatusChange

        _watchdog.Fire();

        _watchdog.Running.Should().BeTrue("обнаружен пропущенный переход питания");
        _debounce.Running.Should().BeTrue();
        _debounce.Fire();
        _applies.Should().Be(2);

        _power.IsOnline = true;
        _watchdog.Fire();
        _debounce.Running.Should().BeTrue("постоянный watchdog замечает следующие переходы");
    }

    [Fact]
    public void StatusChangeBeforeProbe_CancelsOneShotWatchdog()
    {
        using var guard = Guard(new AppConfig());
        _power.RaisePower(PowerModes.Resume);
        _power.IsOnline = false;

        _power.RaisePower(PowerModes.StatusChange);

        _watchdog.Running.Should().BeFalse();
        _debounce.Running.Should().BeTrue();
    }
}
