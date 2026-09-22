using XiControl.Config;

namespace XiControl.Ui.Settings;

/// <summary>
/// Вкладка «Функции»: какие возможности показывать в меню/панели. Выключенная функция
/// исчезает из UI целиком (пункт меню, ячейка панели, действие для клавиш) — для тех, кому
/// она не нужна. «Управление частотой» вдобавок прячет вкладку «Экран» (окно пересобирается).
/// </summary>
public sealed class FeaturesTab : SettingsPane
{
    public FeaturesTab(SettingsToolkit ui, AppConfig cfg, SettingsActions act, Action rebuild) : base(ui)
    {
        ui.AddHeader(this, "settings.tab.features", "settings.features.sub");

        ui.AddRow(this, "settings.owl.feature", "settings.owl.feature.desc",
            ui.Toggle(cfg.OwlMode, act.SetOwlFeature));
        ui.AddRow(this, "settings.touchpad.feature", "settings.touchpad.feature.desc",
            ui.Toggle(cfg.TouchpadFeature, on => { cfg.TouchpadFeature = on; cfg.Save(); rebuild(); }));
        // «Помнить выключенным» — дочерней строкой к своему устройству, и только пока фича
        // видна: при скрытой фиче выключить устройство неоткуда, и настройка была бы о пустом
        if (cfg.TouchpadFeature)
            Controls.Add(ui.SubRow("settings.keepoff",
                ui.Toggle(cfg.TouchpadKeepOff, on => { cfg.TouchpadKeepOff = on; cfg.Save(); })));

        ui.AddRow(this, "settings.touchscreen.feature", "settings.touchscreen.feature.desc",
            ui.Toggle(cfg.TouchscreenFeature, on => { cfg.TouchscreenFeature = on; cfg.Save(); rebuild(); }));
        if (cfg.TouchscreenFeature)
            Controls.Add(ui.SubRow("settings.keepoff",
                ui.Toggle(cfg.TouchscreenKeepOff, on => { cfg.TouchscreenKeepOff = on; cfg.Save(); })));
        ui.AddNote(this, "settings.keepoff.note");
        // выкл/вкл прячет-показывает раздел частоты во вкладке «Экран» → пересобрать окно
        // (после выхода из обработчика); сама вкладка с XIC-29 видна всегда — там яркость
        ui.AddRow(this, "settings.refresh.feature", "settings.refresh.feature.desc",
            ui.Toggle(cfg.RefreshRateFeature, on => { act.SetRefreshRateFeature(on); rebuild(); }));
    }
}
