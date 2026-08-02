using System.Diagnostics;
using XiControl.Localization;
using XiControl.SystemIntegration;

namespace XiControl.Ui.Settings;

/// <summary>Вкладка «О программе»: версия, обновление, ссылки, модель, пути конфига/лога.</summary>
public sealed class AboutTab : SettingsPane
{
    public AboutTab(SettingsToolkit ui, SettingsActions act, Action rebuild) : base(ui)
    {
        ui.AddHeader(this, "settings.tab.about", "settings.about.sub");

        var hero = new Panel { Width = ui.RowW, Height = ui.Sc(74), BackColor = ui.T.WinBg, Margin = new Padding(0, 0, 0, ui.Sc(8)) };
        var ico = new PictureBox
        {
            Image = SvgIcons.Render("settings", ui.Sc(52)),
            SizeMode = PictureBoxSizeMode.CenterImage,
            Size = new Size(ui.Sc(60), ui.Sc(60)),
            Location = new Point(0, ui.Sc(6)),
            BackColor = ui.T.Sel,
        };
        ico.Region = new Region(Draw.Rounded(new RectangleF(0, 0, ui.Sc(60), ui.Sc(60)), ui.Sc(12)));
        hero.Controls.Add(ico);
        var name = new Label { Text = "XiControl", Font = ui.NameFont, AutoSize = true, ForeColor = ui.T.Text, BackColor = Color.Transparent, Location = new Point(ui.Sc(74), ui.Sc(10)) };
        var ver = new Label { Text = $"{Loc.T("settings.version")} {AppVersion()}  ·  GPLv3", Tag = "dim", AutoSize = true, ForeColor = ui.T.Text2, BackColor = Color.Transparent, Font = ui.NoteFont, Location = new Point(ui.Sc(74), ui.Sc(40)) };
        hero.Controls.Add(name); hero.Controls.Add(ver);
        Controls.Add(hero);

        // Отметка о новой версии — из результата стартовой проверки, что уже лежит в памяти.
        // Своего запроса вкладка НЕ делает: окно пересобирается на каждый показ (и на смену
        // темы/DPI/языка), запрос отсюда улетал бы по нескольку раз за сессию.
        if (act.GetUpdate() is { } upd)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Width = ui.RowW, Margin = new Padding(0, 0, 0, ui.Sc(8)), BackColor = ui.T.WinBg };
            row.Controls.Add(new Label
            {
                Text = Loc.T("settings.updates.available", upd.Tag),
                AutoSize = true,
                ForeColor = ui.T.Accent,
                BackColor = Color.Transparent,
                Font = ui.NoteFont,
                Margin = new Padding(0, ui.Sc(6), ui.Sc(8), 0),
            });
            row.Controls.Add(ui.LinkButton("settings.updates.open", () => Open(upd.Url)));
            Controls.Add(row);
        }
        else
        {
            // Проверки могло и не быть (тумблер выключен) — даём явную кнопку: запрос только по
            // нажатию, фонового трафика по-прежнему ноль. Рядом — итог прошлой проверки: без него
            // нажатие выглядит как «ничего не произошло» (окно лишь мигает на пересборке).
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Width = ui.RowW, Margin = new Padding(0, 0, 0, ui.Sc(8)), BackColor = ui.T.WinBg };
            row.Controls.Add(ui.LinkButton("settings.updates.now", () => act.CheckUpdatesNow(rebuild)));

            string? status = act.GetUpdateStatus() switch
            {
                UpdateStatus.UpToDate => Loc.T("settings.updates.uptodate"),
                UpdateStatus.Failed => Loc.T("settings.updates.failed"),
                _ => null, // ещё не проверяли — говорить нечего
            };
            if (status is not null)
                row.Controls.Add(new Label
                {
                    Text = status,
                    AutoSize = true,
                    ForeColor = ui.T.Text2,
                    BackColor = Color.Transparent,
                    Font = ui.NoteFont,
                    Margin = new Padding(ui.Sc(8), ui.Sc(6), 0, 0),
                });
            Controls.Add(row);
        }

        var links = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Width = ui.RowW, Margin = new Padding(0, 0, 0, ui.Sc(14)), BackColor = ui.T.WinBg };
        links.Controls.Add(ui.LinkButton("GitHub", () => Open("https://github.com/Oksion/XiControl")));
        links.Controls.Add(ui.LinkButton("settings.about.forum", () => Open("https://4pda.to/forum/index.php?showtopic=1122287")));
        links.Controls.Add(ui.LinkButton("settings.about.license", () => Open("https://github.com/Oksion/XiControl/blob/main/LICENSE")));
        links.Controls.Add(ui.LinkButton("settings.about.updates", () => Open("https://github.com/Oksion/XiControl/releases")));
        Controls.Add(links);

        ui.AddKv(this, "settings.about.model", SafeModel());
        ui.AddKv(this, "settings.about.iface", "MiCommonInterface (MIFS)");
        ui.AddKv(this, "settings.about.config", "%APPDATA%\\XiControl\\config.json");
        ui.AddKv(this, "settings.about.log", "%APPDATA%\\XiControl\\log.txt");
        ui.AddNote(this, "settings.about.note");
    }

    private static string AppVersion()
    {
        try { return FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).ProductVersion?.Split('+')[0] ?? "—"; }
        catch { return "—"; }
    }

    private static string SafeModel()
    {
        try
        {
            using var s = new System.Management.ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem");
            foreach (var o in s.Get()) return o["Model"]?.ToString() ?? "—";
        }
        catch { /* WMI недоступен */ }
        return "—";
    }

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* нет браузера */ }
    }
}
