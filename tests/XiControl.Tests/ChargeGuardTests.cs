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

    // ---- XIC-64: перед сном защиту не снимаем ----

    [Fact]
    public void Порог_уже_стоит_в_прошивке_значит_не_переписываем()
    {
        // Запись — это ре-арм off→on, то есть промежуток совсем без защиты. Если EC и так
        // держит нужный порог, трогать его незачем: бессмысленный риск на ровном месте.
        _mifs.ChargeLimit = 80;
        using var guard = Create();

        guard.Reapply();

        _mifs.ChargeLimitCalls.Should().BeEmpty();
    }

    [Fact]
    public void Перед_сном_пишем_одной_командой_без_сброса_в_выкл()
    {
        // Суть бага: SetChargeLimit сначала пишет «выкл» (=100%), спит 80 мс и лишь потом
        // ставит код. Уснуть ровно в этом окне — значит остаться без защиты на всю ночь;
        // владелец находил ноутбук заряженным до 92% при пороге 60%.
        _mifs.ChargeLimit = 100;
        using var guard = Create();

        _power.RaisePower(PowerModes.Suspend);

        _mifs.ChargeLimitCalls.Should().Equal(80);
        _mifs.ChargeLimitResets.Should().Equal(false);
    }

    [Fact]
    public void Обычный_ре_арм_сбрасывает_стейт_машину_как_раньше()
    {
        // Вне спешки поведение прежнее: off→on. Времени достаточно, а сброс лечит EC,
        // застрявший в непонятном состоянии.
        _mifs.ChargeLimit = 100;
        using var guard = Create();

        guard.Reapply();

        _mifs.ChargeLimitResets.Should().Equal(true);
    }

    [Fact]
    public void Прошивка_сказала_принято_но_порог_не_удержался()
    {
        // Команда отвечает «ок» и тогда, когда EC значение не сохранил. Без чтения-назад
        // такой отказ выглядел бы успехом, а батарея тихо уходила бы выше порога.
        _mifs.ChargeLimit = 100;
        _mifs.SetChargeLimitResult = true;
        using var guard = Create();

        guard.Reapply();
        _mifs.ChargeLimit = 100;   // EC «забыл» сразу после записи

        guard.Reapply();           // второй заход видит расхождение и пишет о нём

        _mifs.ChargeLimitCalls.Should().Equal(80, 80);
    }
}
