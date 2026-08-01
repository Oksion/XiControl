using Microsoft.Win32;
using XiControl.Config;

namespace XiControl.SystemIntegration;

/// <summary>
/// Авто-герцовка: держит частоту экрана в соответствии с питанием
/// (сеть → AcRefreshRate, батарея → BatteryRefreshRate), пока опция включена.
/// Срабатывает на смену питания и выход из сна; события идут пачкой — дебаунс.
///
/// Дополнительно, если включён <see cref="AppConfig.HoldRefreshRate"/> — на смену режима экрана:
/// частоту мог сменить кто угодно (пользователь в параметрах Windows, чужая утилита, драйвер
/// после сброса), и без этого настройка тихо не держалась бы до следующего события питания.
/// Опроса нет и не должно быть — только события.
/// </summary>
public sealed class RefreshRateGuard : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly IPowerEvents _power;
    private readonly IDisplayEvents _display;
    private readonly IAppTimer _debounce;

    public RefreshRateGuard(AppConfig cfg, IPowerEvents power, IDisplayEvents display, IAppTimer? debounce = null)
    {
        _cfg = cfg;
        _power = power;
        _display = display;

        _debounce = debounce ?? new UiTimer();
        _debounce.Interval = 1500;
        _debounce.Tick += () => { _debounce.Stop(); Reapply(); };

        _power.PowerModeChanged += OnPowerModeChanged;
        _display.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnPowerModeChanged(PowerModes mode)
    {
        // Resume — выход из сна; StatusChange — смена питания AC↔батарея
        if (mode is PowerModes.Resume or PowerModes.StatusChange) Arm();
    }

    // Режим экрана изменился. Зацикливания нет: наше собственное применение тоже поднимет это
    // событие, но RefreshRate.Apply при совпадении частоты не зовёт ChangeDisplaySettings —
    // цикл гаснет на первом витке. Не убирать ту проверку в Apply.
    private void OnDisplaySettingsChanged()
    {
        if (_cfg.HoldRefreshRate) Arm();
    }

    // Переподключение монитора шлёт события пачкой — гасим дребезг, как и с питанием
    private void Arm()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Применить частоту по текущему питанию прямо сейчас (старт/включение опции).</summary>
    public void Reapply() => RefreshRate.ApplyForPower(_cfg);

    public void Dispose()
    {
        _power.PowerModeChanged -= OnPowerModeChanged;
        _display.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _debounce.Dispose();
    }
}
