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

        int textW = Math.Max(ui.Sc(120), ui.RowW - gen.Width - ui.Sc(48)); // текст не лезет под кнопку
        int descH = TextRenderer.MeasureText(tokenDesc, ui.DescFont, new Size(textW, 0), TextFormatFlags.WordBreak).Height;
        int fieldY = ui.Sc(29) + descH + ui.Sc(10);
        int cardH = fieldY + tokenField.Height + ui.Sc(14);
        var card = new Panel { Width = ui.RowW, Height = cardH, BackColor = ui.T.Card, Margin = new Padding(0, 0, 0, ui.Sc(4)) };
        card.Region = new Region(Draw.Rounded(new RectangleF(0, 0, ui.RowW, cardH), ui.Sc(6)));
        card.Paint += (_, e) => ui.PaintCardBorder(e.Graphics, ui.RowW, cardH);
        card.Controls.Add(new Label { Text = tokenTitle, AutoSize = false, Width = textW, Height = ui.Sc(20), ForeColor = ui.T.Text, BackColor = Color.Transparent, Font = ui.TitleFont, Location = new Point(ui.Sc(16), ui.Sc(9)), AutoEllipsis = true });
        card.Controls.Add(new Label { Text = tokenDesc, AutoSize = false, Width = textW, Height = descH + ui.Sc(2), ForeColor = ui.T.Text2, BackColor = Color.Transparent, Font = ui.DescFont, Location = new Point(ui.Sc(16), ui.Sc(29)) });
        gen.Location = new Point(ui.RowW - gen.Width - ui.Sc(16), (fieldY - gen.Height) / 2); // по центру текстового блока
        tokenField.Location = new Point(ui.Sc(16), fieldY);
        card.Controls.Add(gen);
        card.Controls.Add(tokenField);
        Controls.Add(card);

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
