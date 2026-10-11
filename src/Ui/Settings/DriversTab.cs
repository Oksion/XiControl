using System.Diagnostics;
using System.Globalization;
using XiControl.Localization;
using XiControl.SystemIntegration;

namespace XiControl.Ui.Settings;

/// <summary>
/// Вкладка «Драйверы» (XIC-95): что Xiaomi предлагает для этой модели и как это соотносится с
/// установленным. В сеть — только по кнопке; ничего не скачиваем и не ставим. Обновления — со
/// ссылкой на пакет (открывается в браузере после второго нажатия); у BIOS — тоже, но под жёстким
/// предупреждением «на свой страх и риск».
/// Результат живёт в контроллере: окно пересобирается на каждый показ, запроса отсюда нет.
/// </summary>
public sealed class DriversTab : SettingsPane
{
    public DriversTab(SettingsToolkit ui, SettingsActions act, Action rebuild) : base(ui)
    {
        ui.AddHeader(this, "settings.tab.drivers", "settings.drivers.sub");
        ui.AddNote(this, "settings.drivers.privacy");

        var r = act.GetDriverCheck();
        bool running = r.Status == DriverCheckStatus.Running;
        var check = Button(ui, running ? "settings.drivers.status.running" : "settings.drivers.check.btn",
            () => act.CheckDriversNow(rebuild));
        check.Enabled = !running;
        Controls.Add(ui.Row(Loc.T("settings.drivers.check"), StatusText(r), check));

        if (r.Status is DriverCheckStatus.Ok or DriverCheckStatus.NoMatch or DriverCheckStatus.NoCatalog
            || (r.Status == DriverCheckStatus.Running && r.CheckedAt != default))
            AddResult(ui, r);
    }

    private void AddResult(SettingsToolkit ui, DriverCheckResult r)
    {
        // ---- BIOS: провал прошивки = мёртвая машина, поэтому предупреждение отдельное и жёсткое ----
        ui.AddGroup(this, "settings.drivers.bios.group");
        ui.AddKv(this, "settings.drivers.bios.installed", r.BiosCurrent ?? "—");
        string offer = !r.BiosChecked ? Loc.T("settings.drivers.bios.unknown")
            : r.BiosOffer is { } b ? Loc.T("settings.drivers.bios.available", b.FileName)
            : Loc.T("settings.drivers.bios.none");
        ui.AddKv(this, "settings.drivers.bios.offer", offer);
        if (r.BiosOffer is { } bios)
        {
            // Ссылку даём (решение владельца): иначе родная утилита оставалась бы нужна ради одного
            // BIOS. Но предупреждение — жёстче, чем у драйверов, и кнопка та же, в два нажатия
            ui.AddNote(this, "settings.drivers.bios.warn");
            Controls.Add(ui.Row(Loc.T("settings.drivers.bios.package"), PackageText(bios),
                DownloadButton(ui, bios, new HashSet<string>(StringComparer.OrdinalIgnoreCase))));
        }

        if (r.Rows.Count == 0) return;

        // ---- Обновления: устройство, было → стало, пакет; один пакет — одна кнопка ----
        ui.AddGroup(this, "settings.drivers.updates.group");
        var updates = r.Rows.Where(x => x.State == DriverState.Update).ToList();
        var firmware = r.Rows.Where(x => x.State == DriverState.Firmware).ToList();
        if (updates.Count == 0 && firmware.Count == 0)
            ui.AddNote(this, "settings.drivers.updates.none");
        else
        {
            ui.AddNote(this, "settings.drivers.updates.warn");
            var offered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in updates)
            {
                string desc = Loc.T("settings.drivers.update.desc", u.Installed ?? "—", u.Catalog, PackageText(u.Package));
                Controls.Add(ui.Row(u.Device, desc, DownloadButton(ui, u.Package, offered)));
            }
            foreach (var f in firmware)
            {
                string desc = Loc.T("settings.drivers.firmware.desc", f.Catalog, PackageText(f.Package));
                Controls.Add(ui.Row(Loc.T("settings.drivers.firmware.title", f.Device), desc, DownloadButton(ui, f.Package, offered)));
            }
        }

        // ---- Сводка по модулям: под сотню строк «совпадает» читать незачем ----
        ui.AddGroup(this, "settings.drivers.summary.group");
        foreach (var g in r.Rows.Where(x => x.State != DriverState.Firmware).GroupBy(x => x.Module).OrderBy(g => ModuleName(g.Key), StringComparer.CurrentCulture))
        {
            var parts = new List<string>();
            int up = g.Count(x => x.State == DriverState.Update), same = g.Count(x => x.State == DriverState.Same), newer = g.Count(x => x.State == DriverState.Newer);
            if (up > 0) parts.Add(Loc.T("settings.drivers.summary.update", up));
            if (same > 0) parts.Add(Loc.T("settings.drivers.summary.same", same));
            if (newer > 0) parts.Add(Loc.T("settings.drivers.summary.newer", newer));
            var value = ui.ValueLabel(string.Join(" · ", parts));
            if (up > 0) value.ForeColor = ui.T.Accent;
            Controls.Add(ui.Row(ModuleName(g.Key), g.First().Device, value));
        }
        if (r.Rows.Any(x => x.State == DriverState.Newer)) ui.AddNote(this, "settings.drivers.summary.note");
    }

    private static string StatusText(DriverCheckResult r)
    {
        string at = r.CheckedAt == default ? "" : r.CheckedAt.ToString("t", CultureInfo.CurrentCulture);
        return r.Status switch
        {
            DriverCheckStatus.NotChecked => Loc.T("settings.drivers.status.never"),
            DriverCheckStatus.Running => Loc.T("settings.drivers.status.running"),
            DriverCheckStatus.Failed => Loc.T("settings.drivers.status.failed"),
            DriverCheckStatus.NoCatalog => Loc.T("settings.drivers.status.nocatalog", r.Product ?? "—"),
            DriverCheckStatus.NoMatch => Loc.T("settings.drivers.status.nomatch", r.Product ?? "—"),
            _ when r.Failed > 0 => Loc.T("settings.drivers.status.partial", at, r.Failed),
            _ => Loc.T("settings.drivers.status.done", at),
        };
    }

    // Код модуля → человеческое имя; незнакомый код показываем как есть
    private static string ModuleName(string code)
    {
        string key = "settings.drivers.mod." + code;
        string name = Loc.T(key);
        return name == key ? code : name;
    }

    private static string PackageText(OemPackage? p) =>
        p is null ? "—" : Loc.T("settings.drivers.package", p.FileName, Math.Max(0.1, Math.Round(p.Size / 1048576.0, 1)));

    /// <summary>
    /// «Скачать» в два нажатия: первое превращает кнопку в «Открыть в браузере?», второе —
    /// открывает ссылку. Предупреждение висит над списком всегда; второй шаг — чтобы гигабайтный
    /// пакет не уехал в браузер случайным кликом. Один пакет закрывает много устройств — кнопка
    /// у первого из них, у остальных «тот же пакет».
    /// </summary>
    private static Control DownloadButton(SettingsToolkit ui, OemPackage? p, HashSet<string> offered)
    {
        if (p?.Url is not { } url) return ui.ValueLabel(Loc.T("settings.drivers.nolink"));
        if (!offered.Add(url)) return ui.ValueLabel(Loc.T("settings.drivers.samepackage"));
        bool armed = false;
        Button? b = null;
        b = Button(ui, "settings.drivers.download", () =>
        {
            if (!armed)
            {
                armed = true;
                b!.Text = Loc.T("settings.drivers.download.confirm");
                b.ForeColor = ui.T.Accent;
                return;
            }
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* нет браузера */ }
        });
        // Ширина — сразу под длинную надпись второго шага: карточка раскладывает описание под
        // ширину кнопки в момент постройки, и выросшая потом кнопка уходила под текст
        b.Width = Math.Max(b.Width, TextRenderer.MeasureText(Loc.T("settings.drivers.download.confirm"), ui.CtlFont).Width + ui.Sc(28));
        return b;
    }

    // LinkButton.AutoSize срабатывает позже, чем карточка считает раскладку — ширину меряем сами
    private static Button Button(SettingsToolkit ui, string key, Action click)
    {
        var b = ui.LinkButton(key, click);
        Fit(ui, b);
        return b;
    }

    private static void Fit(SettingsToolkit ui, Button b)
    {
        b.AutoSize = false;
        b.Width = TextRenderer.MeasureText(b.Text, ui.CtlFont).Width + ui.Sc(28);
        b.Height = ui.Sc(30);
    }
}
