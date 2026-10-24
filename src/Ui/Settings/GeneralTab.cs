using XiControl.Config;
using XiControl.Localization;

namespace XiControl.Ui.Settings;

/// <summary>Вкладка «Общие»: язык, автозапуск, комфорт-фичи, логирование.</summary>
public sealed class GeneralTab : SettingsPane
{
    public GeneralTab(SettingsToolkit ui, AppConfig cfg, SettingsActions act, Action rebuild) : base(ui)
    {
        ui.AddHeader(this, "settings.tab.general", "settings.general.sub");
        // список языков — из встроенных переводов (родные названия); выбор по культурному коду
        var langs = act.Languages();
        int curLang = Math.Max(0, IndexOfCulture(langs, act.CurrentLanguage()));
        ui.AddRow(this, "settings.language", "settings.language.desc",
            ui.Combo([.. langs.Select(l => l.Name)], curLang, i =>
            {
                act.SetLanguage(langs[i].Culture);
                rebuild(); // пересобрать окно на новом языке (после выхода из обработчика)
            }, ui.Sc(150)));
        ui.AddRow(this, "settings.autostart", "settings.autostart.desc",
            ui.Toggle(act.GetAutoStart(), act.SetAutoStart));
        // winget-пакет portable и сам ярлыков не создаёт — кладём свой, только по тумблеру (XIC-106)
        ui.AddRow(this, "settings.startmenu", "settings.startmenu.desc",
            ui.Toggle(act.GetStartMenu(), act.SetStartMenu));

        // тема панелей/OSD/«Монитора»: значения соответствуют AppConfig.FlyoutTheme
        string?[] themeValues = [null, "light", "system"];
        int curTheme = Math.Max(0, Array.IndexOf(themeValues, cfg.FlyoutTheme));
        ui.AddRow(this, "settings.flyout.theme", "settings.flyout.theme.desc",
            ui.Combo([Loc.T("theme.dark"), Loc.T("theme.light"), Loc.T("theme.system")], curTheme,
                i => act.SetFlyoutTheme(themeValues[i]), ui.Sc(150)));

        ui.AddGroup(this, "settings.general.comfort");
        // «Запоминать яркость» переехала на вкладку «Экран» — к лимитам яркости (XIC-29)
        // выключенный тумблер = ноль исходящих запросов, поэтому подпись прямая, без оговорок
        ui.AddRow(this, "settings.updates.check", "settings.updates.check.desc",
            ui.Toggle(cfg.CheckUpdates, act.SetCheckUpdates));
        // «Режим совы», тачпад и сенсорный экран как функции переехали на вкладку «Функции».
        ui.AddRow(this, "settings.log", "settings.log.desc",
            ui.Toggle(cfg.LogEnabled, on =>
            {
                cfg.LogEnabled = on;
                cfg.Save();
                if (on) { Log.Enabled = true; Log.Write("Логирование включено"); }
                else { Log.Write("Логирование выключено"); Log.Enabled = false; } // прощальная строчка — видно, что тишина намеренная
            }));

        // Индикатор-метрика в трее (XIC-35): второй значок с цифрой. Изменения применяются
        // сразу колбэком TrayMetricApplied (вкл/выкл — создание/уничтожение, остальное — на лету)
        ui.AddGroup(this, "settings.traymetric.group");
        ui.AddRow(this, "settings.traymetric", "settings.traymetric.desc",
            ui.Toggle(cfg.TrayMetricEnabled, on =>
            {
                cfg.TrayMetricEnabled = on;
                cfg.Save();
                act.TrayMetricApplied();
            }));
        // NPU — только если он в ноутбуке есть (XIC-86): пункт с вечным прочерком был бы шумом
        string[] metricValues = SystemIntegration.NpuLoad.Present
            ? ["power", "cpu", "gpu", "npu", "ram", "temp"]
            : ["power", "cpu", "gpu", "ram", "temp"];
        int curMetric = Math.Max(0, Array.IndexOf(metricValues, cfg.TrayMetricKind ?? "power"));
        ui.AddRow(this, "settings.traymetric.metric", "settings.traymetric.metric.desc",
            ui.Combo([.. metricValues.Select(v => Loc.T("traymetric." + v))], curMetric, i =>
            {
                cfg.TrayMetricKind = metricValues[i];
                cfg.Save();
                act.TrayMetricApplied();
            }, ui.Sc(150)));
        int[] periods = [1, 2, 5, 10];
        int curPeriod = Math.Max(0, Array.IndexOf(periods, cfg.TrayMetricPeriodSec));
        ui.AddRow(this, "settings.traymetric.period", "settings.traymetric.period.desc",
            ui.Combo([.. periods.Select(p => Loc.T("settings.traymetric.sec", p))], curPeriod, i =>
            {
                cfg.TrayMetricPeriodSec = periods[i];
                cfg.Save();
                act.TrayMetricApplied();
            }, ui.Sc(150)));

        AddTemperatureSensor(ui, cfg);
    }

    // Какой датчик DPTF считать температурой (XIC-105). Имён у доменов нет — подписываем номером и
    // текущей температурой: человек выбирает тот, что растёт под нагрузкой. Список — из последнего
    // снимка (WMI в UI-потоке не ждём, XIC-81), цифры освежаются фоном после показа вкладки.
    // Нет DPTF (AMD, ACPI-зона) — выбирать не из чего, раздела нет.
    private void AddTemperatureSensor(SettingsToolkit ui, AppConfig cfg)
    {
        var snapshot = SystemIntegration.DptfTemperature.LastDomains;
        int[] indexes = [.. snapshot.Where(d => SystemIntegration.DptfDomains.Plausible(d.Celsius)).Select(d => d.Index)];
        if (cfg.TemperatureDomain is int sel && !indexes.Contains(sel)) indexes = [.. indexes, sel]; // выбранный, но сейчас молчит
        if (indexes.Length == 0) return;

        ui.AddGroup(this, "settings.temp.group");
        int?[] values = [null, .. indexes.Select(i => (int?)i)];
        int cur = Math.Max(0, Array.IndexOf(values, cfg.TemperatureDomain));
        var combo = ui.Combo([.. values.Select(v => Label(v, snapshot))], cur, i =>
        {
            cfg.TemperatureDomain = values[i];
            cfg.Save(); // «Монитор» и трей спрашивают выбор на каждом чтении — применится само
        }, ui.Sc(200));
        ui.AddRow(this, "settings.temp.sensor", "settings.temp.sensor.desc", combo);

        // свежие цифры — с пула; вкладка могла закрыться, пока шёл WMI
        HandleCreated += (_, _) => Task.Run(() =>
        {
            using var dptf = new SystemIntegration.DptfTemperature();
            if (dptf.ReadDomains() is not { } fresh) return;
            try
            {
                BeginInvoke(() =>
                {
                    if (combo.IsDisposed) return;
                    for (int i = 1; i < values.Length; i++) combo.Items[i] = Label(values[i], fresh);
                });
            }
            catch (InvalidOperationException) { /* вкладку уже закрыли — обновлять нечего */ }
        });

        static string Label(int? index, IReadOnlyList<SystemIntegration.DptfDomain> domains)
        {
            if (index is not int n) return Loc.T("settings.temp.sensor.hottest");
            var d = domains.FirstOrDefault(x => x.Index == n);
            return SystemIntegration.DptfDomains.Plausible(d.Celsius) && d.Index == n
                ? Loc.T("settings.temp.sensor.n", n, d.Celsius)
                : Loc.T("settings.temp.sensor.silent", n);
        }
    }

    // Индекс языка с данной культурой в списке (−1 — нет; вызывающий приведёт к 0).
    private static int IndexOfCulture(IReadOnlyList<LangInfo> langs, string culture)
    {
        for (int i = 0; i < langs.Count; i++)
            if (string.Equals(langs[i].Culture, culture, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}
