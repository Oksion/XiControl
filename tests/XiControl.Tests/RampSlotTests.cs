using FluentAssertions;
using XiControl.Config;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Слот плавного хода яркости: новый ход/правка отменяют текущий, но источник отмены живёт,
/// пока задача хода не завершилась. Регресс на «Brightness.Ramp: ObjectDisposedException»:
/// ход между проверками токена делает медленную WMI-запись и после неё зовёт ct.WaitHandle —
/// при «Cancel + сразу Dispose» этот вызов падал.
/// </summary>
public sealed class RampSlotTests
{
    public RampSlotTests() => Log.Enabled = false;

    private static bool Disposed(CancellationToken ct)
    {
        try { _ = ct.WaitHandle; return false; }
        catch (ObjectDisposedException) { return true; }
    }

    // освобождение идёт продолжением на пуле — ждём его, а не полагаемся на тайминг
    private static bool EventuallyDisposed(CancellationToken ct) =>
        SpinWait.SpinUntil(() => Disposed(ct), TimeSpan.FromSeconds(5));

    [Fact]
    public void NewRamp_CancelsPrevious_TokenUsableUntilRampEnds()
    {
        var slot = new RampSlot();
        var first = new TaskCompletionSource();
        CancellationToken a = default, b = default;

        slot.Start(ct => { a = ct; return first.Task; });
        slot.Start(ct => { b = ct; return Task.Delay(Timeout.Infinite, ct); });

        a.IsCancellationRequested.Should().BeTrue("новый ход отменяет предыдущий");
        b.IsCancellationRequested.Should().BeFalse();
        // ровно то, что делает ход после WMI-записи: сон с мгновенной отменой
        a.Invoking(t => t.WaitHandle.WaitOne(0)).Should().NotThrow("ход ещё идёт — токен обязан жить");
        a.WaitHandle.WaitOne(0).Should().BeTrue("отменённый ход просыпается сразу");

        first.SetResult(); // ход заметил отмену и вышел
        EventuallyDisposed(a).Should().BeTrue("по завершении хода источник освобождается");
        Disposed(b).Should().BeFalse("текущий ход не тронут");

        slot.Cancel();
        EventuallyDisposed(b).Should().BeTrue("отменённая Task.Delay завершается — источник следом");
    }

    [Fact]
    public void Cancel_KeepsTokenAliveForRunningRamp()
    {
        var slot = new RampSlot();
        var ramp = new TaskCompletionSource();
        CancellationToken a = default;
        slot.Start(ct => { a = ct; return ramp.Task; });

        slot.Cancel();
        slot.Cancel(); // повторная отмена (правка + Dispose guard-а) — безвредна

        a.IsCancellationRequested.Should().BeTrue();
        Disposed(a).Should().BeFalse();
        ramp.SetResult();
        EventuallyDisposed(a).Should().BeTrue();
    }

    [Fact]
    public void CompletedRamp_SourceReleased()
    {
        // нулевая дельта: Brightness.Ramp отдаёт уже завершённую задачу
        var slot = new RampSlot();
        CancellationToken a = default;
        slot.Start(ct => { a = ct; return Task.CompletedTask; });

        EventuallyDisposed(a).Should().BeTrue();
        slot.Invoking(s => s.Cancel()).Should().NotThrow("отмена после конца хода — не ошибка");
    }

    [Fact]
    public void ThrowingRamp_PropagatesAndSlotStaysUsable()
    {
        var slot = new RampSlot();
        CancellationToken a = default;

        slot.Invoking(s => s.Start(ct => { a = ct; throw new InvalidOperationException(); }))
            .Should().Throw<InvalidOperationException>();
        Disposed(a).Should().BeTrue("ход не стартовал — источник освобождён сразу");

        CancellationToken b = default;
        slot.Start(ct => { b = ct; return Task.Delay(Timeout.Infinite, ct); });
        b.IsCancellationRequested.Should().BeFalse();
        slot.Cancel();
    }

    [Fact]
    public void AutoBrightness_UserEditDuringRamp_DoesNotPullTokenFromUnderRamp()
    {
        // сценарий из лога (выход из сна): авто-яркость начала ход, человек тут же взялся за
        // ползунок — ход отменён, но его следующий ct.WaitHandle не должен падать
        var cfg = new AppConfig { AutoBrightness = true };
        cfg.AutoBrightnessPointsAc.AddRange(BrightnessCurve.DefaultPoints());
        cfg.AutoBrightnessPointsBattery.AddRange(BrightnessCurve.DefaultPoints());
        var settle = new FakeTimer();
        var ramp = new TaskCompletionSource();
        CancellationToken token = default;
        using var g = new AutoBrightnessGuard(cfg, new FakePowerEvents(), settle, new FakeTimer(),
            () => 50, (_, _, ct) => { token = ct; return ramp.Task; }, _ => false, (l, _) => l,
            converge: new FakeTimer(), backoff: new FakeTimer());

        g.OnLux(2000);
        settle.Fire();
        token.CanBeCanceled.Should().BeTrue("ход запущен");

        g.OnBrightness(30, own: false, settling: false);

        token.IsCancellationRequested.Should().BeTrue("правка человека отменяет наш ход");
        token.Invoking(t => t.WaitHandle.WaitOne(0)).Should().NotThrow();
        ramp.SetResult();
        EventuallyDisposed(token).Should().BeTrue();
    }

    [Fact]
    public void CapGuard_DisposeDuringRamp_DoesNotPullTokenFromUnderRamp()
    {
        var cfg = new AppConfig { BrightnessCapEnabled = true, BrightnessCapAc = 60, BrightnessCapBattery = 60 };
        var converge = new FakeTimer();
        var ramp = new TaskCompletionSource();
        CancellationToken token = default;
        var g = new BrightnessCapGuard(cfg, new FakePowerEvents(), converge, new FakeTimer(),
            () => 80, (_, _, ct) => { token = ct; return ramp.Task; }, _ => false);

        g.OnBrightness(80, own: false, settling: false); // выше лимита — эпизод схождения
        converge.Fire();
        token.CanBeCanceled.Should().BeTrue("шаг схождения запустил ход");

        g.Dispose(); // выход из приложения посреди хода

        token.IsCancellationRequested.Should().BeTrue();
        token.Invoking(t => t.WaitHandle.WaitOne(0)).Should().NotThrow();
        ramp.SetResult();
        EventuallyDisposed(token).Should().BeTrue();
    }
}
