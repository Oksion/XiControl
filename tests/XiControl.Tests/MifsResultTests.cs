using FluentAssertions;
using XiControl.Wmi;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Обёртка над ответом MIFS. Сам разбор живёт в <see cref="MifsReply"/> и проверяется в
/// <see cref="MifsDialectTests"/> — здесь только то, что MifsResult правильно к нему обращается
/// и не падает на коротком ответе.
/// </summary>
public sealed class MifsResultTests
{
    [Fact]
    public void Классический_ответ_читается_по_своим_смещениям()
    {
        var r = new MifsResult
        {
            Out = [0x00, 0x80, 0x00, 0x08, 0x02, 0x00, 0x64, 0x00],
            Dialect = MifsDialect.Classic,
            Cmd = Mifs.CmdPerf,
        };

        r.Ok.Should().BeTrue();
        r.Value(MifsReply.PerfOffset).Should().Be(0x02);
        r.Value(MifsReply.ChargeOffset).Should().Be(0x64);
    }

    [Fact]
    public void Эхо_ответ_читается_из_OUT2_независимо_от_запрошенного_смещения()
    {
        var r = new MifsResult
        {
            Out = [0x00, 0x08, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00],
            Dialect = MifsDialect.Echo,
            Cmd = Mifs.CmdPerf,
        };

        r.Ok.Should().BeTrue();
        r.Value(MifsReply.PerfOffset).Should().Be(0x02);
    }

    [Theory]
    [InlineData(0)]   // прошивка не ответила вовсе
    [InlineData(4)]   // нет байта [4]
    [InlineData(6)]   // есть [4], нет [6]
    public void Короткий_ответ_не_роняет_разбор(int length)
    {
        var r = new MifsResult
        {
            Out = new byte[length],
            Dialect = MifsDialect.Classic,
            Cmd = Mifs.CmdPerf,
        };

        r.Value(MifsReply.PerfOffset).Should().Be(0);
        r.Value(MifsReply.ChargeOffset).Should().Be(0);
        r.Ok.Should().BeFalse();
    }
}
