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
/// </summary>
public sealed class PowerProfileGuard : IDisposable
{
    private const int DebounceMs = 1500;    // события питания сыплются пачкой — гасим дребезг
    private const int SettleMs = 3000;      // после смены питания яркость меняем мы и Windows — не считаем её «пользовательской»
    private const int SaveDebounceMs = 800; // не пишем config.json на каждый тик слайдера яркости

    private readonly MifsClient _mifs;
    private readonly AppConfig _cfg;
    private readonly System.Windows.Forms.Timer _debounce;
    private readonly BrightnessWatcher _brightness = new();
    private readonly System.Threading.Timer _save;
    private readonly object _lock = new();
    private volatile int _settleUntil;  // Environment.TickCount, до которого не запоминаем яркость

    /// <summary>Вызывается (на потоке пула) после применения режима — обновить значок трея.</summary>
    public Action? ModeApplied;

    public PowerProfileGuard(MifsClient mifs, AppConfig cfg)
    {
        _mifs = mifs;
        _cfg = cfg;

        _debounce = new System.Windows.Forms.Timer { Interval = DebounceMs };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Apply(); };

        _save = new System.Threading.Timer(_ => { lock (_lock) _cfg.Save(); });

        _brightness.Changed += OnBrightnessChanged;
        _brightness.Start();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // Resume — выход из сна; StatusChange — смена питания AC↔батарея
        if (e.Mode is not (PowerModes.Resume or PowerModes.StatusChange)) return;
        // окно «затишья» ставим сразу: и переход яркости от Windows, и наше применение через
        // дебаунс не должны попасть в «пользовательскую» яркость (иначе слоты перезапишутся мусором)
        _settleUntil = Environment.TickCount + SettleMs;
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Применить профиль текущего питания прямо сейчас (старт / включение опции).</summary>
    public void Reapply()
    {
        _settleUntil = Environment.TickCount + SettleMs;
        Apply();
    }

    private void Apply()
    {
        if (!_cfg.PowerProfiles) return;
        bool online = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
        PerfMode? wantMode = online ? _cfg.AcPerfMode : _cfg.BatteryPerfMode;
        int? wantBright = _cfg.RememberBrightness ? (online ? _cfg.AcBrightness : _cfg.BatteryBrightness) : null;

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
        });
    }

    // пользователь поменял яркость — запомнить её в слот текущего питания (но не в окно «затишья»)
    private void OnBrightnessChanged(int level)
    {
        if (!_cfg.PowerProfiles || !_cfg.RememberBrightness) return;
        if (Environment.TickCount - _settleUntil < 0) return; // ещё «затишье» после смены питания

        bool online = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
        lock (_lock)
        {
            if (online) { if (_cfg.AcBrightness == level) return; _cfg.AcBrightness = level; }
            else        { if (_cfg.BatteryBrightness == level) return; _cfg.BatteryBrightness = level; }
        }
        _save.Change(SaveDebounceMs, Timeout.Infinite); // отложенная запись — бережём SSD при перетаскивании слайдера
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _debounce.Dispose();
        _brightness.Dispose();
        _save.Dispose();
    }
}
