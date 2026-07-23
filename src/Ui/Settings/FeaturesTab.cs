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
            ui.Toggle(cfg.TouchpadFeature, on => { cfg.TouchpadFeature = on; cfg.Save(); }));
        ui.AddRow(this, "settings.touchscreen.feature", "settings.touchscreen.feature.desc",
            ui.Toggle(cfg.TouchscreenFeature, on => { cfg.TouchscreenFeature = on; cfg.Save(); }));
        // выкл/вкл прячет-показывает вкладку «Экран» → пересобрать окно (после выхода из обработчика)
        ui.AddRow(this, "settings.refresh.feature", "settings.refresh.feature.desc",
            ui.Toggle(cfg.RefreshRateFeature, on => { act.SetRefreshRateFeature(on); rebuild(); }));
    }
}
