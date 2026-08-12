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
    private readonly BrightnessCapGuard _capGuard;
    private readonly AutoBrightnessGuard _autoGuard;
    private readonly AlsWatcher _als;
    private readonly TravelChargeMonitor _travel;
    private readonly TouchpadControl _touchpad;
    private readonly TouchscreenControl _touchscreen;
    private readonly TouchpadDeadZone _deadZone;

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
    public Action? RefreshRateFeatureChanged;  // фича «управление частотой» показана/скрыта
    public Action? OwlFeatureChanged;          // фича «сова» показана/скрыта
    public Action? AwakeChanged;               // сам режим совы переключён
    public Action? LanguageChanged;            // язык интерфейса сменился
    public Action? FlyoutThemeChanged;         // тема панелей/OSD сменилась — перерисовать видимые
    public Action<bool>? TouchpadToggled;      // тачпад вкл/выкл (колбэк с фонового потока!)
    public Action<bool>? TouchscreenToggled;   // сенсорный экран вкл/выкл (тоже фон)
    public Action? FirmwareFailed;             // команда прошивке не прошла — UI показывает честную ошибку

    public AppController(IMifsClient mifs, AppConfig cfg, IPowerEvents power, ILocalizer loc,
        ChargeGuard charge, RefreshRateGuard hz, PowerProfileGuard profiles, BrightnessCapGuard capGuard,
        AutoBrightnessGuard autoGuard, AlsWatcher als,
        TravelChargeMonitor travel, TouchpadControl touchpad, TouchscreenControl touchscreen,
        TouchpadDeadZone deadZone)
    {
        _mifs = mifs;
        _cfg = cfg;
        _power = power;
        _loc = loc;
        _charge = charge;
        _hz = hz;
        _profiles = profiles;
        _capGuard = capGuard;
        _autoGuard = autoGuard;
        _als = als;
        _als.LuxChanged += _autoGuard.OnLux; // датчик → авто-яркость (оба живут в DI весь сеанс)
        _travel = travel;
        _touchpad = touchpad;
        _touchscreen = touchscreen;
        _deadZone = deadZone;
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

        // Лимит яркости (XIC-29): превышение на старте сводится тем же вежливым механизмом.
        // Reapply выше уже сверяется сам; отдельная сверка нужна, когда включён только лимит.
        if (_cfg.BrightnessCapEnabled && !_cfg.RememberBrightness && !_cfg.PowerProfiles)
            Task.Run(_capGuard.Evaluate);

        // Авто-яркость (XIC-30): датчик стартуем всегда (нужен вкладке «Экран», чтобы знать,
        // показывать ли фичу); первые люксы придут событием и сами дадут сверку через дебаунс.
        // Кривые сеем и здесь: фичу могли включить правкой config.json мимо SetAutoBrightness.
        if (_cfg.AutoBrightness) SeedCurves();
        _als.Start();

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

    /// <summary>Переключить лимит заряда «беречь X% ↔ 100%» (текущий читается из прошивки).</summary>
    public void ToggleCharge()
    {
        int cur = Safe(() => _mifs.GetChargeLimit(), _cfg.ChargeCare ? _cfg.CarePercent() : 100) ?? 100;
        ToggleCare(cur >= 100);   // сейчас 100 → включить «беречь X%»; иначе → 100
    }

    /// <summary>
    /// Принять порог заряда из прошивки: внешнюю смену (Xiaomi PC Manager, чужая утилита) мы иначе
    /// не заметим — канала уведомления в MIFS нет. Зовётся в момент показа панели и пересборки меню
    /// трея, поэтому фонового опроса не появляется.
    /// <para>Принимаем ТОЛЬКО валидный порог &lt; 100: другой уровень EC сам не породит — это всегда
    /// чей-то осознанный SET. А вот «100%» неотличим от транзиента: EC теряет лимит после сна/смены
    /// питания, и до ре-арма ChargeGuard (дебаунс 1.5 с) читается как 100. Принять его = сбросить
    /// ChargeCare в конфиге и разоружить гард навсегда (панель, открытая сразу после resume/выдёргивания
    /// зарядника, молча убивала бы защиту). Внешнее «выключить» гард всё равно перебивает на следующем
    /// событии питания — это документированное поведение, поэтому «100» честно игнорируем.</para>
    /// <para><see cref="CareChanged"/> здесь намеренно НЕ дёргаем: это не действие пользователя, а
    /// подхват чужого. При скрытой панели TrayApp показывает на это событие OSD — то есть всплывашка
    /// вылетала бы на каждое открытие панели. Панель и меню и так рисуются сразу после вызова.</para>
    /// </summary>
    /// <returns><c>true</c> — значение приняли, конфиг изменился.</returns>
    public bool SyncCareFromFirmware()
    {
        // «В дорогу» намеренно держит 100% при включённой защите — это наше состояние, не внешнее
        if (_cfg.TravelMode) return false;
        if (Safe<int?>(() => _mifs.GetChargeLimit(), null) is not int live) return false;
        if (live >= 100 || Mifs.ChargeCodeForPercent(live) is null) return false;

        bool changed = false;
        if (!_cfg.ChargeCare) { _cfg.ChargeCare = true; changed = true; }
        if (live != _cfg.CareLimitPercent) { _cfg.CareLimitPercent = live; changed = true; }
        if (!changed) return false;

        Log.Write($"Заряд: принят порог прошивки — беречь {live}%");
        _cfg.Save();
        return true;
    }

    /// <summary>Установить «беречь батарею» (порог X% из настроек) либо 100%. Ручная смена отменяет
    /// «В дорогу». Прошивка не приняла → конфиг не трогаем (реальное состояние не изменилось) и честно
    /// сообщаем об ошибке вместо оптимистичного «успеха» (Фаза 6.2).</summary>
    public void ToggleCare(bool on)
    {
        int percent = on ? _cfg.CarePercent() : 100;
        if (!Safe(() => _mifs.SetChargeLimit(percent), false)) { FirmwareFailed?.Invoke(); return; }
        if (_cfg.TravelMode) { _cfg.TravelMode = false; _travel.Rearm(); }
        _cfg.ChargeCare = on;
        _cfg.Save();
        CareChanged?.Invoke(on);
    }

    /// <summary>Сменить порог «беречь батарею» (%). На железе применяем сразу, только если защита
    /// сейчас активна и не перебита «В дорогу»; иначе порог просто запоминается до включения.
    /// Прошивка отвергла уровень (модель его не держит) → откатываем выбор и честно сообщаем.</summary>
    public void SetCareLimit(int percent)
    {
        if (Mifs.ChargeCodeForPercent(percent) is null) return;   // не пишем вслепую неизвестный уровень
        if (_cfg.ChargeCare && !_cfg.TravelMode
            && !Safe(() => _mifs.SetChargeLimit(percent), false)) { FirmwareFailed?.Invoke(); return; }
        _cfg.CareLimitPercent = percent;
        _cfg.Save();
        CareChanged?.Invoke(_cfg.ChargeCare);   // обновить подписи (панель/меню/значок)
    }

    /// <summary>«В дорогу»: временный заряд до 100% поверх «беречь X%».
    /// Доступно только при базовом ChargeCare=true (при постоянном 100% смысла нет).</summary>
    public void SetTravel(bool on)
    {
        if (on && !_cfg.ChargeCare) return;
        // on → снять защиту (заряд до 100); off → вернуть базовый порог X%.
        // Сначала прошивка: не приняла → состояние не изменилось, конфиг не трогаем (6.2)
        if (!Safe(() => _mifs.SetChargeLimit(on ? 100 : _cfg.CarePercent()), false))
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

    /// <summary>Лимит яркости вкл/выкл (XIC-29). Включили — текущее превышение сводится тем же
    /// вежливым механизмом схождения; выключили — guard сам останавливает всё, включая паузу.</summary>
    public void SetBrightnessCap(bool on)
    {
        if (_cfg.BrightnessCapEnabled == on) return;
        _cfg.BrightnessCapEnabled = on;
        _cfg.Save();
        _capGuard.ResetBackoff();
        Task.Run(_capGuard.Evaluate); // сверка читает WMI — не с UI-потока
    }

    /// <summary>Авто-яркость по датчику (XIC-30). Включение сеет дефолтную кривую и гасит
    /// «Запоминать яркость» (кривая заменяет слоты); выключение ничего не трогает —
    /// кривая остаётся в конфиге до следующего раза.</summary>
    public void SetAutoBrightness(bool on)
    {
        if (_cfg.AutoBrightness == on) return;
        _cfg.AutoBrightness = on;
        if (on)
        {
            SeedCurves();
            _cfg.RememberBrightness = false; // взаимоисключение: два хозяина яркости не нужны
        }
        _cfg.Save();
        if (on) Task.Run(_autoGuard.Evaluate); // сверка может читать WMI — не с UI-потока
    }

    /// <summary>«Обучение кривой» (XIC-37): выкл — правки яркости временные, кривая заморожена;
    /// дальше действует «возврат к выученному» (SetAutoBrightnessRevert).</summary>
    public void SetAutoBrightnessLearning(bool on)
    {
        if (_cfg.AutoBrightnessLearning == on) return;
        _cfg.AutoBrightnessLearning = on;
        _cfg.Save();
        _autoGuard.LearningModeChanged(); // недоигранное схождение/уступка/серия обучения — в мусор
    }

    /// <summary>Режим возврата к выученному (XIC-37): null — всегда, "battery" — только на
    /// батарее, "off" — не возвращать (правка живёт до смены света).</summary>
    public void SetAutoBrightnessRevert(string? mode)
    {
        _cfg.AutoBrightnessRevert = mode;
        _cfg.Save();
        _autoGuard.LearningModeChanged(); // сменили правила на ходу — текущий эпизод неактуален
    }

    // Пустые кривые (первое включение / ручная правка конфига) заполняем дефолтом — обеим:
    // кривых две, для сети и батареи (комфорт в одних люксах у розетки и в дороге разный).
    private void SeedCurves()
    {
        if (_cfg.AutoBrightnessPointsAc.Count == 0)
            _cfg.AutoBrightnessPointsAc.AddRange(BrightnessCurve.DefaultPoints());
        if (_cfg.AutoBrightnessPointsBattery.Count == 0)
            _cfg.AutoBrightnessPointsBattery.AddRange(BrightnessCurve.DefaultPoints());
    }

    /// <summary>Есть ли датчик освещённости (для видимости фичи на вкладке «Экран»).</summary>
    public bool AlsAvailable => _als.Available;

    /// <summary>Текущая освещённость, лк (NaN — событий ещё не было) — живой индикатор в настройках.</summary>
    public float CurrentLux => _als.LastLux;

    /// <summary>«Инерция» датчика: окно медианы, сек (0 — мгновенные значения). Новые сэмплы
    /// подхватят окно сами — guard дёргать не нужно.</summary>
    public void SetBrightnessMedianSec(int seconds)
    {
        _cfg.AutoBrightnessMedianSec = Math.Clamp(seconds, 0, 600);
        _cfg.Save();
    }

    /// <summary>Сброс кривой обучения — только по явной кнопке (выкл/вкл фичи кривую не трогает).</summary>
    public void ResetBrightnessCurve() => _autoGuard.ResetCurve();

    /// <summary>Снимок кривой (сеть/батарея) для отрисовки графика на вкладке «Экран».</summary>
    public Config.BrightnessPoint[] BrightnessCurvePoints(bool online) => _autoGuard.CurveSnapshot(online);

    /// <summary>Лимиты яркости из окна настроек (сеть, батарея) — сохранить и свериться.</summary>
    public void SetBrightnessCaps(int ac, int batt)
    {
        _cfg.BrightnessCapAc = ac;
        _cfg.BrightnessCapBattery = batt;
        _cfg.Save();
        if (!_cfg.BrightnessCapEnabled) return;
        _capGuard.ResetBackoff(); // лимит сменили осознанно — старая пауза больше не про эти условия
        Task.Run(_capGuard.Evaluate);
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

    /// <summary>
    /// Показ/скрытие «управления частотой» как фичи (меню/панель/вкладка «Экран»).
    /// Выключаем — активная авто-герцовка гасится (как <see cref="ToggleOwlFeature"/> гасит
    /// активный Awake): сначала возвращаем сеть-частоту, если «батарейная» успела примениться
    /// (иначе пользователь остался бы на 60 Гц без UI, чтобы это поправить), затем снимаем
    /// флаг — «взведённый» AutoRefreshRate без единой видимой поверхности врал бы читателям
    /// (OSD питания рисовал «• N Гц» при выключенной фиче), а повторное включение фичи
    /// молча возобновляло бы переключения.
    /// </summary>
    public void ToggleRefreshRateFeature(bool on)
    {
        _cfg.RefreshRateFeature = on;
        if (!on && _cfg.AutoRefreshRate)
        {
            int ac = _cfg.AcRefreshRate; // снять возможный батарейный троттлинг, не блокируя UI-поток
            Task.Run(() => Safe(() => RefreshRate.Apply(ac), false));
            _cfg.AutoRefreshRate = false;
        }
        else if (on && _cfg.AutoRefreshRate)
        {
            _hz.Reapply(); // флаг взведён только ручной правкой config.json — уважаем и применяем
        }
        _cfg.Save();
        RefreshRateFeatureChanged?.Invoke(); // перестроить панель/меню (ячейка герцовки уходит/появляется)
    }

    /// <summary>
    /// «Удерживать частоту»: возвращать заданную, если режим экрана сменили извне.
    /// Включение сразу подтягивает текущее состояние — экран мог уже уехать до того,
    /// как пользователь дошёл до тумблера.
    /// </summary>
    public void SetHoldRefreshRate(bool on)
    {
        _cfg.HoldRefreshRate = on;
        _cfg.Save();
        if (on) _hz.Reapply();
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

    // ---- Проверка обновлений (XIC-20) ----

    /// <summary>Последний найденный релиз (не обязательно новее нас — см. <see cref="LastUpdateCheck"/>).
    /// Держим на всю сессию: окно настроек пересобирается на каждый показ (и на смену темы/DPI/языка),
    /// запрос оттуда улетал бы по нескольку раз.</summary>
    public ReleaseInfo? Update { get; private set; }

    /// <summary>Чем закончилась последняя проверка — «О программе» отвечает пользователю,
    /// нажавшему «Проверить обновления», а не молчит.</summary>
    public UpdateStatus LastUpdateCheck { get; private set; } = UpdateStatus.NotChecked;

    /// <summary>Новая версия найдена — TrayApp решает, показывать ли тост.</summary>
    public Action<ReleaseInfo>? UpdateFound;

    /// <summary>
    /// Проверить выход новой версии. Тумблер выключен — не ходим в сеть вообще (он и есть
    /// выключатель трафика). Обычная проверка — не чаще раза в сутки; force (кнопка «Проверить
    /// сейчас») это окно игнорирует, потому что это явное действие пользователя.
    /// </summary>
    public async Task CheckUpdatesAsync(bool force)
    {
        if (!force && !UpdateCheck.DueForCheck(_cfg.CheckUpdates, _cfg.LastUpdateCheckUtc, DateTime.UtcNow)) return;
        if (force && !_cfg.CheckUpdates) return; // выключено — молчим даже по кнопке

        _cfg.LastUpdateCheckUtc = DateTime.UtcNow;
        _cfg.Save();

        var release = await UpdateCheck.FetchLatestAsync().ConfigureAwait(false);
        if (release is null)
        {
            LastUpdateCheck = UpdateStatus.Failed; // нет сети/таймаут/лимит — так и скажем
            return;
        }

        var current = UpdateCheck.CurrentVersion();
        // дев-сборку не называем «последней версией»: релиз на GitHub заведомо свежее её 0.0.0
        LastUpdateCheck = UpdateCheck.IsDevBuild(current) ? UpdateStatus.DevBuild
            : UpdateCheck.IsNewer(release.Version, current) ? UpdateStatus.Available
            : UpdateStatus.UpToDate;
        // отметку на «О программе» держим, только если релиз реально новее: иначе на свежей
        // установке вкладка писала бы «доступна X», когда X и так стоит
        Update = release; // что именно показать — решает статус выше, а не сам факт находки

        // тост — раз на версию; отметка на «О программе» при этом остаётся
        if (UpdateCheck.ShouldNotify(release.Version, current, _cfg.SkippedVersion))
            UpdateFound?.Invoke(release);
    }

    /// <summary>Тумблер «Проверять обновления». Включили — сразу проверяем, иначе пришлось бы
    /// ждать следующего запуска.</summary>
    public void SetCheckUpdates(bool on)
    {
        _cfg.CheckUpdates = on;
        _cfg.Save();
        if (on) _ = Task.Run(() => CheckUpdatesAsync(force: true));
    }

    /// <summary>
    /// Мёртвая зона у нижнего края тачпада вкл/выкл. Реестр правим только отсюда — то есть
    /// только по явному переключению пользователем; перезапуск узла тачпада (сотни мс, панель
    /// на секунду пропадает) — в фоне, чтобы не морозить окно настроек.
    /// </summary>
    public void SetTouchpadDeadZone(bool on)
    {
        _cfg.TouchpadDeadZone = on;
        _cfg.Save();
        Task.Run(() => Safe(_deadZone.Apply, false));
    }

    /// <summary>Высота мёртвой зоны (мм). Применяем сразу, но только если зона включена —
    /// иначе выбор высоты «про запас» молча включил бы её.</summary>
    public void SetTouchpadDeadZoneMm(int mm)
    {
        _cfg.TouchpadDeadZoneMm = mm;
        _cfg.Save();
        if (_cfg.TouchpadDeadZone) Task.Run(() => Safe(_deadZone.Apply, false));
    }

    private static T Safe<T>(Func<T> f, T fallback,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        try { return f(); }
        catch (Exception ex) { Log.Ex($"AppController.{caller}", ex); return fallback; }
    }
}
