using System.Security.Cryptography;
using System.Text;
using XiControl.Config;
using XiControl.Localization;
using XiControl.SystemIntegration;

namespace XiControl.Ui.Settings;

/// <summary>
/// Вкладка «HTTP API» (XIC-13): opt-in веб-API для локалки (телефон / Home Assistant).
/// Настройки живут в api.json (ProgramData, ACL «запись только админам») — не в config.json,
/// чтобы непривилегированный процесс не мог включить API или подменить токен. Токен показывается
/// один раз при генерации; write-команды включаются поштучно, по умолчанию только GET /status.
/// </summary>
public sealed class ApiTab : SettingsPane
{
    // Ветка main, а не тег: документ правится чаще, чем выходят релизы, и ссылка из старой
    // версии должна вести на актуальное описание протокола, а не на слепок полугодовой давности.
    private const string DocsUrl = "https://github.com/Oksion/XiControl/blob/main/docs/15-http-api.md";


    public ApiTab(SettingsToolkit ui, AppConfig cfg, SettingsActions act, Action rebuild) : base(ui)
    {
        var s = act.GetApiSettings();
        ui.AddHeader(this, "settings.tab.api", "settings.api.sub");

        // мастер-тумблер: rebuild гасит/зажигает остальные контролы вкладки
        ui.AddRow(this, "settings.api.enable", "settings.api.enable.desc",
            ui.Toggle(s.Enabled, on => { s.Enabled = on; act.ApiApplied(); rebuild(); }));

        // порт: применяем по Leave; кривое значение откатываем показом фактического (rebuild)
        var port = ui.TextField(s.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), ui.Sc(72), v =>
        {
            if (int.TryParse(v, out int p) && p is >= 1024 and <= 65535)
            {
                if (p != s.Port) { s.Port = p; act.ApiApplied(); }
            }
            else rebuild();
        });
        port.Enabled = s.Enabled;
        ui.AddRow(this, "settings.api.port", "settings.api.port.desc", port);

        // LAN-доступ: предупреждение — прямо в описании строки, до включения
        var lan = ui.Toggle(s.LanAccess, on => { s.LanAccess = on; act.ApiApplied(); });
        lan.Enabled = s.Enabled;
        ui.AddRow(this, "settings.api.lan", "settings.api.lan.desc", lan);

        // Токен — карточка в два этажа: верх как у обычной строки (текст слева, кнопка справа),
        // низ — поле во всю ширину карточки: 64-hex токен целиком не влезает ни в какой правый
        // столбец. Плейнтекст живёт только в этом поле до пересборки окна — хранится лишь SHA-256.
        string tokenTitle = Localization.Loc.T("settings.api.token");
        string tokenDesc = Localization.Loc.T("settings.api.token.desc");

        var tokenField = ui.TextField("", ui.RowW - ui.Sc(32), _ => { });
        tokenField.ReadOnly = true;
        tokenField.AccessibleName = tokenTitle;
        var gen = ui.LinkButton("settings.api.token.generate", () =>
        {
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            s.TokenSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
            act.ApiApplied();
            tokenField.Text = token;
            tokenField.Focus();
            tokenField.SelectAll(); // сразу под Ctrl+C — второго показа не будет
        });
        gen.Enabled = s.Enabled;
        // ширина кнопки — явно и сразу: AutoSize досчитал бы её после расстановки контролов
        gen.AutoSize = false;
        gen.Width = TextRenderer.MeasureText(gen.Text, gen.Font).Width + ui.Sc(24);

        Controls.Add(ui.FieldCard(tokenTitle, tokenDesc, tokenField, gen));

        // Пер-командные разрешения. Тумблер команды, чья фича выключена в «Функциях», —
        // серый: сначала включите фичу, потом открывайте её в API.
        ui.AddGroup(this, "settings.api.cmds");
        void Cmd(string key, bool val, Action<bool> set, bool featureOn = true)
        {
            var t = ui.Toggle(val, on => { set(on); act.ApiApplied(); });
            t.Enabled = s.Enabled && featureOn;
            ui.AddRow(this, key, key + ".desc", t);
        }
        Cmd("settings.api.cmd.mode", s.AllowMode, v => s.AllowMode = v);
        Cmd("settings.api.cmd.care", s.AllowCare, v => s.AllowCare = v);
        Cmd("settings.api.cmd.travel", s.AllowTravel, v => s.AllowTravel = v);
        Cmd("settings.api.cmd.owl", s.AllowOwl, v => s.AllowOwl = v, cfg.OwlMode);

        // ---- Вебхук (XIC-75) ----
        // Мастер-тумблером API НЕ гасится намеренно: входящий сервер и исходящее событие —
        // независимые способности. Заставлять открывать слушающий сокет ради одного POST
        // наружу значило бы просить пользователя об уступке в безопасности ни за что.
        ui.AddGroup(this, "settings.api.webhook.group");
        ui.AddNote(this, "settings.api.webhook.note");
        var hook = ui.Toggle(s.WebhookOnChargeLimit, on => { s.WebhookOnChargeLimit = on; act.ApiApplied(); });
        ui.AddRow(this, "settings.api.webhook", "settings.api.webhook.desc", hook);
        var url = ui.TextField(s.WebhookUrl ?? "", ui.RowW - ui.Sc(32), v =>
        {
            string trimmed = v.Trim();
            // пустое — выключено; мусор не сохраняем молча, а откатываем показом прежнего
            if (trimmed.Length == 0 || Webhook.IsAllowed(trimmed))
            {
                s.WebhookUrl = trimmed.Length == 0 ? null : trimmed;
                act.ApiApplied();
            }
            else rebuild();
        });
        url.AccessibleName = Loc.T("settings.api.webhook.url");

        // результат пишем на самой кнопке (как «Проверить обновления»): отдельного OSD у
        // события нет, а знать, дошло ли до розетки, — весь смысл проверки
        Button test = null!;
        test = ui.LinkButton("settings.api.webhook.test", () =>
        {
            test.Enabled = false;
            test.Text = Loc.T("settings.api.webhook.testing");
            act.TestWebhook(ok =>
            {
                test.Text = Loc.T(ok ? "settings.api.webhook.ok" : "settings.api.webhook.fail");
                test.Enabled = true;
            });
        });
        test.AutoSize = false;
        // ширина по самой длинной из подписей: кнопка меняет текст и не должна прыгать
        test.Width = ui.Sc(24) + new[] { test.Text, Loc.T("settings.api.webhook.testing"), Loc.T("settings.api.webhook.ok"), Loc.T("settings.api.webhook.fail") }
            .Max(t => TextRenderer.MeasureText(t, test.Font).Width);
        test.Height = ui.Sc(30);
        // Готовность считаем по тому, что НАБРАНО, а не по сохранённому: адрес сохраняется по
        // уходу фокуса, а фокус как раз и уходит на эту кнопку. Кнопка, серая до пересборки
        // окна, выглядит как сломанная — именно так это и выглядело.
        test.Enabled = Webhook.IsAllowed(url.Text.Trim());
        url.TextChanged += (_, _) =>
        {
            test.Enabled = Webhook.IsAllowed(url.Text.Trim());
            test.Text = Loc.T("settings.api.webhook.test"); // адрес правят — прошлый результат уже не про него
        };
        Controls.Add(ui.FieldCard(Loc.T("settings.api.webhook.url"), Loc.T("settings.api.webhook.url.desc"), url, test));

        // ---- Примеры запросов (XIC-71) ----
        // Без них вкладка кончалась на «включено, токен сгенерирован» — и что дальше, знал
        // только тот, кто читал README. Порт и адрес подставляем фактические, токен — как
        // плейсхолдер: настоящий мы не храним (только его SHA-256).
        ui.AddGroup(this, "settings.api.examples");
        ui.AddNote(this, "settings.api.examples.note");
        // «Подробнее» — на полное описание протокола: коды ответов, вебхук и готовый сценарий
        // для Home Assistant в три экрана настроек не поместятся и не должны
        var docs = ui.LinkButton("settings.api.docs.btn", () => Open(DocsUrl));
        docs.AutoSize = false;
        docs.Width = TextRenderer.MeasureText(docs.Text, docs.Font).Width + ui.Sc(24);
        docs.Height = ui.Sc(30);
        ui.AddRow(this, "settings.api.docs", "settings.api.docs.desc", docs);
        string host = s.LanAccess ? Loc.T("settings.api.examples.host") : "127.0.0.1";
        foreach (var (titleKey, sample) in Examples(host, s.Port))
        {
            var box = ui.TextField(sample, ui.RowW - ui.Sc(32), _ => { });
            box.ReadOnly = true;                   // поле, а не Label: выделяется и копируется Ctrl+C
            box.AccessibleName = Loc.T(titleKey);
            Controls.Add(ui.FieldCard(Loc.T(titleKey), null, box)); // заголовка хватает — что делает команда, видно из неё
        }
    }

    private static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* нет браузера — не повод падать */ }
    }

    // Готовые команды: curl (он же есть в Windows 10+ из коробки) и PowerShell — первая
    // читает состояние, вторая переключает режим. Больше не нужно: остальные маршруты
    // устроены так же, а полное описание — в docs/15-http-api.md по кнопке «Подробнее».
    private static (string TitleKey, string Sample)[] Examples(string host, int port) =>
    [
        ("settings.api.example.status",
            $"curl -H \"Authorization: Bearer ВАШ_ТОКЕН\" http://{host}:{port}/status"),
        ("settings.api.example.mode",
            $"curl -X POST -H \"Authorization: Bearer ВАШ_ТОКЕН\" -H \"Content-Type: application/json\" -d \"{{\\\"value\\\":\\\"Turbo\\\"}}\" http://{host}:{port}/mode"),
        ("settings.api.example.care",
            $"Invoke-RestMethod http://{host}:{port}/care -Method Post -Headers @{{Authorization='Bearer ВАШ_ТОКЕН'}} -ContentType 'application/json' -Body '{{\"on\":true}}'"),
    ];
}
