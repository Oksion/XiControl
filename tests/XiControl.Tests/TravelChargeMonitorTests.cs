using FluentAssertions;
using XiControl.Config;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// TravelChargeMonitor — опрос «В дорогу» до 100% на фейках (план 2.1).
/// BatteryLifePercent: 0..1 — заряд, вне диапазона (2.55) — «неизвестно».
/// </summary>
public sealed class TravelChargeMonitorTests
{
    private readonly AppConfig _cfg = new() { TravelMode = true };
    private readonly FakePowerEvents _power = new();   // IsOnline=true по умолчанию
    private readonly FakeTimer _timer = new();
    private int _ready;
    private readonly TravelChargeMonitor _mon;

    public TravelChargeMonitorTests()
    {
        _mon = new TravelChargeMonitor(_cfg, _power, _timer) { Ready = () => _ready++ };
    }

    [Fact]
    public void Rearm_TravelOnAndOnline_StartsPolling()
    {
        _mon.Rearm();

        _timer.Running.Should().BeTrue();
    }

    [Theory]
    [InlineData(false, true)]   // режим выключен
    [InlineData(true, false)]   // не на зарядке
    public void Rearm_NotEligible_StopsPolling(bool travel, bool online)
    {
        _cfg.TravelMode = travel;
        _power.IsOnline = online;

        _mon.Rearm();

        _timer.Running.Should().BeFalse();
    }

    [Fact]
    public void FullBattery_FiresReadyOnce_AndStopsPolling()
    {
        _power.BatteryLifePercent = 1.0f;
        _mon.Rearm();

        _timer.Fire();

        _ready.Should().Be(1);
        _timer.Running.Should().BeFalse("дальше опрашивать нечего — уведомление разовое");
    }

    [Theory]
    [InlineData(0.97f)]  // ещё заряжается
    [InlineData(2.55f)]  // «неизвестно» (системный sentinel 255%)
    public void NotFullOrUnknown_KeepsWaiting(float level)
    {
        _power.BatteryLifePercent = level;
        _mon.Rearm();

        _timer.Fire();

        _ready.Should().Be(0);
        _timer.Running.Should().BeTrue();
    }

    [Fact]
    public void WentOffline_MidPolling_WaitsWithoutFiring()
    {
        _power.BatteryLifePercent = 1.0f;
        _mon.Rearm();
        _power.IsOnline = false; // выдернули зарядник между тиками

        _timer.Fire();

        _ready.Should().Be(0);
        _timer.Running.Should().BeTrue("TrayApp сам погасит режим по событию питания");
    }

    [Fact]
    public void TravelTurnedOff_MidPolling_TickStops()
    {
        _mon.Rearm();
        _cfg.TravelMode = false; // режим сняли, а Rearm ещё не позвали

        _timer.Fire();

        _ready.Should().Be(0);
        _timer.Running.Should().BeFalse("тик сам останавливает опрос выключенного режима");
    }

    [Fact]
    public void Rearm_AfterReady_AllowsNextSession()
    {
        _power.BatteryLifePercent = 1.0f;
        _mon.Rearm();
        _timer.Fire();          // сессия 1: уведомили

        _mon.Rearm();           // новая сессия режима
        _timer.Fire();

        _ready.Should().Be(2);
    }
}
