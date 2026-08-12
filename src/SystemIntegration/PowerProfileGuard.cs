using Microsoft.Win32;
using XiControl.Config;
using XiControl.Wmi;

namespace XiControl.SystemIntegration;

/// <summary>
/// «Профили питания»: держит режим производительности и яркость экрана в соответствии с
/// питанием. При переходе сеть↔батарея (и на старте / выходе из сна) применяет режим из
/// AcPerfMode/BatteryPerfMode и запомненную яркость для этого состояния. Пока пользователь
/// работает — запоминает его яркость в текущий слот, чтобы восстановить в следующий раз.
/// Паттерн как у ChargeGuard/RefreshRateGuard: событие питания + дебаунс + переустановка.
///
/// Единственный подписчик BrightnessWatcher: каждое событие сначала классифицируется
/// (наша запись или человек — по меткам Brightness.Own), затем раздаётся лимиту яркости
/// (BrightnessCapGuard, XIC-29) и запоминанию. Слот хранит намерение пользователя, лимит
/// работает фильтром на выходе: превышение не запоминается вообще, восстановление клампится.
/// </summary>
public sealed class PowerProfileGuard : IDisposable
{
    private const int DebounceMs = 1500;    // события питания сыплются пачкой — гасим дребезг
    private const int SettleMs = 3000;      // после смены питания яркость меняем мы и Windows — не считаем её «пользовательской»
    private const int SaveDebounceMs = 800; // не пишем config.json на каждый тик слайдера яркости

    private readonly IMifsClient _mifs;
    private readonly AppConfig _cfg;
    private readonly IPowerEvents _power;
    private readonly BrightnessCapGuard _cap;
    private readonly AutoBrightnessGuard _auto;
    private readonly IAppTimer _debounce;
    private readonly BrightnessWatcher _brightness = new();
    private readonly System.Threading.Timer _save;
    private readonly object _lock = new();
    private volatile int _settleUntil;  // Environment.TickCount, до которого не запоминаем яркость

    /// <summary>Вызывается (на потоке пула) после применения режима — обновить значок трея.</summary>
    public Action? ModeApplied;

    public PowerProfileGuard(IMifsClient mifs, AppConfig cfg, IPowerEvents power,
        BrightnessCapGuard cap, AutoBrightnessGuard auto, IAppTimer? debounce = null)
    {
        _mifs = mifs;
        _cfg = cfg;
        _power = power;
        _cap = cap;
        _auto = auto;

        _debounce = debounce ?? new UiTimer();
        _debounce.Interval = DebounceMs;
        _debounce.Tick += () => { _debounce.Stop(); Apply(); };

        _save = new System.Threading.Timer(_ => { lock (_lock) _cfg.Save(); });

        _brightness.Changed += OnBrightnessChanged;
        _brightness.Start();

        _power.PowerModeChanged += OnPowerModeChanged;
        // блокировка/разблокировка сбрасывают паузу лимита яркости (XIC-29). Напрямую из
        // SystemEvents (как _locked в TrayApp): узкому шву IPowerEvents событие сеанса чужое,
        // а guard-у от него нужен только потокобезопасный сброс + фоновая сверка.
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    private void OnPowerModeChanged(PowerModes mode)
    {
        // Resume — выход из сна; StatusChange — смена питания AC↔батарея
        if (mode is not (PowerModes.Resume or PowerModes.StatusChange)) return;
        _cap.ResetBackoff();  // сон/смена питания — условия сменились, торг лимита заново
        _auto.ResetBackoff(); // и уступка авто-яркости тоже (кривая источника сменилась)
        // окно «затишья» ставим сразу: и переход яркости от Windows, и наше применение через
        // дебаунс не должны попасть в «пользовательскую» яркость (иначе слоты перезапишутся мусором)
        _settleUntil = Environment.TickCount + SettleMs;
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is not (SessionSwitchReason.SessionLock or SessionSwitchReason.SessionUnlock)) return;
        _cap.ResetBackoff();
        _auto.ResetBackoff(); // уступка торга авто-яркости (XIC-37) живёт до блокировки
        Task.Run(() => { _cap.Evaluate(); _auto.Evaluate(); }); // WMI — не на потоке SystemEvents; после разблокировки — к выученному
    }

    /// <summary>Применить профиль текущего питания прямо сейчас (старт / включение опции).</summary>
    public void Reapply()
    {
        _settleUntil = Environment.TickCount + SettleMs;
        Apply();
    }

    private void Apply()
    {
        // режим держат «Профили питания», яркость — самостоятельные опции «Запоминать яркость»,
        // лимит (XIC-29) и авто-яркость (XIC-30). Нечего делать — выходим, не будим пул.
        if (!_cfg.PowerProfiles && !_cfg.RememberBrightness &&
            !_cfg.BrightnessCapEnabled && !_cfg.AutoBrightness) return;
        bool online = _power.IsOnline;
        PerfMode? wantMode = _cfg.PowerProfiles ? (online ? _cfg.AcPerfMode : _cfg.BatteryPerfMode) : null;
        // при включённой авто-яркости (XIC-30) слоты не восстанавливаем — яркостью владеет кривая
        int? wantBright = _cfg.RememberBrightness && !_cfg.AutoBrightness
            ? (online ? _cfg.AcBrightness : _cfg.BatteryBrightness) : null;
        // слот, запомненный при старом (высоком) лимите, не должен пробить новый; сам слот не трогаем
        if (wantBright is int b) wantBright = _cap.ClampRestore(b, online);

        // WMI-вызовы (смена режима + яркость) — в фон, чтобы не держать UI-поток
        Task.Run(() =>
        {
            try
            {
                if (wantMode is PerfMode m)
                {
                    if (!_mifs.SetPerfMode(m)) _mifs.SetPerfMode(PerfMode.Auto); // напр. Full-speed на батарее не примут
                    ModeApplied?.Invoke();
                }
            }
            catch (Exception ex) { Log.Ex("PowerProfileGuard.Apply.mode", ex); /* железо могло быть недоступно */ }

            if (wantBright is int lvl) Brightness.Apply(lvl);
            _cap.Evaluate();  // после смены питания яркость могла остаться выше лимита нового источника
            _auto.Evaluate(); // и у авто-яркости сменилась кривая (сеть↔батарея) — пересчитаться
        });
    }

    // событие яркости: классифицировать (наша запись/человек) и раздать лимиту и запоминанию
    private void OnBrightnessChanged(int level)
    {
        bool own = Brightness.Own.IsOwn(level);
        bool settling = Environment.TickCount - _settleUntil < 0;
        _cap.OnBrightness(level, own, settling);  // лимиту — все события: свои шаги он не считает протестом
        _auto.OnBrightness(level, own, settling); // авто-яркости — тоже: правка человека учит кривую

        // дальше — запоминание пользовательского выбора в слот текущего питания
        if (own) return;                      // восстановление слота / шаг лимита — не выбор человека
        if (!_cfg.RememberBrightness) return; // яркость независима от «Профилей питания»
        if (_cfg.AutoBrightness) return;      // кривая заменяет слоты — не пишем в них мусор
        if (settling) return;                 // ещё «затишье» после смены питания
        if (!_cap.AllowsRemember(level)) return; // выше лимита: намерение не запоминаем (и не обрезаем)

        bool online = _power.IsOnline;
        lock (_lock)
        {
            if (online) { if (_cfg.AcBrightness == level) return; _cfg.AcBrightness = level; }
            else        { if (_cfg.BatteryBrightness == level) return; _cfg.BatteryBrightness = level; }
        }
        _save.Change(SaveDebounceMs, Timeout.Infinite); // отложенная запись — бережём SSD при перетаскивании слайдера
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _power.PowerModeChanged -= OnPowerModeChanged;
        _debounce.Dispose();
        _brightness.Dispose();
        _save.Dispose();
        // _cap диспоузит DI-провайдер (инжектированное не трогаем)
    }
}
