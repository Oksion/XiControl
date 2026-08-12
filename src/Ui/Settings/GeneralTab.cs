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
        string[] metricValues = ["power", "cpu", "gpu", "ram", "temp"];
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
    }

    // Индекс языка с данной культурой в списке (−1 — нет; вызывающий приведёт к 0).
    private static int IndexOfCulture(IReadOnlyList<LangInfo> langs, string culture)
    {
        for (int i = 0; i < langs.Count; i++)
            if (string.Equals(langs[i].Culture, culture, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}
