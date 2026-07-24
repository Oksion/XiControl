using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// HTTP API (XIC-13): авторизация и маршрутизация — чистая логика ApiRouter без сокетов.
/// Ключевые гарантии: без токена всё закрыто; write-команды закрыты пер-командными
/// тумблерами (403); белый список исчерпывающий — «опасных» маршрутов не существует.
/// </summary>
public sealed class ApiRouterTests
{
    private static string Sha256Hex(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static ApiRouter Make(ApiSettings s, List<string> calls, bool owlFeature = true) => new(s)
    {
        SetMode = m => calls.Add($"mode:{m}"),
        SetCare = on => calls.Add($"care:{on}"),
        SetTravel = on => calls.Add($"travel:{on}"),
        SetOwl = on => calls.Add($"owl:{on}"),
        OwlFeature = () => owlFeature,
        Status = () => new ApiStatus("Auto", true, false, false, 80, true, 12.5f, 95),
    };

    // ---- Токен ----

    [Fact]
    public void CheckToken_AcceptsOnlyExactBearerToken()
    {
        var r = Make(new ApiSettings { TokenSha256 = Sha256Hex("secret-token") }, []);
        r.CheckToken("Bearer secret-token").Should().BeTrue();
        r.CheckToken("Bearer wrong").Should().BeFalse();
        r.CheckToken("secret-token").Should().BeFalse("без схемы Bearer — отказ");
        r.CheckToken(null).Should().BeFalse();
    }

    [Fact]
    public void CheckToken_NoStoredHash_EverythingClosed()
    {
        // токен ещё не сгенерирован (или хеш в файле битый) — доступ закрыт, а не открыт
        Make(new ApiSettings(), []).CheckToken("Bearer anything").Should().BeFalse();
        Make(new ApiSettings { TokenSha256 = "не-hex" }, []).CheckToken("Bearer anything").Should().BeFalse();
    }

    // ---- Чтение ----

    [Fact]
    public void Status_ReturnsSnapshotJson()
    {
        var (code, json) = Make(new ApiSettings(), []).Handle("GET", "/status", "");
        code.Should().Be(200);
        json.Should().Contain("\"mode\":\"Auto\"").And.Contain("\"batteryPercent\":80")
            .And.Contain("\"charging\":true").And.Contain("\"health\":95");
    }

    // ---- Пер-командные разрешения ----

    [Fact]
    public void Command_Disabled_Returns403_AndDoesNothing()
    {
        var calls = new List<string>();
        var r = Make(new ApiSettings(), calls); // все Allow* по умолчанию false
        r.Handle("POST", "/mode", """{"value":"turbo"}""").Code.Should().Be(403);
        r.Handle("POST", "/care", """{"on":true}""").Code.Should().Be(403);
        r.Handle("POST", "/travel", """{"on":true}""").Code.Should().Be(403);
        r.Handle("POST", "/owl", """{"on":true}""").Code.Should().Be(403);
        calls.Should().BeEmpty();
    }

    [Fact]
    public void Mode_Allowed_ParsesCaseInsensitive_AndInvokes()
    {
        var calls = new List<string>();
        var r = Make(new ApiSettings { AllowMode = true }, calls);
        r.Handle("POST", "/mode", """{"value":"turbo"}""").Code.Should().Be(200);
        calls.Should().Equal("mode:Turbo");
    }

    [Fact]
    public void Mode_GarbageValue_Returns400()
    {
        var calls = new List<string>();
        var r = Make(new ApiSettings { AllowMode = true }, calls);
        r.Handle("POST", "/mode", """{"value":"warp-drive"}""").Code.Should().Be(400);
        r.Handle("POST", "/mode", """{"value":"77"}""").Code.Should().Be(400, "число вне enum — не режим");
        r.Handle("POST", "/mode", "не json").Code.Should().Be(400);
        calls.Should().BeEmpty();
    }

    [Fact]
    public void BoolCommands_Allowed_Invoke()
    {
        var calls = new List<string>();
        var r = Make(new ApiSettings { AllowCare = true, AllowTravel = true, AllowOwl = true }, calls);
        r.Handle("POST", "/care", """{"on":true}""").Code.Should().Be(200);
        r.Handle("POST", "/travel", """{"on":false}""").Code.Should().Be(200);
        r.Handle("POST", "/owl", """{"on":true}""").Code.Should().Be(200);
        calls.Should().Equal("care:True", "travel:False", "owl:True");
    }

    [Fact]
    public void Owl_FeatureDisabled_Returns403_EvenIfAllowed()
    {
        // сова выключена во вкладке «Функции» → её API-команда закрыта независимо от тумблера
        var calls = new List<string>();
        var r = Make(new ApiSettings { AllowOwl = true }, calls, owlFeature: false);
        r.Handle("POST", "/owl", """{"on":true}""").Code.Should().Be(403);
        calls.Should().BeEmpty();
    }

    // ---- Белый список исчерпывающий ----

    [Fact]
    public void UnknownRoutes_Return404_DangerousOnesDoNotExist()
    {
        var r = Make(new ApiSettings
        { AllowMode = true, AllowCare = true, AllowTravel = true, AllowOwl = true }, []);
        // настройки, автостарт, запуск программ — таких маршрутов нет физически (AC XIC-13)
        r.Handle("POST", "/autostart", """{"on":true}""").Code.Should().Be(404);
        r.Handle("POST", "/launch", """{"cmd":"calc"}""").Code.Should().Be(404);
        r.Handle("POST", "/settings", "{}").Code.Should().Be(404);
        r.Handle("GET", "/mode", "").Code.Should().Be(404, "команды — только POST");
        r.Handle("POST", "/status", "").Code.Should().Be(404, "статус — только GET");
    }
}
