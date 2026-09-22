using System.Text;
using System.Text.Json;

namespace XiControl.SystemIntegration;

/// <summary>
/// Исходящее событие в чужую систему (XIC-75): POST с компактным JSON на адрес, который задал
/// пользователь. Типичный получатель — Home Assistant или мост умной розетки: заряд дошёл до
/// порога → розетка выключилась сама, без опроса нас по кругу.
///
/// Это первый сетевой запрос, который приложение делает по своей инициативе (проверка
/// обновлений — по расписанию и с явным тумблером), поэтому правила жёсткие:
/// <list type="bullet">
/// <item>Адрес не задан — сетевого кода не существует: ни клиента, ни таймера, ни DNS.</item>
/// <item>Только http/https. Запрос делает elevated-процесс, и file:// или что-нибудь
/// экзотическое из чужого конфига ему тут не нужно.</item>
/// <item>Редиректы не ходим: «переадресация» с чужого адреса на локальный — классический способ
/// прокатиться на чужих правах.</item>
/// <item>Ответ не читаем вовсе — хватит кода состояния.</item>
/// </list>
/// </summary>
public static class Webhook
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly int[] RetryDelaysMs = [2000, 6000]; // две попытки вдогонку — розетка могла моргнуть

    /// <summary>Адрес, по которому мы согласны сходить: абсолютный http/https.</summary>
    public static bool IsAllowed(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Тело события: поля состояния — теми же именами, что у <c>GET /status</c> (второй словарь
    /// для тех же величин заставил бы получателя писать две раскладки), плюс сам повод.
    /// </summary>
    public static string Payload(string evt, int limit, ApiStatus st) =>
        JsonSerializer.Serialize(new
        {
            @event = evt,
            limit,
            // Zulu без смещения: у формата "O" смещение пишется через «+», а JSON-сериализатор
            // экранирует его в + — валидно, но в логе получателя выглядит как поломка
            time = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            mode = st.Mode,
            care = st.Care,
            travel = st.Travel,
            owl = st.Owl,
            batteryPercent = st.BatteryPercent,
            charging = st.Charging,
            watts = st.Watts,
            health = st.Health,
        });

    /// <summary>
    /// Отправить событие; true — получатель ответил успехом. Исключения наружу не выпускаем:
    /// чужая железка недоступна — это не наше падение. Попыток три с нарастающей паузой,
    /// ход виден в логе.
    /// </summary>
    public static async Task<bool> SendAsync(string url, string json, CancellationToken ct = default)
    {
        if (!IsAllowed(url)) { Log.Write("Webhook: адрес не http/https — не отправляем"); return false; }

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = Timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("XiControl-webhook");

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var body = new StringContent(json, Encoding.UTF8, "application/json");
                using var res = await http.PostAsync(url, body, ct).ConfigureAwait(false);
                if (res.IsSuccessStatusCode) { Log.Write($"Webhook: отправлено ({(int)res.StatusCode})"); return true; }
                Log.Write($"Webhook: получатель ответил {(int)res.StatusCode}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Log.Write($"Webhook: не дозвонились ({ex.GetType().Name})");
            }

            if (attempt >= RetryDelaysMs.Length || ct.IsCancellationRequested) return false;
            try { await Task.Delay(RetryDelaysMs[attempt], ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { return false; }
        }
    }
}
