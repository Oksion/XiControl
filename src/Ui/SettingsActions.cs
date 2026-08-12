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
    public required Action<bool> SetBrightnessCap;       // лимит яркости вкл/выкл (XIC-29)
    public required Action<int, int> SetBrightnessCaps;  // лимиты яркости: ac, batt
    public required Func<bool> IsAdaptiveBrightness;     // адаптивная яркость в схеме питания → лимит не работает
    public required Action<bool> SetAutoBrightness;      // авто-яркость по датчику (XIC-30)
    public required Action<bool> SetAutoBrightnessLearning; // обучение кривой вкл/выкл (XIC-37)
    public required Action<string?> SetAutoBrightnessRevert; // возврат к выученному: null/"battery"/"off"
    public required Func<bool> IsAlsAvailable;           // есть ли датчик освещённости (видимость фичи)
    public required Func<float> CurrentLux;              // живые люксы для индикатора (NaN — ещё нет)
    public required Action<int> SetBrightnessMedianSec;  // «инерция» датчика: окно медианы, сек (0 — выкл)
    public required Action ResetBrightnessCurve;         // явный сброс кривой обучения
    public required Func<bool, Config.BrightnessPoint[]> BrightnessCurvePoints; // снимок кривой (true=сеть) для графика
    public required Action<bool> SetAutoHz;
    public required Action<bool> SetHoldRefreshRate;         // возвращать частоту после чужих изменений
    public required Action<bool> SetRefreshRateFeature;      // «управление частотой» как фича вкл/выкл
    public required Action<int, int> SetRefreshRates;        // ac, batt
    public required Action<bool> SetCheckUpdates;            // «проверять обновления» вкл/выкл
    public required Func<ReleaseInfo?> GetUpdate;            // найденный релиз (из проверки на старте)
    public required Func<UpdateStatus> GetUpdateStatus;      // чем кончилась последняя проверка
    public required Action<Action> CheckUpdatesNow;          // проверить по кнопке; колбэк — перерисовать вкладку
    public required Action<bool> SetTouchpadDeadZone;        // мёртвая зона у нижнего края тачпада
    public required Action<int> SetTouchpadDeadZoneMm;       // её высота в мм
    public required Action<bool> SetOwlFeature;
    public required Action<int> SetCareLimit;                // порог «беречь батарею», % (применить на железе)
    public required Func<SystemIntegration.BatteryReport> GetBatteryReport; // здоровье батареи (WMI + SOH1)
    public required Func<SystemIntegration.ApiSettings> GetApiSettings;    // настройки HTTP API (api.json, XIC-13)
    public required Action ApiApplied; // вкладка изменила настройки API → сохранить + перезапустить хост/фаервол
    public required Action TrayMetricApplied; // индикатор в трее (XIC-35): вкл/выкл/метрика/период изменились
}
