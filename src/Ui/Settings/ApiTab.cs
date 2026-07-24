using System.Security.Cryptography;
using System.Text;
using XiControl.Config;

namespace XiControl.Ui.Settings;

/// <summary>
/// Вкладка «HTTP API» (XIC-13): opt-in веб-API для локалки (телефон / Home Assistant).
/// Настройки живут в api.json (ProgramData, ACL «запись только админам») — не в config.json,
/// чтобы непривилегированный процесс не мог включить API или подменить токен. Токен показывается
/// один раз при генерации; write-команды включаются поштучно, по умолчанию только GET /status.
/// </summary>
public sealed class ApiTab : SettingsPane
{
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

        // токен: плейнтекст живёт только в этом поле до пересборки окна — хранится лишь SHA-256
        var tokenField = ui.TextField("", ui.Sc(210), _ => { });
        tokenField.ReadOnly = true;
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
        ui.AddRow(this, "settings.api.token", "settings.api.token.desc", ui.Pair(gen, tokenField));

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
    }
}
