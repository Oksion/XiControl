using FluentAssertions;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Таймер пула не выпускает исключение подписчика наружу (XIC-63).
///
/// Цена вопроса: тик исполняется в потоке пула, и необработанное исключение оттуда убивает
/// процесс целиком — молча, без диалога и без строки в журнале. Восемь таймеров в приложении
/// ходят в WMI и реестр, то есть бросить может любой.
/// </summary>
public class WorkerTimerTests
{
    [Fact]
    public async Task Исключение_подписчика_не_убивает_таймер()
    {
        using var t = new WorkerTimer { Interval = 15 };
        int calls = 0;
        var second = new TaskCompletionSource();

        t.Tick += () =>
        {
            // первый тик падает; если исключение выпустить наружу — до второго дело не дойдёт,
            // а в проде не дожил бы и сам процесс
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("проверка");
            second.TrySetResult();
        };

        t.Start();
        (await Task.WhenAny(second.Task, Task.Delay(5000))).Should().Be(second.Task,
            "после упавшего тика таймер обязан продолжать тикать");
        t.Stop();

        calls.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Stop_прекращает_тики()
    {
        using var t = new WorkerTimer { Interval = 15 };
        int calls = 0;
        t.Tick += () => Interlocked.Increment(ref calls);

        t.Start();
        await Task.Delay(120);
        t.Stop();

        int afterStop = Volatile.Read(ref calls);
        await Task.Delay(120);
        Volatile.Read(ref calls).Should().Be(afterStop);
    }
}
