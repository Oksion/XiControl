using XiControl.Config;
using XiControl.Localization;

namespace XiControl.Ui.Settings;

/// <summary>Вкладка «Экран»: авто-герцовка и частоты для сети/батареи.</summary>
public sealed class DisplayTab : SettingsPane
{
    public DisplayTab(SettingsToolkit ui, AppConfig cfg, SettingsActions act) : base(ui)
    {
        ui.AddHeader(this, "settings.tab.display", "settings.display.sub");
        ui.AddRow(this, "settings.hz.auto", "settings.hz.auto.desc",
            ui.Toggle(cfg.AutoRefreshRate, act.SetAutoHz));
        ui.AddRow(this, "settings.hz.hold", "settings.hz.hold.desc",
            ui.Toggle(cfg.HoldRefreshRate, act.SetHoldRefreshRate));
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
}
