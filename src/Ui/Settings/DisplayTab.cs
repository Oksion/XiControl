using XiControl.Config;
using XiControl.Localization;

namespace XiControl.Ui.Settings;

/// <summary>
/// Вкладка «Экран»: яркость (лимит + запоминание, XIC-29) и авто-герцовка. С XIC-29 вкладка
/// видна всегда — яркость от фичи «управление частотой» не зависит; при выключенной фиче
/// скрывается только раздел частоты.
/// </summary>
public sealed class DisplayTab : SettingsPane
{
    public DisplayTab(SettingsToolkit ui, AppConfig cfg, SettingsActions act, Action rebuild) : base(ui)
    {
        ui.AddHeader(this, "settings.tab.display", "settings.display.sub");

        // ---- Яркость ----
        ui.AddGroup(this, "settings.bright.group");
        // rebuild — зажечь/погасить комбо лимитов и плашку про адаптивную яркость
        ui.AddRow(this, "settings.bright.cap", "settings.bright.cap.desc",
            ui.Toggle(cfg.BrightnessCapEnabled, on => { act.SetBrightnessCap(on); rebuild(); }));
        var capAc = PercentCombo(cfg.BrightnessCapAc, v => act.SetBrightnessCaps(v, cfg.BrightnessCapBattery));
        capAc.Enabled = cfg.BrightnessCapEnabled;
        ui.AddRow(this, "settings.bright.cap.ac", "settings.bright.cap.ac.desc", capAc);
        var capBatt = PercentCombo(cfg.BrightnessCapBattery, v => act.SetBrightnessCaps(cfg.BrightnessCapAc, v));
        capBatt.Enabled = cfg.BrightnessCapEnabled;
        ui.AddRow(this, "settings.bright.cap.battery", "settings.bright.cap.battery.desc", capBatt);
        // авто-яркость по датчику (XIC-30) — только на машинах с датчиком; Available
        // выясняется в фоне на старте, к открытию окна ответ обычно уже есть
        if (act.IsAlsAvailable())
            ui.AddRow(this, "settings.bright.auto", "settings.bright.auto.desc",
                ui.Toggle(cfg.AutoBrightness, on => { act.SetAutoBrightness(on); rebuild(); }));
        // честная плашка: с адаптивной яркостью Windows ни лимит, ни авто-яркость не работают
        if ((cfg.BrightnessCapEnabled || cfg.AutoBrightness) && act.IsAdaptiveBrightness())
            ui.AddNote(this, "settings.bright.adaptive");
        var remember = ui.Toggle(cfg.RememberBrightness, act.SetRememberBrightness);
        remember.Enabled = !cfg.AutoBrightness; // кривая заменяет слоты — два хозяина не нужны
        ui.AddRow(this, "settings.profile.brightness", "settings.brightness.desc", remember);

        // ---- Частота — только пока «управление частотой» включено во вкладке «Функции» ----
        if (!cfg.RefreshRateFeature) return;
        ui.AddGroup(this, "settings.hz.group");
        // мастер-тумблер: rebuild гасит/зажигает «удерживать» — без авто-частоты возвращать нечего
        ui.AddRow(this, "settings.hz.auto", "settings.hz.auto.desc",
            ui.Toggle(cfg.AutoRefreshRate, on => { act.SetAutoHz(on); rebuild(); }));
        var hold = ui.Toggle(cfg.HoldRefreshRate, act.SetHoldRefreshRate);
        hold.Enabled = cfg.AutoRefreshRate;
        ui.AddRow(this, "settings.hz.hold", "settings.hz.hold.desc", hold);
        ui.AddGroup(this, "settings.hz.rates");
        ui.AddRow(this, "settings.hz.ac", "settings.hz.ac.desc",
            HzCombo(cfg.AcRefreshRate, hz => act.SetRefreshRates(hz, cfg.BatteryRefreshRate)));
        ui.AddRow(this, "settings.hz.battery", "settings.hz.battery.desc",
            HzCombo(cfg.BatteryRefreshRate, hz => act.SetRefreshRates(cfg.AcRefreshRate, hz)));
        ui.AddNote(this, "settings.hz.note");
    }

    // Комбо частоты: пресеты + текущее значение из config.json, если оно нестандартное
    // (вручную вписанные 165 Гц не должны отображаться как «144»)
    private ComboBox HzCombo(int current, Action<int> apply)
    {
        int[] presets = [144, 120, 90, 60, 48];
        int[] rates = presets.Contains(current) ? presets : [current, .. presets];
        return Ui.Combo([.. rates.Select(r => $"{r} " + Loc.T("settings.hz.unit"))],
            Array.IndexOf(rates, current), i => apply(rates[i]), Ui.Sc(110));
    }

    // Комбо лимита яркости: та же механика — рукописное значение из config.json не подменяем пресетом
    private ComboBox PercentCombo(int current, Action<int> apply)
    {
        int[] presets = [90, 80, 70, 60, 50, 40, 30];
        int[] caps = presets.Contains(current) ? presets : [current, .. presets];
        return Ui.Combo([.. caps.Select(c => $"{c}%")],
            Array.IndexOf(caps, current), i => apply(caps[i]), Ui.Sc(110));
    }
}
