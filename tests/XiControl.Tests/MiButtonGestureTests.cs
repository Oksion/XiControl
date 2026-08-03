using FluentAssertions;
using XiControl.Input;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// MiButtonGesture — сценарии клик / двойной / удержание на фейковых таймерах
/// (план 3.2). Fire() у FakeTimer срабатывает только когда таймер запущен —
/// как настоящий тик.
/// </summary>
public sealed class MiButtonGestureTests
{
    private readonly FakeTimer _hold = new();
    private readonly FakeTimer _click = new();
    private int _clicks, _doubles, _holds;
    private readonly MiButtonGesture _mi;

    public MiButtonGestureTests() => _mi = new MiButtonGesture(_hold, _click)
    {
        Click = () => _clicks++,
        DoubleClick = () => _doubles++,
        Hold = () => _holds++,
    };

    [Fact]
    public void SingleClick_FiresAfterDoubleWindow()
    {
        _mi.Down();
        _hold.Running.Should().BeTrue("нажатие взводит порог удержания");

        _mi.Up();
        _hold.Running.Should().BeFalse();
        _clicks.Should().Be(0, "клик ждёт окно двойного");
        _click.Running.Should().BeTrue();

        _click.Fire(); // окно истекло
        _clicks.Should().Be(1);
        (_doubles, _holds).Should().Be((0, 0));
    }

    [Fact]
    public void DoubleClick_SecondUpInsideWindow()
    {
        _mi.Down(); _mi.Up();   // первый клик — окно взведено
        _mi.Down(); _mi.Up();   // второй внутри окна

        _doubles.Should().Be(1);
        (_clicks, _holds).Should().Be((0, 0));
        _click.Running.Should().BeFalse("двойной обнуляет окно");
    }

    [Fact]
    public void TwoSeparateClicks_BothFire()
    {
        _mi.Down(); _mi.Up(); _click.Fire();
        _mi.Down(); _mi.Up(); _click.Fire();

        _clicks.Should().Be(2);
        _doubles.Should().Be(0);
    }

    [Fact]
    public void Hold_FiresOnce_AndUpIsNotAClick()
    {
        _mi.Down();
        _hold.Fire();          // порог удержания истёк

        _holds.Should().Be(1);
        _hold.Running.Should().BeFalse();

        _mi.Up();              // отпускание после удержания — не клик
        _clicks.Should().Be(0);
        _click.Running.Should().BeFalse();
    }

    [Fact]
    public void Hold_CancelsPendingClickWindow()
    {
        _mi.Down(); _mi.Up();  // клик №1 — окно двойного открыто
        _mi.Down();
        _hold.Fire();          // вместо второго клика — удержание

        _holds.Should().Be(1);
        _click.Running.Should().BeFalse("удержание гасит окно двойного");
        _clicks.Should().Be(0, "накопленный клик сброшен");
    }

    [Fact]
    public void DoubleDisabled_ClickFiresImmediately()
    {
        _mi.DoubleEnabled = () => false;

        _mi.Down(); _mi.Up();

        _clicks.Should().Be(1, "без окна ожидания");
        _click.Running.Should().BeFalse();
    }

    [Fact]
    public void HoldDisabled_LongPressActsAsClick()
    {
        _mi.HoldEnabled = () => false;

        _mi.Down();
        _hold.Running.Should().BeFalse("выключенный жест не взводит порог удержания");

        _hold.Fire();          // даже если тик придёт — таймер стоит, удержания нет
        _holds.Should().Be(0);

        _mi.Up(); _click.Fire();
        _clicks.Should().Be(1, "долгое нажатие уходит в обычный клик, а не пропадает");
    }

    [Fact]
    public void QuickTapBeforeHoldThreshold_DoesNotHold()
    {
        _mi.Down();
        _mi.Up();              // отпустили до порога — таймер удержания снят

        _hold.Fire();          // «тик» после остановки не срабатывает (FakeTimer, как настоящий)
        _holds.Should().Be(0);
    }
}
