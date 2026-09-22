using FluentAssertions;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Вебхук о достижении порога заряда (XIC-75): решение «сообщать или нет» и проверка адреса.
/// Сама отправка (сокеты, ретраи) — глазами: чужая железка юнит-тестом не поднимается.
/// </summary>
public sealed class WebhookTests
{
    public WebhookTests() => Log.Enabled = false;

    // ---- Когда сообщать ----

    [Fact]
    public void Порог_достигнут_на_зарядке_сообщаем()
        => ChargeLimitWatcher.Decide(care: true, online: true, limit: 80, life: 0.80f, fired: false)
            .Should().Be(ChargeWatch.Fire);

    [Fact]
    public void Порог_перепрыгнули_всё_равно_сообщаем()
        => ChargeLimitWatcher.Decide(care: true, online: true, limit: 60, life: 0.72f, fired: false)
            .Should().Be(ChargeWatch.Fire, "между замерами заряд мог уйти выше порога");

    [Fact]
    public void До_порога_молчим()
        => ChargeLimitWatcher.Decide(care: true, online: true, limit: 80, life: 0.79f, fired: false)
            .Should().Be(ChargeWatch.Idle);

    [Fact]
    public void Второй_раз_за_зарядку_не_сообщаем()
        => ChargeLimitWatcher.Decide(care: true, online: true, limit: 80, life: 0.95f, fired: true)
            .Should().Be(ChargeWatch.Idle, "розетку выключают один раз, а не каждые полминуты");

    [Fact]
    public void Отключили_зарядник_взводимся_заново()
        => ChargeLimitWatcher.Decide(care: true, online: false, limit: 80, life: 0.95f, fired: true)
            .Should().Be(ChargeWatch.Reset);

    [Fact]
    public void На_батарее_без_срабатывания_просто_молчим()
        => ChargeLimitWatcher.Decide(care: true, online: false, limit: 80, life: 0.50f, fired: false)
            .Should().Be(ChargeWatch.Idle);

    [Fact]
    public void Порог_выключен_молчим()
        => ChargeLimitWatcher.Decide(care: false, online: true, limit: 80, life: 1.0f, fired: false)
            .Should().Be(ChargeWatch.Idle, "беречь не просили — сообщать не о чем");

    [Theory]
    [InlineData(2.55f)]  // «батарея неизвестна» в семантике WinForms
    [InlineData(-1f)]
    public void Неизвестный_заряд_не_считаем_достигнутым_порогом(float life)
        => ChargeLimitWatcher.Decide(care: true, online: true, limit: 80, life: life, fired: false)
            .Should().Be(ChargeWatch.Idle);

    // ---- Куда согласны ходить ----

    [Theory]
    [InlineData("http://192.168.1.10:8123/api/webhook/xic")]
    [InlineData("https://ha.local/api/webhook/xic")]
    public void Адрес_http_и_https_разрешены(string url) => Webhook.IsAllowed(url).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ha.local/webhook")]                   // без схемы — не абсолютный
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ftp://example.com/drop")]
    public void Прочие_схемы_и_мусор_отклоняются(string? url) => Webhook.IsAllowed(url).Should().BeFalse(
        "запрос делает elevated-процесс по адресу из чужого конфига");

    // ---- Что отправляем ----

    [Fact]
    public void Тело_события_несёт_повод_порог_и_состояние()
    {
        var st = new ApiStatus("Turbo", Care: true, Travel: false, Owl: false,
            BatteryPercent: 80, Charging: true, Watts: 12.5f, Health: 97);

        string json = Webhook.Payload("chargeLimit", 80, st);

        json.Should().Contain("\"event\":\"chargeLimit\"").And.Contain("\"limit\":80");
        json.Should().Contain("\"hardwareLimit\":true", "по умолчанию порог держит прошивка");
        // имена — те же, что у GET /status: получателю не нужна вторая раскладка полей
        json.Should().Contain("\"batteryPercent\":80").And.Contain("\"charging\":true")
            .And.Contain("\"mode\":\"Turbo\"").And.Contain("\"health\":97");
    }

    [Fact]
    public void Программный_порог_помечен_в_теле_события()
    {
        var st = new ApiStatus("Auto", Care: true, Travel: false, Owl: false,
            BatteryPercent: 82, Charging: true, Watts: null, Health: 100);

        string json = Webhook.Payload("chargeLimit", 80, st, hardwareLimit: false);

        json.Should().Contain("\"hardwareLimit\":false",
            "здесь заряд ПРОДОЛЖАЕТСЯ, и розетка — единственное, что его остановит: " +
            "получателю эту разницу надо знать, а из одного limit её не вывести");
    }
}
