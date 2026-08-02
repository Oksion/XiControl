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

        // Итог проверки — из результата, что уже лежит в памяти. Своего запроса вкладка НЕ делает:
        // окно пересобирается на каждый показ (и на смену темы/DPI/языка), запрос отсюда улетал бы
        // по нескольку раз за сессию. Что показать, решает статус, а не сам факт находки: релиз
        // найден всегда, но «доступно обновление» — только когда он реально новее.
        var upd = act.GetUpdate();
        var status = act.GetUpdateStatus();
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Width = ui.RowW, Margin = new Padding(0, 0, 0, ui.Sc(8)), BackColor = ui.T.WinBg };

        if (status == UpdateStatus.Available && upd is not null)
        {
            row.Controls.Add(Note(Loc.T("settings.updates.available", upd.Tag), ui.T.Accent));
            row.Controls.Add(ui.LinkButton("settings.updates.open", () => Open(upd.Url)));
        }
        else
        {
            // проверки могло и не быть (тумблер выключен) — даём явную кнопку: запрос только по
            // нажатию, фонового трафика по-прежнему ноль
            row.Controls.Add(ui.LinkButton("settings.updates.now", () => act.CheckUpdatesNow(rebuild)));
            string? text = status switch
            {
                // дев-сборку «последней версией» не называем — это было бы неправдой
                UpdateStatus.DevBuild when upd is not null => Loc.T("settings.updates.dev", upd.Tag),
                UpdateStatus.UpToDate => Loc.T("settings.updates.uptodate"),
                UpdateStatus.Failed => Loc.T("settings.updates.failed"),
                _ => null, // ещё не проверяли — говорить нечего
            };
            if (text is not null) row.Controls.Add(Note(text, ui.T.Text2, indent: true));
        }
        Controls.Add(row);

        Label Note(string text, Color color, bool indent = false) => new()
        {
            Text = text,
            AutoSize = true,
            ForeColor = color,
            BackColor = Color.Transparent,
            Font = ui.NoteFont,
            Margin = new Padding(indent ? ui.Sc(8) : 0, ui.Sc(6), indent ? 0 : ui.Sc(8), 0),
        };

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
