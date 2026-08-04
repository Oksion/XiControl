using XiControl.Config;

namespace XiControl.SystemIntegration;

/// <summary>
/// Авто-яркость по датчику освещённости (XIC-30): свет изменился значимо (гистерезис в
/// лог-шкале) и устоялся (дебаунс) → яркость плавно едет к предсказанию обучаемой кривой
/// (BrightnessCurve). Ручная правка пользователя после «периода раздумья» становится
/// обучающей точкой — кривая персонализируется, и в этих условиях мы больше не спорим.
/// При включённой адаптивной яркости Windows молчим (двое не должны рулить одним ползунком),
/// лимит яркости (XIC-29) клампит выход — «кривая хранит намерение, лимит — фильтр».
///
/// Люксы сюда отдаёт AlsWatcher (монтирует AppController), события яркости — PowerProfileGuard
/// (единственный подписчик BrightnessWatcher, уже классифицировавший «наша запись/человек»).
/// Всё на потоках пула, паттерн BrightnessCapGuard: WorkerTimer, общий замок, швы для тестов.
/// </summary>
public sealed class AutoBrightnessGuard : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly IPowerEvents _power;
    private readonly IAppTimer _settle;  // свет меняется — ждём стабилизации
    private readonly IAppTimer _learn;   // «период раздумья»: пользователь докрутил и остановился
    private readonly Func<int?> _read;
    private readonly BrightnessCapGuard.RampFn _ramp;
    private readonly Func<bool, bool> _adaptive;
    private readonly Func<int, bool, int> _clamp;
    private readonly BrightnessCurve _curve;
    private readonly object _lock = new();

    private float _pendingLux = float.NaN; // последние люксы с датчика
    private float _actedLux = float.NaN;   // люксы, на которые уже среагировали (база гистерезиса)
    private int _current = -1;             // последняя известная яркость (все события)
    private bool _learning;                // серия ручных правок идёт
    private float _learnLux = float.NaN;   // свет в момент НАЧАЛА серии (правка обдумывалась при нём)
    private int _learnPercent;
    private CancellationTokenSource? _rampCts;

    public AutoBrightnessGuard(AppConfig cfg, IPowerEvents power,
        IAppTimer? settle = null, IAppTimer? learn = null,
        Func<int?>? read = null, BrightnessCapGuard.RampFn? ramp = null,
        Func<bool, bool>? adaptive = null, Func<int, bool, int>? clamp = null)
    {
        _cfg = cfg;
        _power = power;
        _settle = settle ?? new WorkerTimer();
        _learn = learn ?? new WorkerTimer();
        _read = read ?? Brightness.Get;
        _ramp = ramp ?? ((f, t, ct) => Brightness.Ramp(f, t, Math.Max(1000, cfg.BrightnessRampMs), ct));
        _adaptive = adaptive ?? AdaptiveBrightness.IsEnabled;
        _clamp = clamp ?? ((level, _) => level);
        _curve = new BrightnessCurve(cfg.AutoBrightnessPoints);

        _settle.Tick += OnSettleTick;
        _learn.Tick += OnLearnTick;
    }

    /// <summary>Свежие люксы (поток пула, из AlsWatcher). Мелкие колебания гасятся гистерезисом,
    /// значимые — взводят дебаунс стабилизации: экран не «дышит» на каждый лк.</summary>
    public void OnLux(float lux)
    {
        lock (_lock)
        {
            _pendingLux = lux;
            if (!_cfg.AutoBrightness) return;
            if (!BrightnessCurve.Significant(_actedLux, lux, Hysteresis())) return;
            _settle.Interval = Math.Max(500, _cfg.AutoBrightnessSettleMs);
            _settle.Stop();
            _settle.Start();
        }
    }

    /// <summary>Событие яркости от PowerProfileGuard (уже классифицировано). Наши записи —
    /// только трекинг; правка человека отменяет наш ход и взводит обучение.</summary>
    public void OnBrightness(int level, bool own, bool settling)
    {
        lock (_lock)
        {
            _current = level;
            if (own || settling || !_cfg.AutoBrightness) return;

            CancelRampLocked(); // человек взялся за ползунок — не тянем одеяло
            if (!_learning)
            {
                // условия фиксируем в момент НАЧАЛА серии: правка обдумывалась при этом свете
                _learning = true;
                _learnLux = _pendingLux;
            }
            _learnPercent = level;
            _learn.Interval = Math.Max(1000, _cfg.AutoBrightnessLearnMs);
            _learn.Stop();
            _learn.Start();
        }
    }

    /// <summary>
    /// Свериться сейчас: включение фичи, старт с включённой, конец обучения. Синхронно
    /// (WMI-чтение при неизвестной яркости) — звать с фонового потока.
    /// </summary>
    public void Evaluate()
    {
        float lux;
        bool online = _power.IsOnline;
        lock (_lock)
        {
            if (!_cfg.AutoBrightness || float.IsNaN(_pendingLux)) return;
            lux = _pendingLux;
        }
        if (_adaptive(online)) return; // качели с датчиком Windows не устраиваем; причина — в UI

        int current;
        lock (_lock) current = _current;
        if (current < 0)
        {
            if (_read() is not int c) return; // панель не отдаёт яркость — фича молча спит
            lock (_lock) _current = current = c;
        }

        lock (_lock)
        {
            int want = _clamp(_curve.Predict(lux), online);
            _actedLux = lux; // гистерезис отсчитываем от «на что смотрели», даже если не тронули
            if (Math.Abs(want - current) < Math.Max(1, _cfg.AutoBrightnessDeadband)) return;
            StartRampLocked(current, want);
        }
    }

    private void OnSettleTick()
    {
        _settle.Stop();
        Evaluate();
    }

    private void OnLearnTick()
    {
        float lux;
        int percent;
        lock (_lock)
        {
            _learn.Stop();
            if (!_learning) return;
            _learning = false;
            lux = _learnLux;
            percent = _learnPercent;
            if (!_cfg.AutoBrightness || float.IsNaN(lux)) return;

            _curve.Learn(lux, percent);
            // предсказание в этих условиях теперь равно выученному — не воюем с пользователем
            _actedLux = lux;
        }
        Log.Write($"AutoBrightness: выучено {percent}% при {lux:0.#} лк (точек: {_curve.Count})");
        _cfg.Save(); // раз в правку (после раздумья) — SSD не страдает
    }

    /// <summary>Принудительный сброс обучения (кнопка в настройках): выученные точки стираются,
    /// сеется кривая по умолчанию. Выключение/включение фичи кривую НЕ трогает — забыть её
    /// можно только этой явной командой.</summary>
    public void ResetCurve()
    {
        lock (_lock)
        {
            _cfg.AutoBrightnessPoints.Clear();
            _cfg.AutoBrightnessPoints.AddRange(BrightnessCurve.DefaultPoints());
            _learning = false;
            _learn.Stop();
            _actedLux = float.NaN; // следующие люксы значимы — пересчитаемся по свежей кривой
        }
        Log.Write("AutoBrightness: кривая обучения сброшена к умолчанию");
        _cfg.Save();
        if (_cfg.AutoBrightness) Task.Run(Evaluate);
    }

    /// <summary>Снимок точек кривой для отрисовки в настройках: копия под замком —
    /// обучение может идти параллельно на пуле.</summary>
    public BrightnessPoint[] CurveSnapshot()
    {
        lock (_lock)
            return [.. _cfg.AutoBrightnessPoints.Select(p => new BrightnessPoint { Lux = p.Lux, Percent = p.Percent })];
    }

    private double Hysteresis() => Math.Max(0.01, _cfg.AutoBrightnessHysteresis);

    private void StartRampLocked(int from, int to)
    {
        CancelRampLocked();
        _rampCts = new CancellationTokenSource();
        _ramp(from, to, _rampCts.Token);
    }

    private void CancelRampLocked()
    {
        _rampCts?.Cancel();
        _rampCts?.Dispose();
        _rampCts = null;
    }

    public void Dispose()
    {
        lock (_lock) CancelRampLocked();
        _settle.Dispose();
        _learn.Dispose();
    }
}
