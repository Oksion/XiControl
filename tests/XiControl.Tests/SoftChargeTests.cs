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
            hardwareMissing: false);

        t.Should().Be(new ChargeTarget(60, false),
            "прошивка остановит заряд сама — программный порог тут лишний, даже если включён");
    }

    [Fact]
    public void Аппаратного_нет_следим_за_программным()
    {
        var t = ChargeLimitWatcher.Target(care: true, careLimit: 60, softOn: true, softLimit: 90,
            hardwareMissing: true);

        t.Should().Be(new ChargeTarget(90, true));
    }

    [Fact]
    public void Аппаратного_нет_и_программный_выключен_следить_не_за_чем()
        => ChargeLimitWatcher.Target(care: true, careLimit: 60, softOn: false, softLimit: 90,
            hardwareMissing: true).Should().BeNull("выключено — значит батарею не опрашиваем вовсе");

    [Fact]
    public void Защита_выключена_следить_не_за_чем()
        => ChargeLimitWatcher.Target(care: false, careLimit: 60, softOn: true, softLimit: 90,
            hardwareMissing: false).Should().BeNull();

    [Fact]
    public void Программный_порог_из_конфига_зажимается_в_разумные_рамки()
        => ChargeLimitWatcher.Target(care: false, careLimit: 60, softOn: true, softLimit: 500,
            hardwareMissing: true)!.Value.Limit.Should().Be(100, "порог выше 100% недостижим");

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
