using XiControl.Localization;
using XiControl.SystemIntegration;
using XiControl.Wmi;

namespace XiControl.Ui;

/// <summary>Какая стратегия режима при старте выбрана (взаимоисключающие).</summary>
public enum StartStrategy { None, Restore, Pin, Profiles }

/// <summary>
/// Колбэки в TrayApp/AppController: окно настроек не дублирует логику (взаимоисключения режимов
/// старта, переармливание гардов, применение профиля) — оно меняет то, что тривиально
/// (config.json), а «умные» операции делегирует сюда.
///
/// Все поля <c>required</c> (заглушек-дефолтов нет намеренно): забыть примонтировать колбэк в
/// <see cref="TrayApp"/> — теперь ошибка компиляции CS9035, а не молчаливый вызов пустышки.
/// Так закрыта грабля Фазы 6.4 (незамонтированный SetFlyoutTheme тихо звал заглушку).
/// </summary>
public sealed class SettingsActions
{
    public required Func<bool> GetAutoStart;
    public required Action<bool> SetAutoStart;
    public required Func<IReadOnlyList<LangInfo>> Languages;  // доступные языки (data-driven)
    public required Func<string> CurrentLanguage;             // текущий культурный код
    public required Action<string> SetLanguage;              // сменить язык по культурному коду
    public required Action<string?> SetFlyoutTheme;          // тема панелей/OSD: null/"light"/"system"
    public required Action<bool, bool> SetModeVisibility;    // eco, full
    public required Func<StartStrategy> GetStartStrategy;
    public required Action<StartStrategy> SetStartStrategy;
    public required Action<bool, PerfMode?> SetProfileMode;  // ac, mode
    public required Action<bool> SetRememberBrightness;
    public required Action<bool> SetAutoHz;
    public required Action<bool> SetHoldRefreshRate;         // возвращать частоту после чужих изменений
    public required Action<bool> SetRefreshRateFeature;      // «управление частотой» как фича вкл/выкл
    public required Action<int, int> SetRefreshRates;        // ac, batt
    public required Action<bool> SetCheckUpdates;            // «проверять обновления» вкл/выкл
    public required Func<ReleaseInfo?> GetUpdate;            // найденный релиз (из проверки на старте)
    public required Action<Action> CheckUpdatesNow;          // проверить по кнопке; колбэк — перерисовать вкладку
    public required Action<bool> SetTouchpadDeadZone;        // мёртвая зона у нижнего края тачпада
    public required Action<int> SetTouchpadDeadZoneMm;       // её высота в мм
    public required Action<bool> SetOwlFeature;
    public required Action<int> SetCareLimit;                // порог «беречь батарею», % (применить на железе)
    public required Func<SystemIntegration.BatteryReport> GetBatteryReport; // здоровье батареи (WMI + SOH1)
    public required Func<SystemIntegration.ApiSettings> GetApiSettings;    // настройки HTTP API (api.json, XIC-13)
    public required Action ApiApplied; // вкладка изменила настройки API → сохранить + перезапустить хост/фаервол
}
