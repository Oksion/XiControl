using FluentAssertions;
using Microsoft.Win32;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// ChargeGuard — «когда переармливаем EC, когда нет» (план 3.2). Вся логика на фейках:
/// прошивка (IMifsClient), питание (IPowerEvents), дебаунс (ITimer) — железо не трогаем.
/// </summary>
public sealed class ChargeGuardTests
{
    private readonly FakeMifsClient _mifs = new();
    private readonly FakePowerEvents _power = new();
    private readonly FakeTimer _timer = new();

    private ChargeGuard Create(bool careWanted = true) =>
        new(_mifs, _power, () => careWanted ? 80 : 100, _timer);   // порог 80% (беречь) / 100% (выкл)

    [Fact]
    public void Reapply_WhenCareWanted_ArmsEc()
    {
        using var guard = Create(careWanted: true);

        guard.Reapply();

        _mifs.ChargeLimitCalls.Should().Equal(80);
    }

    [Fact]
    public void Reapply_WhenCareNotWanted_DoesNotTouchEc()
    {
        using var guard = Create(careWanted: false);

        guard.Reapply();

        _mifs.ChargeLimitCalls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(PowerModes.StatusChange)]
    [InlineData(PowerModes.Resume)]
    public void PowerChange_DebouncesBeforeRearm(PowerModes mode)
    {
        using var guard = Create();

        _power.RaisePower(mode);

        // событие пришло — но EC трогаем только после тика дебаунса (события сыплются пачкой)
        _mifs.ChargeLimitCalls.Should().BeEmpty();
        _timer.Running.Should().BeTrue();

        _timer.Fire();

        _mifs.ChargeLimitCalls.Should().Equal(80);
        _timer.Running.Should().BeFalse(); // одноразовый: тик сам себя останавливает
    }

    [Fact]
    public void Suspend_RearmsImmediately_WithoutDebounce()
    {
        using var guard = Create();

        _power.RaisePower(PowerModes.Suspend);

        // после Suspend наш код уже не выполнится — ре-арм строго до засыпания
        _mifs.ChargeLimitCalls.Should().Equal(80);
        _timer.Running.Should().BeFalse();
    }

    [Fact]
    public void SessionEnding_RearmsImmediately()
    {
        using var guard = Create();

        _power.RaiseSession();

        _mifs.ChargeLimitCalls.Should().Equal(80);
    }

    [Fact]
    public void PendingDebounce_IsCancelledBySuspend()
    {
        using var guard = Create();

        _power.RaisePower(PowerModes.StatusChange); // взводим дебаунс
        _power.RaisePower(PowerModes.Suspend);      // сон пришёл раньше тика

        _mifs.ChargeLimitCalls.Should().Equal(80); // ре-арм от Suspend
        _timer.Running.Should().BeFalse();          // дебаунс снят — второго ре-арма не будет
    }

    [Fact]
    public void Dispose_UnsubscribesFromPowerEvents()
    {
        var guard = Create();
        guard.Dispose();

        _power.RaisePower(PowerModes.Suspend);
        _power.RaiseSession();

        _mifs.ChargeLimitCalls.Should().BeEmpty();
    }
}
