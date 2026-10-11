using System.Text;
using FluentAssertions;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// HttpApi.ReadBody (XIC-80): чтение тела запроса с потолком и дедлайном — без сокетов.
/// Регрессия, ради которой фикс: лимит 4 КБ проверялся только по Content-Length, а у
/// chunked-запроса длины нет — прежний ReadToEnd читал сколько пришлют и сколько угодно долго.
/// </summary>
public sealed class HttpApiBodyTests
{
    private const int Max = 4096;

    private static Task<(string Body, HttpApi.BodyError Error)> Read(Stream s, int max = Max, CancellationToken ct = default)
        => HttpApi.ReadBody(s, max, ct);

    [Fact]
    public async Task Обычная_команда_читается_целиком()
    {
        var (body, error) = await Read(new MemoryStream("""{"mode":"turbo"}"""u8.ToArray()));
        error.Should().Be(HttpApi.BodyError.None);
        body.Should().Be("""{"mode":"turbo"}""");
    }

    [Fact]
    public async Task Ровно_на_лимите_принимается()
    {
        var (body, error) = await Read(new MemoryStream(new byte[Max]));
        error.Should().Be(HttpApi.BodyError.None);
        body.Should().HaveLength(Max);
    }

    [Fact]
    public async Task Тело_больше_лимита_без_длины_отвергается_не_дочитывая()
    {
        // chunked: Content-Length нет, хвост бесконечный — читаем лишь лимит + 1 байт
        var s = new EndlessStream();
        var (_, error) = await Read(s);
        error.Should().Be(HttpApi.BodyError.TooLarge);
        s.Served.Should().BeLessThanOrEqualTo(Max + 1);
    }

    [Fact]
    public async Task Тело_кусками_по_байту_собирается()
    {
        // так приходит chunked-тело мелкими частями: ReadAsync отдаёт меньше, чем просили
        var (body, error) = await Read(new TrickleStream("""{"on":true}"""u8.ToArray()));
        error.Should().Be(HttpApi.BodyError.None);
        body.Should().Be("""{"on":true}""");
    }

    [Fact]
    public async Task Битый_UTF8_это_ошибка_а_не_вопросики()
    {
        var (_, error) = await Read(new MemoryStream([0x7B, 0xC3, 0x28, 0x7D])); // { невалидная пара }
        error.Should().Be(HttpApi.BodyError.BadUtf8);
    }

    [Fact]
    public async Task BOM_снимается_как_у_прежнего_StreamReader()
    {
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. "{}"u8.ToArray()];
        var (body, error) = await Read(new MemoryStream(withBom));
        error.Should().Be(HttpApi.BodyError.None);
        body.Should().Be("{}");
    }

    [Fact]
    public async Task Пустое_тело_пустая_строка()
    {
        var (body, error) = await Read(new MemoryStream());
        error.Should().Be(HttpApi.BodyError.None);
        body.Should().BeEmpty();
    }

    [Fact]
    public async Task Зависшее_чтение_обрывается_по_дедлайну()
    {
        // Клиент открыл тело и молчит. Поток отмену не слушает (как у http.sys) — оборвать
        // ожидание обязан сам ReadBody, иначе API висел бы для всех остальных запросов.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        Func<Task> act = () => Read(new StuckStream(), ct: cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- Потоки-фейки ----

    /// <summary>Бесконечный поток нулей; считает, сколько отдал.</summary>
    private sealed class EndlessStream : ReadOnlyFake
    {
        public long Served { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            buffer.Span.Clear();
            Served += buffer.Length;
            return ValueTask.FromResult(buffer.Length);
        }
    }

    /// <summary>Отдаёт данные по одному байту за вызов.</summary>
    private sealed class TrickleStream(byte[] data) : ReadOnlyFake
    {
        private int _pos;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos >= data.Length || buffer.Length == 0) return ValueTask.FromResult(0);
            buffer.Span[0] = data[_pos++];
            return ValueTask.FromResult(1);
        }
    }

    /// <summary>Чтение, которое никогда не завершается и отмену не слушает.</summary>
    private sealed class StuckStream : ReadOnlyFake
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => new(new TaskCompletionSource<int>().Task);
    }

    private abstract class ReadOnlyFake : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
