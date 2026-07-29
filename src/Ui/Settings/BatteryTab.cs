using XiControl.Config;
using XiControl.Localization;

namespace XiControl.Ui.Settings;

/// <summary>Вкладка «Батарея»: джингл «в дорогу», OSD зарядника, здоровье батареи (read-only).</summary>
public sealed class BatteryTab : SettingsPane
{
    public BatteryTab(SettingsToolkit ui, AppConfig cfg, SettingsActions act) : base(ui)
    {
        ui.AddHeader(this, "settings.tab.battery", "settings.battery.sub");

        // Порог «беречь батарею»: панель/меню переключают между этим X и 100%, поэтому сам выбор
        // живёт здесь (редкая настройка), а не в быстрой панели. Набор — уровни, которые держит
        // прошивка (docs/12-charge-levels.md); модель без granular отвергнет — AppController откатит.
        ui.AddGroup(this, "settings.battery.care");
        int[] presets = Wmi.Mifs.ChargeCarePresets;
        int PresetIndex() => Math.Max(0, Array.IndexOf(presets, cfg.CarePercent()));
        bool reverting = false;   // откат ниже сам дёргает SelectedIndexChanged — не пишем повторно
        ComboBox limit = null!;
        limit = ui.Combo([.. presets.Select(p => $"{p}%")], PresetIndex(), i =>
        {
            if (reverting) return;
            act.SetCareLimit(presets[i]);
            // прошивка отвергла уровень → конфиг не изменился — вернуть комбо фактический выбор
            if (limit.SelectedIndex != PresetIndex())
            {
                reverting = true;
                limit.SelectedIndex = PresetIndex();
                reverting = false;
            }
        }, ui.Sc(120));
        ui.AddRow(this, "settings.battery.care.limit", "settings.battery.care.limit.desc", limit);
        ui.AddNote(this, "settings.battery.note");

        ui.AddGroup(this, "settings.battery.travel");
        ui.AddRow(this, "settings.travel.sound", "settings.travel.sound.desc",
            ui.Toggle(cfg.TravelSound, on => { cfg.TravelSound = on; cfg.Save(); }));

        var soundBox = ui.TextField(cfg.TravelSoundFile ?? "", ui.Sc(230), s =>
        {
            cfg.TravelSoundFile = string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            cfg.Save();
        });
        soundBox.PlaceholderText = "%USERPROFILE%\\Music\\ready.wav";
        var browse = ui.LinkButton("settings.browse", () =>
        {
            using var d = new OpenFileDialog { Filter = "WAV|*.wav", CheckFileExists = true };
            if (d.ShowDialog(FindForm()) == DialogResult.OK)
            {
                soundBox.Text = d.FileName;
                cfg.TravelSoundFile = d.FileName; cfg.Save();
            }
        });
        browse.AutoSize = false;
        browse.Size = new Size(ui.Sc(92), ui.Sc(28));
        ui.AddRow(this, "settings.travel.file", "settings.travel.file.desc", ui.Pair(soundBox, browse));

        // «слепая» обратная связь на заблокированном экране (XIC-11): OSD под локскрином
        // не виден, поэтому джингл + toast; сноска — как включить показ содержимого в Windows
        ui.AddRow(this, "settings.travel.lock.sound", "settings.travel.lock.sound.desc",
            ui.Toggle(cfg.TravelLockSound, on => { cfg.TravelLockSound = on; cfg.Save(); }));
        ui.AddRow(this, "settings.travel.lock.toast", "settings.travel.lock.toast.desc",
            ui.Toggle(cfg.TravelLockToast, on => { cfg.TravelLockToast = on; cfg.Save(); }));
        ui.AddNote(this, "settings.travel.lock.note");

        ui.AddGroup(this, "settings.charger");
        ui.AddRow(this, "settings.charger.watts", "settings.charger.watts.desc",
            ui.Toggle(cfg.ChargerWattsOsd, on => { cfg.ChargerWattsOsd = on; cfg.Save(); }));
        // порог «слабого зарядника»: 0 = выкл, иначе Вт
        int[] thresholds = [0, 30, 45, 60, 90];
        ui.AddRow(this, "settings.charger.weak", "settings.charger.weak.desc",
            ui.Combo([.. thresholds.Select(t => t == 0 ? Loc.T("settings.act.none") : Loc.T("osd.charger.watts", t))],
                Math.Max(0, Array.IndexOf(thresholds, cfg.WeakChargerWatts)),
                i => { cfg.WeakChargerWatts = thresholds[i]; cfg.Save(); }, ui.Sc(120)));

        // состояние батареи — только чтение (WMI-классы ACPI + SOH1); каждую строку прячем,
        // если прошивка не отдаёт значение (на части моделей поля пустые)
        var bat = act.GetBatteryReport();
        if (bat.HealthPercent is not null || bat.Cycles is not null || bat.DesignWh > 0)
        {
            ui.AddGroup(this, "settings.battery.state");
            if (bat.HealthPercent is int hp)
                ui.AddRow(this, "settings.battery.health", "settings.battery.health.desc", ui.ValueLabel($"{hp}%"));
            if (bat.Cycles is int cy)
                ui.AddRow(this, "settings.battery.cycles", "settings.battery.cycles.desc", ui.ValueLabel($"{cy}"));
            if (bat.DesignWh > 0 && bat.FullWh > 0)
                ui.AddRow(this, "settings.battery.capacity", "settings.battery.capacity.desc",
                    ui.ValueLabel(Loc.T("settings.battery.capacity.val", bat.FullWh, bat.DesignWh)));
        }
    }
}
