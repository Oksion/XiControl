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
    private readonly BrightnessCurve _curveAc;    // кривых две: комфорт у розетки и в дороге разный
    private readonly BrightnessCurve _curveBatt;
    private readonly MedianWindow _filter = new(); // «инерция»: медиана окна гасит блики
    private readonly Func<long> _clock;            // шов времени для тестов фильтра
    private readonly object _lock = new();

    private float _pendingLux = float.NaN; // последние люксы с датчика
    private float _actedLux = float.NaN;   // люксы, на которые уже среагировали (база гистерезиса)
    private float _armedLux = float.NaN;   // люксы, на которых взведён settle: сэмплы без нового
                                           // значимого сдвига таймер НЕ перевзводят — иначе поток
                                           // датчика (1.5 с) чаще дебаунса (2 с) отодвигал бы
                                           // Evaluate бесконечно, и фича не работала бы вовсе
    private int _current = -1;             // последняя известная яркость (все события)
    private bool _learning;                // серия ручных правок идёт
    private float _learnLux = float.NaN;   // свет в момент НАЧАЛА серии (правка обдумывалась при нём)
    private bool _learnOnline;             // источник питания в момент начала серии — чья кривая учится
    private int _learnPercent;
    private CancellationTokenSource? _rampCts;

    public AutoBrightnessGuard(AppConfig cfg, IPowerEvents power,
        IAppTimer? settle = null, IAppTimer? learn = null,
        Func<int?>? read = null, BrightnessCapGuard.RampFn? ramp = null,
        Func<bool, bool>? adaptive = null, Func<int, bool, int>? clamp = null,
        Func<long>? clock = null)
    {
        _clock = clock ?? (static () => Environment.TickCount64);
        _cfg = cfg;
        _power = power;
        _settle = settle ?? new WorkerTimer();
        _learn = learn ?? new WorkerTimer();
        _read = read ?? Brightness.Get;
        _ramp = ramp ?? ((f, t, ct) => Brightness.Ramp(f, t, Math.Max(1000, cfg.BrightnessRampMs), ct));
        _adaptive = adaptive ?? AdaptiveBrightness.IsEnabled;
        _clamp = clamp ?? ((level, _) => level);
        _curveAc = new BrightnessCurve(cfg.AutoBrightnessPointsAc);
        _curveBatt = new BrightnessCurve(cfg.AutoBrightnessPointsBattery);

        _settle.Tick += OnSettleTick;
        _learn.Tick += OnLearnTick;
    }

    /// <summary>
    /// Очередной сэмпл люксов (поток пула, из AlsWatcher, ~раз в 1.5 с). Решения принимаются
    /// не по мгновенному значению, а по МЕДИАНЕ окна «инерции»: у датчика нет интеграционной
    /// сферы, и случайный блик даёт всплеск на сэмпл-другой — медиану он не сдвигает вообще.
    /// Дальше как раньше: значимое (гистерезис в лог-шкале) изменение медианы взводит дебаунс
    /// стабилизации — экран не «дышит».
    /// </summary>
    public void OnLux(float lux)
    {
        lock (_lock)
        {
            int windowMs = Math.Clamp(_cfg.AutoBrightnessMedianSec, 0, 600) * 1000;
            long now = _clock();
            _filter.Add(now, lux, windowMs);
            float filtered = windowMs <= 0 ? lux : _filter.Median(now, windowMs);
            _pendingLux = filtered;
            if (!_cfg.AutoBrightness) return;
            if (!BrightnessCurve.Significant(_actedLux, filtered, Hysteresis())) { _armedLux = float.NaN; return; }
            // settle уже взведён на этот же (по гистерезису) свет → пусть дотикает; перевзвод —
            // только по НОВОМУ значимому сдвигу (свет продолжает меняться — ждём стабилизации)
            if (!float.IsNaN(_armedLux) && !BrightnessCurve.Significant(_armedLux, filtered, Hysteresis())) return;
            _armedLux = filtered;
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
                _learnOnline = _power.IsOnline; // и при этом питании — учится его кривая
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

        int from, to;
        lock (_lock)
        {
            int want = _clamp(CurveFor(online).Predict(lux), online);
            _actedLux = lux; // гистерезис отсчитываем от «на что смотрели», даже если не тронули
            if (Math.Abs(want - current) < Math.Max(1, _cfg.AutoBrightnessDeadband)) return;
            (from, to) = (current, want);
            StartRampLocked(from, to);
        }
        // полевая диагностика (XIC-36): по этой строке видно, что цепочка датчик → решение жива
        Log.Write($"AutoBrightness: {from}% → {to}% ({lux:0.#} лк, {(online ? "сеть" : "батарея")})");
    }

    private BrightnessCurve CurveFor(bool online) => online ? _curveAc : _curveBatt;

    private void OnSettleTick()
    {
        _settle.Stop();
        lock (_lock) _armedLux = float.NaN; // взвод отработал — следующий сдвиг взводит заново
        Evaluate();
    }

    private void OnLearnTick()
    {
        float lux;
        int percent;
        bool online;
        lock (_lock)
        {
            _learn.Stop();
            if (!_learning) return;
            _learning = false;
            lux = _learnLux;
            percent = _learnPercent;
            online = _learnOnline;
            if (!_cfg.AutoBrightness || float.IsNaN(lux)) return;

            // порог склейки = гистерезис: неразличимые для триггера условия не копят мнения;
            // уточняющая правка (в пределах ступени клавиш) сглаживается, а не заменяет (XIC-32)
            CurveFor(online).Learn(lux, percent, Hysteresis(),
                _cfg.AutoBrightnessLearnBlend, _cfg.AutoBrightnessFineStep);
            // предсказание в этих условиях теперь равно выученному — не воюем с пользователем
            _actedLux = lux;
        }
        Log.Write($"AutoBrightness: выучено {percent}% при {lux:0.#} лк ({(online ? "сеть" : "батарея")}, точек: {CurveFor(online).Count})");
        _cfg.Save(); // раз в правку (после раздумья) — SSD не страдает
    }

    /// <summary>Принудительный сброс обучения (кнопка в настройках): выученные точки ОБЕИХ
    /// кривых стираются, сеются кривые по умолчанию. Выключение/включение фичи кривые НЕ
    /// трогает — забыть их можно только этой явной командой.</summary>
    public void ResetCurve()
    {
        lock (_lock)
        {
            _cfg.AutoBrightnessPointsAc.Clear();
            _cfg.AutoBrightnessPointsAc.AddRange(BrightnessCurve.DefaultPoints());
            _cfg.AutoBrightnessPointsBattery.Clear();
            _cfg.AutoBrightnessPointsBattery.AddRange(BrightnessCurve.DefaultPoints());
            _learning = false;
            _learn.Stop();
            _actedLux = float.NaN; // следующие люксы значимы — пересчитаемся по свежей кривой
        }
        Log.Write("AutoBrightness: кривые обучения сброшены к умолчанию");
        _cfg.Save();
        // CancellationToken.None намеренно (S8949): _rampCts отменяет ПЛАВНЫЙ ХОД яркости, и
        // подставить его сюда было бы ошибкой — сверка по свежей кривой обязана доработать
        // независимо от того, жив ли предыдущий ход.
        if (_cfg.AutoBrightness) Task.Run(Evaluate, CancellationToken.None);
    }

    /// <summary>Снимок точек кривой выбранного источника питания для отрисовки в настройках
    /// (график рисует обе): копия под замком — обучение может идти параллельно на пуле.</summary>
    public BrightnessPoint[] CurveSnapshot(bool online)
    {
        var points = online ? _cfg.AutoBrightnessPointsAc : _cfg.AutoBrightnessPointsBattery;
        lock (_lock)
            return [.. points.Select(p => new BrightnessPoint { Lux = p.Lux, Percent = p.Percent })];
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
