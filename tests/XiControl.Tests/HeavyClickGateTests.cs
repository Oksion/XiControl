using FluentAssertions;
using XiControl.SystemIntegration;
using Xunit;
using static XiControl.SystemIntegration.HeavyClickGate;

namespace XiControl.Tests;

/// <summary>
/// Сильное нажатие как клик другой кнопкой (XIC-108): когда левое нажатие придержать, отдать
/// или заменить. Окно — 300 мс, допуск сдвига — 4 px.
/// </summary>
public sealed class HeavyClickGateTests
{
    private readonly HeavyClickGate _gate = new(windowMs: 300, moveTolerancePx: 4);

    [Fact]
    public void Tap_НетПальцев_НеТрогаем()
    {
        // тап-клик: Windows шлёт нажатие, когда палец уже оторван
        _gate.LeftDown(touching: false, 0, 10, 10).Should().BeFalse();
        _gate.LeftUp().Should().Be((false, Effect.None));
    }

    [Fact]
    public void СильноеНажатие_ЛевыйКликЗаменяетсяДействием()
    {
        _gate.LeftDown(true, 0, 10, 10).Should().BeTrue();   // нажатие придержали
        _gate.Heavy().Should().BeTrue();                      // на TM2424 — через 110–210 мс
        _gate.Tick(400).Should().Be(Effect.None);             // окно вышло, но решение уже принято
        _gate.LeftUp().Should().Be((true, Effect.RunAction)); // отпускание проглочено, вместо него действие
    }

    [Fact]
    public void БыстрыйКлик_ОтпустилиВОкне_ОтдаёмЦеликом()
    {
        _gate.LeftDown(true, 0, 10, 10);
        _gate.LeftUp().Should().Be((true, Effect.InjectLeftClick));
    }

    [Fact]
    public void ОкноВышло_ОтдаёмНажатие_ОтпусканиеПослеНегоПроходит()
    {
        _gate.LeftDown(true, 0, 10, 10);
        _gate.Tick(299).Should().Be(Effect.None);
        _gate.Tick(300).Should().Be(Effect.InjectLeftDown);
        _gate.InjectedDownSeen();
        _gate.LeftUp().Should().Be((false, Effect.None));
        _gate.Heavy().Should().BeFalse(); // продавили позже — заменять уже нечего
    }

    [Fact]
    public void ОтпусканиеОбогналоОтданноеНажатие_СинтезируемОтпускание()
    {
        // отпускание уже стояло в очереди ввода, когда мы отдали нажатие: пропусти его — и
        // кнопка залипнет нажатой
        _gate.LeftDown(true, 0, 10, 10);
        _gate.Tick(300).Should().Be(Effect.InjectLeftDown);
        _gate.LeftUp().Should().Be((true, Effect.InjectLeftUp));
        _gate.InjectedDownSeen();
        _gate.LeftUp().Should().Be((false, Effect.None)); // наше же отпускание дальше не трогаем
    }

    [Fact]
    public void Перетаскивание_СдвигБольшеДопуска_ОтдаёмСразу()
    {
        _gate.LeftDown(true, 0, 10, 10);
        _gate.Move(13, 7).Should().Be(Effect.None);           // дрожь пальца — ждём дальше
        _gate.Move(15, 10).Should().Be(Effect.InjectLeftDown);
        _gate.Pending.Should().BeFalse();
    }

    [Fact]
    public void ПовторноеНажатиеВОжидании_НеНаше()
    {
        _gate.LeftDown(true, 0, 10, 10);
        _gate.LeftDown(true, 50, 10, 10).Should().BeFalse();
    }

    [Fact]
    public void СильноеНажатиеБезЛевогоНажатия_Игнорируем() =>
        _gate.Heavy().Should().BeFalse();

    [Theory]
    [InlineData("rclick", true)]
    [InlineData("mclick", true)]
    [InlineData("dblclick", true)]
    [InlineData("screenshot", false)]
    [InlineData(null, false)]
    public void ДействияКлики(string? action, bool mouse) =>
        HeavyPressMouse.IsMouseAction(action).Should().Be(mouse);
}
