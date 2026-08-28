using Microsoft.Win32;
using XiControl.Config;

namespace XiControl.SystemIntegration;

/// <summary>
/// Авто-герцовка: держит частоту экрана в соответствии с питанием
/// (сеть → AcRefreshRate, батарея → BatteryRefreshRate), пока опция включена.
/// Срабатывает на смену питания и выход из сна; события идут пачкой — дебаунс. После
/// выхода из сна один раз проверяет питание через 15 секунд. Постоянный опрос включается
/// только если эта проверка обнаружила переход AC↔батарея без события StatusChange.
///
/// Дополнительно, если включён <see cref="AppConfig.HoldRefreshRate"/> — на смену режима экрана:
/// частоту мог сменить кто угодно (пользователь в параметрах Windows, чужая утилита, драйвер
/// после сброса), и без этого настройка тихо не держалась бы до следующего события питания.
/// В норме опроса нет: watchdog остаётся одноразовой проверкой после resume.
/// </summary>
public sealed class RefreshRateGuard : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly IPowerEvents _power;
    private readonly IDisplayEvents _display;
    private readonly IAppTimer _debounce;
    private readonly IAppTimer _watchdog;
    private readonly Action _apply;
    private bool _lastOnline;
    private bool _persistentWatchdog;

    public RefreshRateGuard(AppConfig cfg, IPowerEvents power, IDisplayEvents display,
        IAppTimer? debounce = null)
        : this(cfg, power, display, debounce, null, null) { }

    internal RefreshRateGuard(AppConfig cfg, IPowerEvents power, IDisplayEvents display,
        IAppTimer? debounce, IAppTimer? watchdog, Action? apply)
    {
        _cfg = cfg;
        _power = power;
        _display = display;
        _apply = apply ?? (() => RefreshRate.ApplyForPower(_cfg));
        _lastOnline = _power.IsOnline;

        _debounce = debounce ?? new UiTimer();
        _debounce.Interval = 1500;
        _debounce.Tick += OnDebounce;

        _watchdog = watchdog ?? new UiTimer();
        _watchdog.Interval = 15_000;
        _watchdog.Tick += VerifyPower;

        _power.PowerModeChanged += OnPowerModeChanged;
        _display.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnPowerModeChanged(PowerModes mode)
    {
        if (mode == PowerModes.StatusChange)
        {
            _lastOnline = _power.IsOnline;
            Arm();
            // Событие пришло штатно — одноразовая post-resume проверка уже не нужна.
            if (!_persistentWatchdog) _watchdog.Stop();
        }
        else if (mode == PowerModes.Resume)
        {
            Arm();
            if (!_persistentWatchdog)
            {
                // Не обновляем _lastOnline: watchdog должен заметить смену питания во сне,
                // если Windows не прислала StatusChange после пробуждения.
                _watchdog.Stop();
                _watchdog.Start();
            }
        }
    }

    private void VerifyPower()
    {
        bool online = _power.IsOnline;
        if (online != _lastOnline)
        {
            _lastOnline = online;
            Arm();
            // Обнаружили реальный пропуск StatusChange: оставляем 15-секундный timer
            // работать постоянно для этой сессии.
            _persistentWatchdog = true;
        }
        else if (!_persistentWatchdog)
        {
            // Обычный случай: одноразовая проверка после resume ничего не нашла.
            _watchdog.Stop();
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
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnDebounce()
    {
        _debounce.Stop();
        Reapply();
    }

    /// <summary>Применить частоту по текущему питанию прямо сейчас (старт/включение опции).</summary>
    public void Reapply() => _apply();

    public void Dispose()
    {
        _power.PowerModeChanged -= OnPowerModeChanged;
        _display.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _debounce.Tick -= OnDebounce;
        _watchdog.Tick -= VerifyPower;
        _debounce.Stop();
        _watchdog.Stop();
        _debounce.Dispose();
        _watchdog.Dispose();
    }
}
