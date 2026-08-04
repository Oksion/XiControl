using System.Net.Http;
using System.Text.Json;
using XiControl.Config;

namespace XiControl.SystemIntegration;

/// <summary>Найденный релиз: числовая версия, тег как на GitHub и ссылка на страницу.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string Url);

/// <summary>Чем закончилась последняя проверка — чтобы кнопка «Проверить обновления» не молчала:
/// без ответа нажатие выглядит как «ничего не произошло». <see cref="DevBuild"/> отдельно от
/// <see cref="UpToDate"/> намеренно: у локальной сборки версия `0.0.0`, и назвать её «последней»
/// значило бы соврать — релиз на GitHub заведомо свежее.</summary>
public enum UpdateStatus { NotChecked, UpToDate, Available, Failed, DevBuild }

/// <summary>
/// Проверка выхода новой версии — только оповещение, без самообновления: запущенный exe нельзя
/// перезаписать («file is being used by another process»), а приложение и есть тот файл, который
/// заменяют. Любое «обновить сейчас» потребовало бы сначала выйти, отсюда растут сторожевые
/// процессы — мы их не берём (XIC-20). Работает при любом канале поставки: и winget, и portable.
///
/// Один HTTPS-GET к <c>/releases/latest</c> — этот эндпоинт сам отсекает pre-release, а у нас на
/// каждый push в main собирается скользящий pre-release под тегом `pre`, предлагать его нельзя.
/// Сеть — только из фонового потока, ошибки гасим в лог: молчаливая деградация вместо окон.
/// </summary>
public static class UpdateCheck
{
    private const string LatestReleaseApi = "https://api.github.com/repos/Oksion/XiControl/releases/latest";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MinInterval = TimeSpan.FromHours(24);

    /// <summary>Пора ли идти в сеть: тумблер включён и с прошлой проверки прошли сутки.
    /// Кнопка «Проверить сейчас» это окно игнорирует — там явное действие пользователя.</summary>
    internal static bool DueForCheck(bool enabled, DateTime? lastUtc, DateTime nowUtc)
    {
        if (!enabled) return false;
        if (lastUtc is not DateTime last) return true;
        return nowUtc - last >= MinInterval || nowUtc < last; // время «уехало» назад — не залипаем навсегда
    }

    /// <summary>Тег релиза (`v0.9.0`, `0.9.0`) → числовая версия; null — не разобрали.</summary>
    internal static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        string s = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(s, out var v) ? v : null;
    }

    /// <summary>
    /// Найденный релиз реально новее нас? Это же условие решает, показывать ли отметку на вкладке
    /// «О программе»: без проверки она писала бы «доступна 0.9.0», когда 0.9.0 и так установлена.
    /// Нерелизная сборка (`0.0.*` — локальная или тестовая из main) молчит: она ниже любого релиза,
    /// и разработка с тестированием тонули бы в уведомлениях «обновись на релиз».
    /// </summary>
    internal static bool IsNewer(Version? latest, Version? current)
    {
        if (latest is null || current is null) return false;
        if (IsDevBuild(current)) return false;
        return latest > current;
    }

    /// <summary>
    /// Нерелизная сборка: локальная (`0.0.0`) ИЛИ тестовая из main — скользящий pre-release
    /// собирается с версией `0.0.<номер прогона CI>`. Проверять только `0.0.0` было мало:
    /// тестеры pre-сборки получали тост «вышла 0.11.0» и обновлялись на релиз, теряя ровно ту
    /// сборку, которую взялись гонять. Релизы у нас всегда `0.<минор≥1>.<патч>`, так что
    /// «минор равен нулю» и есть признак нерелизной сборки.
    /// </summary>
    internal static bool IsDevBuild(Version? v) => v is { Major: 0, Minor: 0 };

    /// <summary>Показывать ли тост: версия новее и про неё ещё не говорили (тост — раз на версию).</summary>
    internal static bool ShouldNotify(Version? latest, Version? current, string? skipped) =>
        IsNewer(latest, current) && ParseTag(skipped) != latest;

    /// <summary>
    /// Сходить за последним релизом. null — нет сети, таймаут, лимит GitHub или мусор в ответе;
    /// это штатный исход, а не ошибка приложения. Звать только из фонового потока.
    /// </summary>
    public static async Task<ReleaseInfo?> FetchLatestAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = Timeout };
            // без User-Agent GitHub отвечает 403
            http.DefaultRequestHeaders.UserAgent.ParseAdd("XiControl-update-check");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            string json = await http.GetStringAsync(LatestReleaseApi).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            string? tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (ParseTag(tag) is not Version v) { Log.Write($"UpdateCheck: не разобрал тег «{tag}»"); return null; }

            string url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
            return new ReleaseInfo(v, tag!, url);
        }
        catch (Exception ex)
        {
            // сеть недоступна/лимит/таймаут — молча живём дальше, окон с ошибками не показываем
            Log.Write($"UpdateCheck: проверить обновление не вышло ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    /// <summary>Версия текущей сборки — числовая (`0.0.0` у дев-сборки, реальная из тега в CI).</summary>
    public static Version? CurrentVersion() =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;

    /// <summary>Отметить, что про эту версию уже сказали: тост показывается один раз на версию.</summary>
    public static void MarkNotified(AppConfig cfg, ReleaseInfo release)
    {
        cfg.SkippedVersion = release.Version.ToString();
        cfg.Save();
    }
}
