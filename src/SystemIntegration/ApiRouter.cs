using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XiControl.Wmi;

namespace XiControl.SystemIntegration;

/// <summary>Снимок состояния для GET /status; собирает владелец (TrayApp).</summary>
public readonly record struct ApiStatus(string Mode, bool Care, bool Travel, bool Owl,
    int? BatteryPercent, bool Charging, float? Watts, int? Health);

/// <summary>
/// Авторизация и маршрутизация HTTP API (XIC-13) — чистая логика без сокетов, тестируется
/// на фейках. Белый список зашит в switch: других маршрутов физически не существует —
/// настройки, автостарт и запуск программ через API невозможны и не будут добавлены
/// без отдельного решения (см. issue). Ответы машинные (JSON, без Loc).
/// </summary>
public sealed class ApiRouter
{
    private readonly ApiSettings _s;

    // Команды монтирует владелец; маршалинг в UI-поток — его забота (запросы приходят с пула).
    public required Action<PerfMode> SetMode;
    public required Action<bool> SetCare;
    public required Action<bool> SetTravel;
    public required Action<bool> SetOwl;
    public required Func<ApiStatus> Status;
    /// <summary>Сова доступна как фича (OwlMode в «Функциях»): выключена → команда 403.</summary>
    public required Func<bool> OwlFeature;

    public ApiRouter(ApiSettings s) => _s = s;

    /// <summary>Bearer-токен верен: SHA-256(токен) == хеш из настроек. Сверка constant-time —
    /// длина и содержимое не утекают по времени ответа.</summary>
    public bool CheckToken(string? authorization)
    {
        if (_s.TokenSha256 is not { Length: > 0 } stored) return false; // токен не сгенерирован — всё закрыто
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        byte[] expected;
        try { expected = Convert.FromHexString(stored); }
        catch (FormatException) { return false; } // битый хеш в файле — доступ закрыт, не открыт
        byte[] actual = SHA256.HashData(Encoding.UTF8.GetBytes(authorization["Bearer ".Length..].Trim()));
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Обработать авторизованный запрос → (HTTP-код, JSON-тело).</summary>
    public (int Code, string Json) Handle(string method, string path, string body)
    {
        if (method == "GET" && path == "/status")
        {
            var st = Status();
            return (200, JsonSerializer.Serialize(new
            {
                mode = st.Mode,
                care = st.Care,
                travel = st.Travel,
                owl = st.Owl,
                batteryPercent = st.BatteryPercent,
                charging = st.Charging,
                watts = st.Watts,
                health = st.Health,
            }));
        }

        if (method == "POST")
            switch (path)
            {
                case "/mode":
                    if (!_s.AllowMode) return Forbidden();
                    // числа Enum.TryParse тоже парсит — IsDefined отсекает мусор вроде "77";
                    // скрытый/недоступный режим отклонит прошивка (честный отказ, как в UI)
                    if (ReadString(body, "value") is not string v
                        || !Enum.TryParse<PerfMode>(v, ignoreCase: true, out var mode)
                        || !Enum.IsDefined(mode))
                        return Bad();
                    SetMode(mode);
                    return Ok();
                case "/care":
                    if (!_s.AllowCare) return Forbidden();
                    if (ReadBool(body, "on") is not bool care) return Bad();
                    SetCare(care);
                    return Ok();
                case "/travel":
                    if (!_s.AllowTravel) return Forbidden();
                    if (ReadBool(body, "on") is not bool travel) return Bad();
                    SetTravel(travel);
                    return Ok();
                case "/owl":
                    if (!_s.AllowOwl || !OwlFeature()) return Forbidden();
                    if (ReadBool(body, "on") is not bool owl) return Bad();
                    SetOwl(owl);
                    return Ok();
            }

        return (404, """{"error":"not found"}""");
    }

    private static (int, string) Ok() => (200, """{"ok":true}""");
    private static (int, string) Bad() => (400, """{"error":"bad request"}""");
    private static (int, string) Forbidden() => (403, """{"error":"command disabled"}""");

    private static bool? ReadBool(string body, string prop)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty(prop, out var v)
                   && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? v.GetBoolean() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? ReadString(string body, string prop)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
