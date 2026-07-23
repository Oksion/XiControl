using XiControl.Config;
using XiControl.Localization;
using XiControl.SystemIntegration;
using XiControl.Wmi;

namespace XiControl.Ui;

/// <summary>
/// Командный слой приложения: все Set*/Toggle*-операции (заряд, «в дорогу», режимы,
/// стратегии старта, профили, герцовка, сова, автозапуск, язык, тачпад/экран).
/// Ядро сообщает результат именованными колбэками — UI (TrayApp) решает, что показать
/// (OSD/панель/значок). Меню, панель, роутер и окно настроек зовут одни и те же методы.
/// </summary>
public sealed class AppController
{
    private readonly IMifsClient _mifs;
    private readonly AppConfig _cfg;
    private readonly IPowerEvents _power;
    private readonly ILocalizer _loc;
    private readonly ChargeGuard _charge;
    private readonly RefreshRateGuard _hz;
    private readonly PowerProfileGuard _profiles;
    private readonly TravelChargeMonitor _travel;
    private readonly TouchpadControl _touchpad;
    private readonly TouchscreenControl _touchscreen;

    private PerfMode[] _modes = [];
    private bool _autoStart;   // кэш состояния автозапуска (не дёргаем schtasks на каждое меню)

    // Все режимы по нарастанию мощности — этот же порядок задаёт цикл Mi-кнопки
    // и список в комбо профилей питания (вкладка «Производительность»).
    internal static readonly PerfMode[] AllModes =
        [PerfMode.Eco, PerfMode.Quiet, PerfMode.Auto, PerfMode.Turbo, PerfMode.FullSpeed];

    // --- уведомления для UI: ядро сообщает «что случилось», не «что показать» ---
    public Action<bool>? CareChanged;          // защита заряда переключена пользователем
    public Action<bool>? TravelChanged;        // «в дорогу» вкл/выкл пользователем
    public Action? TravelCancelled;            // тихий сброс «в дорогу» (отключили зарядник)
    public Action<PerfMode>? ModeSet;          // явный выбор режима (меню/настройки)
    public Action<PerfMode>? ModeCycled;       // переключение по кольцу (Mi-кнопка/клавиша)
    public Action? ProfileModeApplied;         // режим применён из-за смены профиля питания
    public Action? ModesReloaded;              // набор видимых режимов изменился
    public Action<bool>? AutoHzChanged;        // авто-герцовка вкл/выкл
    public Action? OwlFeatureChanged;          // фича «сова» показана/скрыта
    public Action? AwakeChanged;               // сам режим совы переключён
    public Action? LanguageChanged;            // язык интерфейса сменился
    public Action? FlyoutThemeChanged;         // тема панелей/OSD сменилась — перерисовать видимые
    public Action<bool>? TouchpadToggled;      // тачпад вкл/выкл (колбэк с фонового потока!)
    public Action<bool>? TouchscreenToggled;   // сенсорный экран вкл/выкл (тоже фон)
    public Action? FirmwareFailed;             // команда прошивке не прошла — UI показывает честную ошибку

    public AppController(IMifsClient mifs, AppConfig cfg, IPowerEvents power, ILocalizer loc,
        ChargeGuard charge, RefreshRateGuard hz, PowerProfileGuard profiles,
        TravelChargeMonitor travel, TouchpadControl touchpad, TouchscreenControl touchscreen)
    {
        _mifs = mifs;
        _cfg = cfg;
        _power = power;
        _loc = loc;
        _charge = charge;
        _hz = hz;
        _profiles = profiles;
        _travel = travel;
        _touchpad = touchpad;
        _touchscreen = touchscreen;
        ApplyModeVisibility();
    }

    /// <summary>Видимые режимы по нарастанию мощности (Эко/Полная скрываются настройками).</summary>
    public IReadOnlyList<PerfMode> VisibleModes => _modes;

    /// <summary>Автозапуск включён (кэш; реальное состояние уточняется в фоне на старте).</summary>
    public bool AutoStartEnabled => _autoStart;

    /// <summary>
    /// Стартовая бизнес-логика (зовёт TrayApp.Start): кэш автозапуска, ре-арм guard-ов,
    /// возобновление «в дорогу», режим при старте, восстановление совы, страховка тачпада/экрана.
    /// </summary>
    public void Startup()
    {
        // пока показываем состояние из конфига; реальное уточняем в фоне —
        // schtasks /query может блокировать до 10 с, старту это ни к чему
        _autoStart = _cfg.AutoStart;
        Task.Run(() =>
        {
            _autoStart = Safe(AutoStart.IsEnabled, _cfg.AutoStart);
            // самопочинка: после обновления/переноса exe задача указывает на пропавший путь
            // и молча не стартует — пересоздаём на текущий exe
            if (_autoStart) Safe(() => { AutoStart.RepairIfBroken(); return true; }, true);
        });

        // Страж заряда и авто-герцовка: применить желаемое состояние на старте
        _charge.Reapply();
        _hz.Reapply();

        // «В дорогу»: следим за достижением 100%. Если стартовали посреди режима — на зарядке
        // продолжаем ждать, иначе (уже отключены) сбрасываем: режим живёт только на зарядке.
        if (_cfg.TravelMode)
        {
            if (_power.IsOnline) _travel.Rearm();
            else { _cfg.TravelMode = false; _cfg.Save(); }
        }

        // Режим при старте (прошивка сбрасывает его на ребуте):
        //  • PowerProfiles → применить профиль текущего питания (режим + яркость);
        //  • иначе RestoreMode → восстановить последний выбранный (если он ещё видим), иначе Auto;
        //  • иначе, если задан ForceStartMode (только правкой конфига) → принудительно его.
        if (_cfg.PowerProfiles)
        {
            _profiles.Reapply();
        }
        else if (_cfg.RestoreMode)
        {
            if (_cfg.StartPerfMode is PerfMode saved)
                ApplyStartMode(_modes.Contains(saved) ? saved : PerfMode.Auto);
        }
        else if (_cfg.ForceStartMode is PerfMode forced)
        {
            ApplyStartMode(forced);
        }

        // «Запоминать яркость» — самостоятельная опция (без профилей): применить яркость
        // текущего питания на старте (при профилях это уже сделал _profiles.Reapply выше).
        if (_cfg.RememberBrightness && !_cfg.PowerProfiles) _profiles.Reapply();

        // «Режим совы»: восстановить после сбоя, включить заново, либо погасить, если фичу отключили
        if (_cfg.Awake && !_cfg.OwlMode) { AwakeMode.Disable(_cfg); _cfg.Awake = false; _cfg.Save(); }
        else if (_cfg.Awake) { AwakeMode.Enable(_cfg); _cfg.Save(); }
        else if (_cfg.AwakeSavedLidAc is not null) { AwakeMode.Disable(_cfg); _cfg.Save(); }

        // страховка «не залипает»: если тачпад/экран пришлось отключить персистентно,
        // после перезагрузки включаем их сами (в фоне — PnP-вызовы небыстрые)
        if (_cfg.TouchpadPersistOff)
            Task.Run(() => Safe(() => { _touchpad.RestoreAfterBoot(); return true; }, false));
        if (_cfg.TouchscreenPersistOff)
            Task.Run(() => Safe(() => { _touchscreen.RestoreAfterBoot(); return true; }, false));
    }

    /// <summary>Завершение работы: вернуть действие крышки; флаг Awake в конфиге не трогаем —
    /// при следующем запуске режим включится снова.</summary>
    public void Shutdown()
    {
        if (_cfg.Awake) { AwakeMode.Disable(_cfg); _cfg.Save(); }
    }

    // ---- Заряд и «в дорогу» ----

    /// <summary>Переключить лимит заряда на противоположный (текущий читается из прошивки).</summary>
    public void ToggleCharge()
        => ToggleCare(!Safe(() => _mifs.GetChargeCare(), _cfg.ChargeCare));

    /// <summary>Установить «беречь батарею». Ручная смена отменяет временный режим «В дорогу».
    /// Прошивка не приняла → конфиг не трогаем (реальное состояние не изменилось) и честно
    /// сообщаем об ошибке вместо оптимистичного «успеха» (Фаза 6.2).</summary>
    public void ToggleCare(bool on)
    {
        if (!Safe(() => { _mifs.SetChargeCare(on); return true; }, false)) { FirmwareFailed?.Invoke(); return; }
        if (_cfg.TravelMode) { _cfg.TravelMode = false; _travel.Rearm(); }
        _cfg.ChargeCare = on;
        _cfg.Save();
        CareChanged?.Invoke(on);
    }

    /// <summary>«В дорогу»: временный заряд до 100% поверх «беречь 80%».
    /// Доступно только при базовом ChargeCare=true (при постоянном 100% смысла нет).</summary>
    public void SetTravel(bool on)
    {
        if (on && !_cfg.ChargeCare) return;
        // on → снять защиту (заряд до 100); off → вернуть базовый режим заряда.
        // Сначала прошивка: не приняла → состояние не изменилось, конфиг не трогаем (6.2)
        if (!Safe(() => { _mifs.SetChargeCare(on ? false : _cfg.ChargeCare); return true; }, false))
        { FirmwareFailed?.Invoke(); return; }
        _cfg.TravelMode = on;
        _cfg.Save();
        _travel.Rearm();
        TravelChanged?.Invoke(on);
    }

    /// <summary>Тихий сброс «В дорогу» (отключили зарядник): ChargeGuard сам вернёт «беречь 80%».</summary>
    public void DisableTravel()
    {
        _cfg.TravelMode = false;
        _cfg.Save();
        _travel.Rearm();
        TravelCancelled?.Invoke();
    }

    // ---- Режимы производительности ----

    /// <summary>Явный выбор режима (меню/панель/настройки). Прошивка отказала (false или
    /// исключение) → не запоминаем и честно сообщаем об ошибке (Фаза 6.2).</summary>
    public void SetMode(PerfMode mode)
    {
        if (!Safe(() => _mifs.SetPerfMode(mode), false)) { FirmwareFailed?.Invoke(); return; }
        _cfg.RememberMode(mode);
        ModeSet?.Invoke(mode);
    }

    /// <summary>Переключить на следующий режим по кругу (Mi-кнопка / клавиша).</summary>
    public void CycleMode()
    {
        var cur = Safe<PerfMode?>(() => _mifs.GetPerfMode(), null) ?? PerfMode.Auto;
        int idx = Array.IndexOf(_modes, cur);
        var next = _modes[(idx < 0 ? 0 : idx + 1) % _modes.Length];
        if (!Safe(() => _mifs.SetPerfMode(next), false)) { FirmwareFailed?.Invoke(); return; }
        _cfg.RememberMode(next);
        ModeCycled?.Invoke(next);
    }

    /// <summary>Показ/скрытие Эко и Полной мощности в наборе режимов.</summary>
    public void ToggleModeVisibility(bool eco, bool full)
    {
        _cfg.EcoMode = eco;
        _cfg.FullSpeedMode = full;
        _cfg.Save();
        ApplyModeVisibility();
        ModesReloaded?.Invoke();
    }

    // Применить желаемый стартовый режим; если прошивка не приняла (напр. Full-speed на батарее) — Auto.
    private void ApplyStartMode(PerfMode mode)
    {
        if (!Safe(() => _mifs.SetPerfMode(mode), false))
            Safe(() => _mifs.SetPerfMode(PerfMode.Auto), false);
    }

    private void ApplyModeVisibility() => _modes = AllModes.Where(m =>
        (_cfg.EcoMode || m != PerfMode.Eco) &&
        (_cfg.FullSpeedMode || m != PerfMode.FullSpeed)).ToArray();

    // ---- Стратегия режима при старте ----

    /// <summary>
    /// Текущая стратегия — производная от трёх взаимоисключающих флагов конфига
    /// (порядок проверки = приоритет на случай рассинхрона после ручной правки config.json).
    /// </summary>
    public StartStrategy CurrentStartStrategy =>
        _cfg.PowerProfiles ? StartStrategy.Profiles
        : _cfg.ForceStartMode is not null ? StartStrategy.Pin
        : _cfg.RestoreMode ? StartStrategy.Restore
        : StartStrategy.None;

    /// <summary>Radio в окне настроек → взаимоисключающая логика стратегий старта.</summary>
    public void SetStartStrategy(StartStrategy s)
    {
        switch (s)
        {
            case StartStrategy.None:
                _cfg.RestoreMode = false; _cfg.ForceStartMode = null; _cfg.PowerProfiles = false; _cfg.Save();
                break;
            case StartStrategy.Restore:
                SetStartRestore(true);
                break;
            case StartStrategy.Pin:
                if (_cfg.ForceStartMode is null) PinCurrentStartMode(); // закрепить текущий (Авто закрепить нельзя)
                else { _cfg.RestoreMode = false; _cfg.PowerProfiles = false; _cfg.Save(); }
                break;
            case StartStrategy.Profiles:
                SetPowerProfiles(true);
                break;
        }
    }

    // «Восстанавливать последний» (взаимоисключающе с «закрепить»). При первом включении
    // (StartPerfMode ещё пуст) запоминаем текущий режим сразу — чтобы было что восстанавливать;
    // при повторном значение не трогаем, поэтому вернётся всё как было до отключения.
    private void SetStartRestore(bool on)
    {
        _cfg.RestoreMode = on;
        if (on)
        {
            _cfg.ForceStartMode = null;   // включили восстановление — снимаем закреп
            _cfg.PowerProfiles = false;   // …и профили питания (три стратегии взаимоисключающи)
            if (_cfg.StartPerfMode is null)
                _cfg.StartPerfMode = Safe<PerfMode?>(() => _mifs.GetPerfMode(), null);
        }
        _cfg.Save();
    }

    // «Закрепить текущий режим» — переключатель: уже закреплён → снять (обе галки пустые);
    // не закреплён → закрепить текущий (Авто/не прочитался — закреплять нечего). Закрепление
    // взаимоисключающе гасит «восстанавливать последний».
    private void PinCurrentStartMode()
    {
        if (_cfg.ForceStartMode is not null)
        {
            _cfg.ForceStartMode = null; // снять закреп
        }
        else if (Safe<PerfMode?>(() => _mifs.GetPerfMode(), null) is PerfMode m && m != PerfMode.Auto)
        {
            _cfg.ForceStartMode = m;
            _cfg.RestoreMode = false;
            _cfg.PowerProfiles = false; // закрепили режим — профили питания выключаем
        }
        _cfg.Save();
    }

    // «Профили питания» (взаимоисключающе с «восстанавливать»/«закрепить»): включаем —
    // засеваем текущую яркость в слот текущего питания (чтоб было что вспоминать) и применяем.
    private void SetPowerProfiles(bool on)
    {
        _cfg.PowerProfiles = on;
        if (on)
        {
            _cfg.RestoreMode = false;
            _cfg.ForceStartMode = null;
            if (_cfg.RememberBrightness) SeedCurrentBrightness();
        }
        _cfg.Save();
        if (on) _profiles.Reapply();
    }

    /// <summary>Выбор режима профиля (ac=true — сеть, иначе батарея; mode=null — «не менять»).
    /// Если это профиль текущего питания — применяем сразу для мгновенной обратной связи.</summary>
    public void SetProfileMode(bool ac, PerfMode? mode)
    {
        if (ac) _cfg.AcPerfMode = mode; else _cfg.BatteryPerfMode = mode;
        _cfg.Save();
        if (mode is PerfMode m && ac == _power.IsOnline)
        {
            if (!Safe(() => _mifs.SetPerfMode(m), false))
                Safe(() => _mifs.SetPerfMode(PerfMode.Auto), false);
            ProfileModeApplied?.Invoke();
        }
    }

    // ---- Яркость ----

    /// <summary>Явная установка «запоминать яркость» (окно даёт тумблер, а не переключатель).</summary>
    public void SetRememberBrightness(bool on)
    {
        if (_cfg.RememberBrightness == on) return;
        _cfg.RememberBrightness = on;
        if (on) SeedCurrentBrightness();
        _cfg.Save();
    }

    // Запомнить текущую яркость в слот текущего питания (при включении опции — чтобы был старт).
    private void SeedCurrentBrightness()
    {
        if (Brightness.Get() is not int lvl) return;
        if (_power.IsOnline) _cfg.AcBrightness = lvl;
        else _cfg.BatteryBrightness = lvl;
    }

    // ---- Авто-герцовка ----

    /// <summary>Авто-герцовка: вкл — сразу применить частоту по текущему питанию, выкл — не трогаем.</summary>
    public void ToggleAutoHz(bool on)
    {
        _cfg.AutoRefreshRate = on;
        _cfg.Save();
        if (on) _hz.Reapply();
        AutoHzChanged?.Invoke(on);
    }

    /// <summary>Частоты из окна настроек: сохранить и, если режим включён, применить сейчас.</summary>
    public void SetRefreshRates(int ac, int batt)
    {
        _cfg.AcRefreshRate = ac;
        _cfg.BatteryRefreshRate = batt;
        _cfg.Save();
        if (_cfg.AutoRefreshRate) _hz.Reapply();
    }

    // ---- «Сова», автозапуск, язык ----

    /// <summary>Показ/скрытие «режима совы» как фичи; при скрытии активный режим гасится.</summary>
    public void ToggleOwlFeature(bool on)
    {
        _cfg.OwlMode = on;
        if (!on && _cfg.Awake) { AwakeMode.Disable(_cfg); _cfg.Awake = false; }
        _cfg.Save();
        OwlFeatureChanged?.Invoke(); // перестроить раскладку панели (сова появляется/уходит)
    }

    /// <summary>«Режим совы»: включить/выключить «не спать».</summary>
    public void ToggleAwake()
    {
        if (_cfg.Awake) { AwakeMode.Disable(_cfg); _cfg.Awake = false; }
        else if (AwakeMode.Enable(_cfg)) { _cfg.Awake = true; }
        _cfg.Save();
        AwakeChanged?.Invoke();
    }

    /// <summary>Автозапуск. schtasks может блокировать до 10 с (WaitForExit) — не с UI-потока.</summary>
    public void ToggleAutoStart(bool on)
    {
        Task.Run(() =>
        {
            Safe(() => { AutoStart.Set(on); return true; }, false);
            _autoStart = Safe(AutoStart.IsEnabled, on);  // перечитать реальное состояние
            _cfg.AutoStart = _autoStart;
            _cfg.Save();
        });
    }

    /// <summary>Доступные языки (культура + родное название) — для комбо настроек.</summary>
    public IReadOnlyList<LangInfo> Languages => _loc.Available;

    /// <summary>Текущий язык интерфейса (культурный код).</summary>
    public string CurrentLanguage => _loc.Current;

    /// <summary>Тема флайаутов (панель/OSD/«Монитор»): null — тёмная, "light", "system".
    /// Применяется сразу; TrayApp перерисует видимые окна по колбэку.</summary>
    public void SetFlyoutTheme(string? theme)
    {
        _cfg.FlyoutTheme = theme;
        _cfg.Save();
        FlyoutPalette.Apply(theme);
        FlyoutThemeChanged?.Invoke();
    }

    /// <summary>Смена языка (культурный код): применяется сразу; UI сам пересоберёт свои подписи.</summary>
    public void SetLanguage(string culture)
    {
        _loc.Current = culture;        // Loc нормализует неизвестную культуру к базовой
        _cfg.Language = _loc.Current;
        _cfg.Save();
        LanguageChanged?.Invoke();
    }

    // ---- Тачпад / сенсорный экран ----

    /// <summary>Тачпад вкл/выкл: CM-вызовы небыстрые (сотни мс) — в фоне; колбэк придёт с фона.</summary>
    public void ToggleTouchpad() => Task.Run(() =>
    {
        bool? on = Safe<bool?>(() => _touchpad.Toggle(), null);
        if (on is bool b) TouchpadToggled?.Invoke(b);
    });

    /// <summary>Сенсорный экран вкл/выкл — то же самое, но для дигитайзера экрана.</summary>
    public void ToggleTouchscreen() => Task.Run(() =>
    {
        bool? on = Safe<bool?>(() => _touchscreen.Toggle(), null);
        if (on is bool b) TouchscreenToggled?.Invoke(b);
    });

    private static T Safe<T>(Func<T> f, T fallback,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        try { return f(); }
        catch (Exception ex) { Log.Ex($"AppController.{caller}", ex); return fallback; }
    }
}
