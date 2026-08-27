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
/// Для питания есть редкий резервный опрос: часть Modern Standby/ACPI-систем не присылает
/// SystemEvents.StatusChange при вытаскивании кабеля. Режимы экрана по-прежнему не опрашиваются.
/// </summary>
public sealed class RefreshRateGuard : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly IPowerEvents _power;
    private readonly IDisplayEvents _display;
    private readonly IAppTimer _debounce;
    private readonly IAppTimer _watchdog;
    private readonly Action _apply;
    private readonly object _timerSync = new();
    private volatile bool _lastOnline;
    private volatile bool _disposed;

    public RefreshRateGuard(AppConfig cfg, IPowerEvents power, IDisplayEvents display,
        IAppTimer? debounce = null, IAppTimer? watchdog = null, Action? apply = null)
    {
        _cfg = cfg;
        _power = power;
        _display = display;

        _apply = apply ?? (() => RefreshRate.ApplyForPower(_cfg));
        _debounce = debounce ?? new WorkerTimer();
        _debounce.Interval = 1500;
        _debounce.Tick += OnDebounce;

        // SystemEvents.PowerModeChanged is not guaranteed on every Modern Standby/ACPI stack.
        // GetSystemPowerStatus is virtually free, so a slow fallback closes that gap without
        // polling display modes or touching the screen when nothing changed.
        _lastOnline = _power.IsOnline;
        _watchdog = watchdog ?? new WorkerTimer();
        _watchdog.Interval = 2000;
        _watchdog.Tick += PollPower;

        _power.PowerModeChanged += OnPowerModeChanged;
        _display.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _watchdog.Start();
    }

    private void OnPowerModeChanged(PowerModes mode)
    {
        // Resume — выход из сна; StatusChange — смена питания AC↔батарея
        if (mode is PowerModes.Resume or PowerModes.StatusChange)
        {
            _lastOnline = _power.IsOnline;
            Arm();
        }
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
        if (_disposed) return;
        lock (_timerSync)
        {
            _debounce.Stop();
            _debounce.Start();
        }
    }

    private void PollPower()
    {
        if (_disposed) return;
        bool online = _power.IsOnline;
        if (online == _lastOnline) return;
        _lastOnline = online;
        Arm();
    }

    private void OnDebounce()
    {
        if (_disposed) return;
        lock (_timerSync)
        {
            if (_disposed) return;
            _debounce.Stop();
        }
        Reapply();
    }

    /// <summary>Применить частоту по текущему питанию прямо сейчас (старт/включение опции).</summary>
    public void Reapply() => _apply();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _power.PowerModeChanged -= OnPowerModeChanged;
        _display.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _watchdog.Tick -= PollPower;
        _debounce.Tick -= OnDebounce;
        _watchdog.Stop();
        _debounce.Stop();
        _watchdog.Dispose();
        _debounce.Dispose();
    }
}
