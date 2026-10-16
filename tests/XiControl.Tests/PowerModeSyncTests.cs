using FluentAssertions;
using XiControl.Config;
using XiControl.SystemIntegration;
using XiControl.Wmi;
using Xunit;
using Skip = XiControl.SystemIntegration.PowerModeSync.Skip;

namespace XiControl.Tests;

/// <summary>
/// PowerModeSync (XIC-96): режим ноутбука → режим питания Windows. Живой overlay юнитами не
/// трогаем — фейк хранит overlay по источнику, как Windows (AC и DC раздельно).
/// </summary>
public sealed class PowerModeSyncTests
{
    private sealed class FakeOverlay : PowerModeSync.IPowerOverlay
    {
        private readonly FakePowerEvents _power;
        public FakeOverlay(FakePowerEvents power) => _power = power;

        public Guid Scheme = PowerModeSync.BalancedScheme;
        public readonly Dictionary<bool, Guid> Overlay = new() { [true] = Guid.Empty, [false] = Guid.Empty };
        public bool FailSet;
        public int Writes;

        public bool TryGetScheme(out Guid scheme) { scheme = Scheme; return true; }
        public bool TryGetOverlay(out Guid overlay) { overlay = Overlay[_power.IsOnline]; return true; }
        public bool SetActive(Guid overlay)
        {
            if (FailSet) return false;
            Overlay[_power.IsOnline] = overlay;   // как PowerSetActiveOverlayScheme — только текущий источник
            Writes++;
            return true;
        }
    }

    private readonly AppConfig _cfg = new() { PowerModeSync = true };
    private readonly FakeMifsClient _mifs = new() { Mode = PerfMode.Eco };
    private readonly FakePowerEvents _power = new() { IsOnline = true };
    private readonly FakeOverlay _overlay;
    private OemServiceState _service = OemServiceState.Stopped;
    private readonly PowerModeSync _sync;

    public PowerModeSyncTests()
    {
        Log.Enabled = false;
        _overlay = new FakeOverlay(_power);
        _sync = new PowerModeSync(_cfg, _mifs, _power, _overlay, () => _service);
    }

    [Theory]
    [InlineData(PerfMode.Eco, "961cc777-2547-4f9d-8174-7d86181b8a7a")]
    [InlineData(PerfMode.Quiet, "961cc777-2547-4f9d-8174-7d86181b8a7a")]
    [InlineData(PerfMode.Auto, "00000000-0000-0000-0000-000000000000")]
    [InlineData(PerfMode.Balance, "00000000-0000-0000-0000-000000000000")]
    [InlineData(PerfMode.Turbo, "ded574b5-45a0-4f42-8737-46345c09c238")]
    [InlineData(PerfMode.FullSpeed, "ded574b5-45a0-4f42-8737-46345c09c238")]
    public void Таблица_режим_в_overlay(PerfMode mode, string overlay) =>
        PowerModeSync.OverlayFor(mode).Should().Be(new Guid(overlay));

    [Fact]
    public void Решение_по_порядку_причин()
    {
        var bal = PowerModeSync.BalancedScheme;
        PowerModeSync.Decide(false, OemServiceState.Stopped, bal, PerfMode.Eco, null, out _).Should().Be(Skip.Disabled);
        PowerModeSync.Decide(true, OemServiceState.Running, bal, PerfMode.Eco, null, out _).Should().Be(Skip.ServiceRunning);
        PowerModeSync.Decide(true, OemServiceState.Stopped, Guid.NewGuid(), PerfMode.Eco, null, out _).Should().Be(Skip.OtherScheme);
        PowerModeSync.Decide(true, OemServiceState.Stopped, bal, null, null, out _).Should().Be(Skip.NoMode);
        PowerModeSync.Decide(true, OemServiceState.Stopped, bal, PerfMode.Eco, PowerModeSync.Efficiency, out _).Should().Be(Skip.AlreadySet);
        PowerModeSync.Decide(true, OemServiceState.Stopped, bal, PerfMode.Eco, Guid.Empty, out var t).Should().Be(Skip.None);
        t.Should().Be(PowerModeSync.Efficiency);
    }

    [Theory]
    [InlineData(OemServiceState.NotInstalled)]
    [InlineData(OemServiceState.Stopped)]
    [InlineData(OemServiceState.Disabled)]
    [InlineData(OemServiceState.NoAccess)]
    [InlineData(OemServiceState.Unknown)]
    public void Служба_не_запущена_пишем(OemServiceState state) =>
        PowerModeSync.Decide(true, state, null, PerfMode.Turbo, null, out _).Should().Be(Skip.None);

    [Fact]
    public void Пишет_только_текущий_источник()
    {
        _sync.Sync();

        _overlay.Overlay[true].Should().Be(PowerModeSync.Efficiency);
        _overlay.Overlay[false].Should().Be(Guid.Empty);
    }

    [Fact]
    public void Повторная_сверка_не_пишет()
    {
        _sync.Sync();
        _sync.Sync();   // двойное 0x16 на TM2424

        _overlay.Writes.Should().Be(1);
    }

    [Fact]
    public void Смена_питания_сверяет_новый_источник()
    {
        _sync.Sync();
        _power.IsOnline = false;
        _sync.Sync();

        _overlay.Overlay[false].Should().Be(PowerModeSync.Efficiency);
    }

    [Fact]
    public void Служба_запущена_не_пишем_и_тумблер_заблокирован()
    {
        _service = OemServiceState.Running;
        _sync.Sync();

        _overlay.Writes.Should().Be(0);
        _sync.BlockedByService.Should().BeTrue();
    }

    [Fact]
    public void Выключено_в_конфиге_не_пишем()
    {
        _cfg.PowerModeSync = false;
        _sync.Sync();

        _overlay.Writes.Should().Be(0);
    }

    [Fact]
    public void Выключение_возвращает_сбалансированный_только_там_где_наше()
    {
        _sync.Sync();                     // сеть ← эффективность (наше)
        _cfg.PowerModeSync = false;
        _sync.UndoCurrent();

        _overlay.Overlay[true].Should().Be(Guid.Empty);
    }

    [Fact]
    public void Выбор_человека_откат_не_трогает()
    {
        _sync.Sync();                                         // наше — эффективность
        _overlay.Overlay[true] = PowerModeSync.Performance;   // человек сменил в «Параметрах»
        _cfg.PowerModeSync = false;
        _sync.UndoCurrent();

        _overlay.Overlay[true].Should().Be(PowerModeSync.Performance);
    }

    [Fact]
    public void Откат_второго_источника_доделывается_на_нём()
    {
        _sync.Sync();                     // сеть
        _power.IsOnline = false;
        _sync.Sync();                     // батарея
        _cfg.PowerModeSync = false;
        _sync.UndoCurrent();              // выключили на батарее
        _overlay.Overlay[true].Should().Be(PowerModeSync.Efficiency); // сеть пока не достать

        _power.IsOnline = true;
        _sync.UndoCurrent();              // воткнули зарядку — доделали

        _overlay.Overlay[true].Should().Be(Guid.Empty);
        _overlay.Overlay[false].Should().Be(Guid.Empty);
    }

    [Fact]
    public void Откат_переживает_перезапуск()
    {
        _sync.Sync();                     // сеть ← эффективность
        _cfg.PowerOverlayWritten.Should().ContainKey("ac");

        // «перезапуск»: новый экземпляр с тем же конфигом, памяти прежнего нет
        var restarted = new PowerModeSync(_cfg, _mifs, _power, _overlay, () => _service);
        _cfg.PowerModeSync = false;
        restarted.UndoCurrent();

        _overlay.Overlay[true].Should().Be(Guid.Empty, "откатываем записанное нами и после перезапуска (XIC-101)");
        _cfg.PowerOverlayWritten.Should().BeNull("откатили — помнить нечего");
    }

    [Fact]
    public void Сбалансированный_не_хранится()
    {
        _mifs.Mode = PerfMode.Turbo;
        _sync.Sync();
        _mifs.Mode = PerfMode.Auto;       // своя запись «сбалансированного» — откатывать нечего
        _sync.Sync();

        _cfg.PowerOverlayWritten.Should().BeNull();
    }

    [Fact]
    public void Отказ_записи_не_запоминается_как_наше()
    {
        _overlay.FailSet = true;
        _sync.Sync();
        _overlay.FailSet = false;
        _overlay.Overlay[true] = PowerModeSync.Efficiency;    // человек сам выбрал то же
        _cfg.PowerModeSync = false;
        _sync.UndoCurrent();

        _overlay.Overlay[true].Should().Be(PowerModeSync.Efficiency);
    }

    [Fact]
    public void Прошивка_молчит_ничего_не_пишем()
    {
        _mifs.Mode = null;
        _sync.Sync();

        _overlay.Writes.Should().Be(0);
    }
}
