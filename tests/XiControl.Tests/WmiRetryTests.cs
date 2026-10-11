using FluentAssertions;
using XiControl.Wmi;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// WmiRetry (XIC-83): первый старт WMI-подписки повторяется ограниченно и с нарастающей паузой,
/// обрыв уже работавшего потока — бессрочно.
/// </summary>
public sealed class WmiRetryTests
{
    [Fact]
    public void Первый_старт_ретраится_с_нарастающей_паузой_и_сдаётся()
    {
        var delays = new List<TimeSpan>();
        for (int failures = 1; WmiRetry.NextDelay(everStarted: false, failures) is { } d; failures++)
            delays.Add(d);

        delays.Should().HaveCount(WmiRetry.FirstStartAttempts - 1);
        delays.Should().BeInAscendingOrder();
        delays[0].Should().BeLessThan(TimeSpan.FromSeconds(5), "первый повтор — быстро, WMI обычно догоняет за секунды");
        delays.Aggregate(TimeSpan.Zero, (a, b) => a + b).Should().BeLessThan(TimeSpan.FromMinutes(2),
            "класса событий нет — замолкаем быстро, а не крутимся всю сессию");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void После_успешного_старта_переподключаемся_всегда(int failures) =>
        WmiRetry.NextDelay(everStarted: true, failures).Should().Be(WmiRetry.Reconnect);

    [Fact]
    public void Без_сбоев_паузы_нет() =>
        WmiRetry.NextDelay(everStarted: false, 0).Should().BeNull();
}
