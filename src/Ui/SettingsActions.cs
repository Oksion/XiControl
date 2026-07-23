using XiControl.Localization;
using XiControl.Wmi;

namespace XiControl.Ui;

/// <summary>Какая стратегия режима при старте выбрана (взаимоисключающие).</summary>
public enum StartStrategy { None, Restore, Pin, Profiles }

/// <summary>
/// Колбэки в TrayApp/AppController: окно настроек не дублирует логику (взаимоисключения режимов
/// старта, переармливание гардов, применение профиля) — оно меняет то, что тривиально
/// (config.json), а «умные» операции делегирует сюда.
/// </summary>
public sealed class SettingsActions
{
    public Func<bool> GetAutoStart = () => false;
    public Action<bool> SetAutoStart = _ => { };
    public Func<IReadOnlyList<LangInfo>> Languages = () => [];  // доступные языки (data-driven)
    public Func<string> CurrentLanguage = () => "";            // текущий культурный код
    public Action<string> SetLanguage = _ => { };             // сменить язык по культурному коду
    public Action<string?> SetFlyoutTheme = _ => { };         // тема панелей/OSD: null/"light"/"system"
    public Action<bool, bool> SetModeVisibility = (_, _) => { };   // eco, full
    public Func<StartStrategy> GetStartStrategy = () => StartStrategy.None;
    public Action<StartStrategy> SetStartStrategy = _ => { };
    public Action<bool, PerfMode?> SetProfileMode = (_, _) => { }; // ac, mode
    public Action<bool> SetRememberBrightness = _ => { };
    public Action<bool> SetAutoHz = _ => { };
    public Action<int, int> SetRefreshRates = (_, _) => { };       // ac, batt
    public Action<bool> SetOwlFeature = _ => { };
    public Func<SystemIntegration.BatteryReport> GetBatteryReport = () => default; // здоровье батареи (WMI + SOH1)
}
