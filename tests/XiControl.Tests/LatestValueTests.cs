using FluentAssertions;
using XiControl;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// LatestValue (XIC-81): медленный WMI-источник читается на пуле, UI берёт последнее готовое.
/// Главные гарантии: чтение в полёте одно (зависший провайдер не плодит потоков), старое
/// значение не выдаётся за текущее, ожидание первого значения ограничено таймаутом.
/// </summary>
public sealed class LatestValueTests
{
    public LatestValueTests() => Log.Enabled = false; // ошибка чтения пишет в лог — не сорим в реальный файл

    private long _now = 1_000;

    private LatestValue<int> Make(Func<int> read) => new(read, -1, "Test", () => _now);

    [Fact]
    public async Task До_первого_чтения_начальное_значение_и_его_нет()
    {
        var v = Make(() => 42);
        v.HasValue.Should().BeFalse();
        v.Latest.Should().Be(-1);
        v.Fresh(5000, -2).Should().Be(-2); // начальное — не «свежее»

        await v.Request();
        v.HasValue.Should().BeTrue();
        v.Latest.Should().Be(42);
        v.Fresh(5000, -2).Should().Be(42);
    }

    [Fact]
    public async Task Старое_значение_не_выдаётся_за_текущее()
    {
        var v = Make(() => 42);
        await v.Request();
        _now += 5000;
        v.Fresh(5000, -2).Should().Be(42);  // ровно на границе — ещё годно
        _now += 1;
        v.Fresh(5000, -2).Should().Be(-2);  // старше — «нет данных», хоть Latest и помнит
        v.Latest.Should().Be(42);
    }

    [Fact]
    public async Task Зависшее_чтение_одно_новых_не_плодит()
    {
        // Провайдер завис (после сна): тик UI каждую секунду зовёт Request — потоков не прибавляется.
        using var release = new ManualResetEventSlim();
        int calls = 0;
        var v = Make(() => { Interlocked.Increment(ref calls); release.Wait(); return 7; });

        var first = v.Request();
        for (int i = 0; i < 10; i++) v.Request().Should().BeSameAs(first);
        await Task.Delay(50);
        calls.Should().Be(1);

        release.Set();
        await first;
        v.Latest.Should().Be(7);
        await v.Request(); // освободилось — следующий тик снова читает
        calls.Should().Be(2);
    }

    [Fact]
    public void Ожидание_первого_значения_ограничено()
    {
        using var release = new ManualResetEventSlim();
        var v = Make(() => { release.Wait(); return 7; });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        v.WaitFirst(100);
        sw.ElapsedMilliseconds.Should().BeLessThan(2000); // не вечно, сколько бы ни висел провайдер
        v.HasValue.Should().BeFalse();
        release.Set();
    }

    [Fact]
    public void Ожидание_первого_значения_дожидается_быстрого_ответа()
    {
        var v = Make(() => 42);
        v.WaitFirst(5000);
        v.Latest.Should().Be(42);
    }

    [Fact]
    public async Task Ошибка_чтения_не_портит_прежнее_значение()
    {
        bool fail = false;
        var v = Make(() => fail ? throw new InvalidOperationException("WMI") : 42);
        await v.Request();

        fail = true;
        await v.Request(); // исключение поймано и залогировано, наружу не летит
        v.Latest.Should().Be(42);
        _now += 6000;
        v.Fresh(5000, -2).Should().Be(-2); // а неудачи подряд дают «нет данных» по возрасту

        fail = false;
        await v.Request(); // после ошибки слот свободен — чтение снова идёт
        v.Fresh(5000, -2).Should().Be(42);
    }

    // XIC-101: владелец ждёт чтение в полёте, прежде чем освободить источник
    [Fact]
    public async Task Pending_это_чтение_в_полёте_и_оно_завершается()
    {
        using var release = new ManualResetEventSlim();
        var v = Make(() => { release.Wait(); return 7; });

        v.Pending.IsCompleted.Should().BeTrue("чтения нет — ждать нечего");
        var read = v.Request();
        v.Pending.Should().BeSameAs(read);
        await Task.Delay(50);
        v.Pending.IsCompleted.Should().BeFalse("чтение ещё идёт");

        release.Set();
        await v.Pending.WaitAsync(TimeSpan.FromSeconds(5));
        v.Latest.Should().Be(7);
        v.Pending.IsCompleted.Should().BeTrue();
    }
}
