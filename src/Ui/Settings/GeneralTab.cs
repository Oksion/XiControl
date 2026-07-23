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
        ui.AddRow(this, "settings.profile.brightness", "settings.brightness.desc",
            ui.Toggle(cfg.RememberBrightness, act.SetRememberBrightness));
        ui.AddRow(this, "settings.owl.feature", "settings.owl.feature.desc",
            ui.Toggle(cfg.OwlMode, act.SetOwlFeature));
        ui.AddRow(this, "settings.touchpad.feature", "settings.touchpad.feature.desc",
            ui.Toggle(cfg.TouchpadFeature, on => { cfg.TouchpadFeature = on; cfg.Save(); }));
        ui.AddRow(this, "settings.touchscreen.feature", "settings.touchscreen.feature.desc",
            ui.Toggle(cfg.TouchscreenFeature, on => { cfg.TouchscreenFeature = on; cfg.Save(); }));
        ui.AddRow(this, "settings.log", "settings.log.desc",
            ui.Toggle(cfg.LogEnabled, on =>
            {
                cfg.LogEnabled = on;
                cfg.Save();
                if (on) { Log.Enabled = true; Log.Write("Логирование включено"); }
                else { Log.Write("Логирование выключено"); Log.Enabled = false; } // прощальная строчка — видно, что тишина намеренная
            }));
    }

    // Индекс языка с данной культурой в списке (−1 — нет; вызывающий приведёт к 0).
    private static int IndexOfCulture(IReadOnlyList<LangInfo> langs, string culture)
    {
        for (int i = 0; i < langs.Count; i++)
            if (string.Equals(langs[i].Culture, culture, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}
