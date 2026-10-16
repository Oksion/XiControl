using FluentAssertions;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Программный порог заряда для моделей без аппаратного лимита (XIC-74): за каким порогом
/// следим и когда напоминаем. Сам OSD и звук — глазами.
/// </summary>
public sealed class SoftChargeTests
{
    private const long Quarter = 15 * 60_000L; // «напоминать раз в 15 минут» из дефолтов

    // ---- За каким порогом следим ----

    [Fact]
    public void Аппаратный_лимит_есть_следим_за_ним()
    {
        var t = ChargeLimitWatcher.Target(care: true, careLimit: 60, softOn: true, softLimit: 90,
            hardwareMissing: false, travel: false);

        t.Should().Be(new ChargeTarget(60, false),
            "прошивка остановит заряд сама — программный порог тут лишний, даже если включён");
    }

    [Fact]
    public void Аппаратного_нет_следим_за_программным()
    {
        var t = ChargeLimitWatcher.Target(care: true, careLimit: 60, softOn: true, softLimit: 90,
            hardwareMissing: true, travel: false);

        t.Should().Be(new ChargeTarget(90, true));
    }

    [Fact]
    public void Аппаратного_нет_и_программный_выключен_следить_не_за_чем()
        => ChargeLimitWatcher.Target(care: true, careLimit: 60, softOn: false, softLimit: 90,
            hardwareMissing: true, travel: false).Should().BeNull("выключено — значит батарею не опрашиваем вовсе");

    [Fact]
    public void Защита_выключена_следить_не_за_чем()
        => ChargeLimitWatcher.Target(care: false, careLimit: 60, softOn: true, softLimit: 90,
            hardwareMissing: false, travel: false).Should().BeNull();

    [Fact]
    public void Программный_порог_из_конфига_зажимается_в_разумные_рамки()
        => ChargeLimitWatcher.Target(care: false, careLimit: 60, softOn: true, softLimit: 500,
            hardwareMissing: true, travel: false)!.Value.Limit.Should().Be(100, "порог выше 100% недостижим");

    // ---- «В дорогу» снимает порог (XIC-99) ----

    [Fact]
    public void В_дорогу_аппаратный_порог_не_наблюдаем()
        => ChargeLimitWatcher.Target(care: true, careLimit: 80, softOn: false, softLimit: 80,
            hardwareMissing: false, travel: true).Should().BeNull(
            "прошивка заряжает до 100 — вебхук «дошли до 80» оборвал бы разовый заряд розеткой");

    [Fact]
    public void Без_в_дорогу_аппаратный_порог_как_раньше()
        => ChargeLimitWatcher.Target(care: true, careLimit: 80, softOn: false, softLimit: 80,
            hardwareMissing: false, travel: false).Should().Be(new ChargeTarget(80, false));

    [Fact]
    public void В_дорогу_программный_порог_тоже_не_наблюдаем()
        => ChargeLimitWatcher.Target(care: true, careLimit: 80, softOn: true, softLimit: 80,
            hardwareMissing: true, travel: true).Should().BeNull("правило одно на оба порога");

    [Fact]
    public void В_дорогу_заряд_выше_порога_вебхук_молчит_а_после_выхода_сообщает()
    {
        var cfg = new Config.AppConfig { ChargeCare = true, CareLimitPercent = 80, TravelMode = true };
        var power = new FakePowerEvents { IsOnline = true, BatteryLifePercent = 0.85f };
        var timer = new FakeTimer();
        using var w = new ChargeLimitWatcher(cfg, power, timer);
        int fired = 0;
        w.Reached = (_, _, _) => fired++;

        w.Rearm();
        timer.Running.Should().BeFalse("во время «В дорогу» следить не за чем");
        timer.Fire();
        fired.Should().Be(0);

        cfg.TravelMode = false;      // вышли из «В дорогу» на 85% — порог 80 снова держит прошивка
        w.Rearm();
        timer.Fire();
        fired.Should().Be(1, "заряд выше порога — автоматика должна узнать сразу (вариант А)");
    }

    // ---- Напоминания ----

    [Fact]
    public void Прошло_время_и_провод_в_розетке_напоминаем()
        => ChargeLimitWatcher.ShouldRemind(online: true, life: 0.92f, limit: 90,
            sinceMs: Quarter, everyMs: Quarter, sent: 0, max: 3).Should().BeTrue();

    [Fact]
    public void Рано_молчим()
        => ChargeLimitWatcher.ShouldRemind(online: true, life: 0.92f, limit: 90,
            sinceMs: Quarter - 1, everyMs: Quarter, sent: 0, max: 3).Should().BeFalse();

    [Fact]
    public void Зарядник_выдернули_напоминать_не_о_чем()
        => ChargeLimitWatcher.ShouldRemind(online: false, life: 0.92f, limit: 90,
            sinceMs: Quarter, everyMs: Quarter, sent: 0, max: 3).Should().BeFalse();

    [Fact]
    public void Заряд_упал_ниже_порога_напоминать_не_о_чем()
        => ChargeLimitWatcher.ShouldRemind(online: true, life: 0.85f, limit: 90,
            sinceMs: Quarter, everyMs: Quarter, sent: 0, max: 3).Should().BeFalse(
            "успели выдернуть и воткнуть снова при меньшем заряде — это уже другая зарядка");

    [Fact]
    public void Лимит_напоминаний_исчерпан_замолкаем()
        => ChargeLimitWatcher.ShouldRemind(online: true, life: 0.92f, limit: 90,
            sinceMs: Quarter * 10, everyMs: Quarter, sent: 3, max: 3).Should().BeFalse(
            "приложение, пищащее до утра, выключают целиком");

    [Fact]
    public void Напоминания_отключены_совсем()
        => ChargeLimitWatcher.ShouldRemind(online: true, life: 0.92f, limit: 90,
            sinceMs: Quarter * 10, everyMs: Quarter, sent: 0, max: 0).Should().BeFalse();

    [Theory]
    [InlineData(2.55f)]
    [InlineData(-1f)]
    public void Неизвестный_заряд_не_повод_напоминать(float life)
        => ChargeLimitWatcher.ShouldRemind(online: true, life: life, limit: 90,
            sinceMs: Quarter, everyMs: Quarter, sent: 0, max: 3).Should().BeFalse();
}
