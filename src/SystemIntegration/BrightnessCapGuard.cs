using XiControl.Config;

namespace XiControl.SystemIntegration;

/// <summary>
/// Лимит яркости экрана (XIC-29): выше лимита (свой для сети и батареи) яркость плавно
/// сводится обратно. Заблокировать ползунок Windows невозможно — только вернуть после факта,
/// поэтому вместо ультиматума — вежливый торг: раз в BrightnessConvergeMs разрыв сокращается
/// в BrightnessGapDivisor раз (каждый шаг — плавный ход Brightness.Ramp), остаток ≤
/// BrightnessSnapPercent доводится сразу. Повторный подъём пользователем после нашего шага —
/// осознанный сигнал «мне правда нужно ярче»: отступаем на BrightnessBackoffMin (сбрасывается
/// блокировкой, сном, сменой питания, перезапуском). Понижение ниже лимита не трогаем вовсе —
/// мы никогда не поднимаем. При включённой адаптивной яркости не работает: Windows поднимала
/// бы яркость по датчику, мы — возвращали, получилась бы качель.
///
/// События яркости сюда доставляет PowerProfileGuard (единственный подписчик BrightnessWatcher),
/// уже отличив наши записи от пользовательских по меткам Brightness.Own. Всё происходит на
/// потоках пула (события WMI, WorkerTimer) — состояние под общим замком, UI-поток не участвует.
/// </summary>
public sealed class BrightnessCapGuard : IDisposable
{
    /// <summary>Плавный ход яркости; шов для тестов (прод — Brightness.Ramp).</summary>
    public delegate void RampFn(int from, int to, CancellationToken ct);

    private readonly AppConfig _cfg;
    private readonly IPowerEvents _power;
    private readonly IAppTimer _converge;      // шаги схождения, раз в BrightnessConvergeMs
    private readonly IAppTimer _backoffTimer;  // одноразовый: конец паузы после «протеста»
    private readonly Func<int?> _read;
    private readonly RampFn _ramp;
    private readonly Func<bool, bool> _adaptive;
    private readonly object _lock = new();

    private int _last = -1;     // последняя известная яркость (события и чтения); -1 — неизвестна
    private bool _converging;   // таймер схождения взведён (у IAppTimer нет IsRunning)
    private bool _stepped;      // в этом эпизоде уже был наш шаг вниз → подъём = осознанный протест
    private bool _backoff;      // отступили: до конца паузы яркость не трогаем
    private CancellationTokenSource? _rampCts;

    public BrightnessCapGuard(AppConfig cfg, IPowerEvents power,
        IAppTimer? converge = null, IAppTimer? backoff = null,
        Func<int?>? read = null, RampFn? ramp = null, Func<bool, bool>? adaptive = null)
    {
        _cfg = cfg;
        _power = power;
        _converge = converge ?? new WorkerTimer();
        _backoffTimer = backoff ?? new WorkerTimer();
        _read = read ?? Brightness.Get;
        _ramp = ramp ?? ((f, t, ct) => Brightness.Ramp(f, t, Math.Max(1000, cfg.BrightnessRampMs), ct));
        _adaptive = adaptive ?? AdaptiveBrightness.IsEnabled;

        _converge.Tick += OnConvergeTick;
        _backoffTimer.Tick += OnBackoffTick;
    }

    /// <summary>Лимит для источника питания; кривое config.json-значение клэмпится
    /// (0% погасил бы экран совсем; 100 = лимит фактически выключен).</summary>
    public int Cap(bool online) =>
        Math.Clamp(online ? _cfg.BrightnessCapAc : _cfg.BrightnessCapBattery, 10, 100);

    /// <summary>
    /// Можно ли записать эту яркость в слот «Запоминать яркость»: слот хранит намерение
    /// пользователя, превышение лимита не сохраняется ВООБЩЕ (не обрезается!) — кламп при
    /// записи постепенно съедал бы настройку человека: комфортные 55 при лимите 60 после
    /// одного подъёма до 80 навсегда превратились бы в 60.
    /// </summary>
    public bool AllowsRemember(int level) =>
        !_cfg.BrightnessCapEnabled || level <= Cap(_power.IsOnline);

    /// <summary>Кламп восстанавливаемой яркости: слот, запомненный при старом (высоком) лимите,
    /// не должен пробить новый. Сам слот в конфиге не трогается — вернут лимит, вернётся и яркость.</summary>
    public int ClampRestore(int level, bool online) =>
        _cfg.BrightnessCapEnabled ? Math.Min(level, Cap(online)) : level;

    /// <summary>
    /// Следующий шаг схождения: разрыв (current − cap) сокращается в divisor раз (вверх до
    /// целого), остаток ≤ snap доводится до лимита сразу — иначе гонялись бы за половинками
    /// бесконечно. Пример (лимит 60, делитель 2): 80 → 70 → 65 → 63 → 62 → 60.
    /// </summary>
    public static int NextStep(int current, int cap, int divisor, int snap)
    {
        int gap = current - cap;
        if (gap <= Math.Max(1, snap)) return cap;
        divisor = Math.Max(2, divisor); // 1 не сокращал бы разрыв — схождение стояло бы на месте
        return cap + (gap + divisor - 1) / divisor;
    }

    /// <summary>
    /// Событие яркости от PowerProfileGuard. Наши записи (own) только обновляют «последнюю
    /// известную»; для пользовательских решается судьба эпизода: ниже лимита — не наше дело,
    /// подъём после нашего шага — отступаем, иначе — (пере)взводим схождение. settling —
    /// окно затишья после смены питания: яркость в нём меняет сама Windows, протестом не считаем.
    /// </summary>
    public void OnBrightness(int level, bool own, bool settling)
    {
        lock (_lock)
        {
            int prev = _last;
            _last = level;
            if (own) return;
            if (!_cfg.BrightnessCapEnabled) return;

            CancelRampLocked(); // человек взялся за ползунок — наш недоигранный ход неактуален

            if (level <= Cap(_power.IsOnline)) { EpisodeDoneLocked(); return; }
            if (_backoff) return;
            if (!settling && prev >= 0 && level > prev && _stepped) { BackoffLocked(); return; }
            EnsureConvergeLocked();
        }
    }

    /// <summary>
    /// Свериться с текущей яркостью: старт приложения, включение фичи, смена лимитов, смена
    /// питания, конец паузы. Читает WMI синхронно — звать с фонового потока (Task.Run).
    /// </summary>
    public void Evaluate()
    {
        bool online = _power.IsOnline;
        if (!_cfg.BrightnessCapEnabled || _adaptive(online))
        {
            if (_cfg.BrightnessCapEnabled) Log.Write("BrightnessCap: адаптивная яркость включена — лимит не работает");
            Halt();
            return;
        }

        int? cur = _read();
        lock (_lock)
        {
            if (cur is int c) _last = c;
            if (_backoff) return;
            if (_last > Cap(online))
            {
                Log.Write($"BrightnessCap: яркость {_last}% выше лимита {Cap(online)}% — схождение взведено");
                EnsureConvergeLocked();
            }
            else EpisodeDoneLocked(); // яркость неизвестна (-1) или в норме — следить нечего
        }
    }

    /// <summary>Сбросить паузу «не трогаем» (блокировка/разблокировка, пробуждение, смена
    /// питания, смена настроек): условия сменились — торг начинается заново.</summary>
    public void ResetBackoff()
    {
        lock (_lock)
        {
            if (!_backoff) return;
            _backoffTimer.Stop();
            _backoff = false;
        }
    }

    // Шаг схождения. Тикает на пуле (WorkerTimer) — синхронные Win32/WMI-проверки здесь законны.
    private void OnConvergeTick()
    {
        bool online = _power.IsOnline;
        if (!_cfg.BrightnessCapEnabled || _adaptive(online)) { Halt(); return; } // адаптивную включили на ходу

        int level;
        lock (_lock)
        {
            if (_backoff || !_converging) return;
            level = _last;
        }
        if (level < 0 && _read() is int c) { level = c; lock (_lock) _last = c; }

        lock (_lock)
        {
            int cap = Cap(online);
            if (level <= cap) { EpisodeDoneLocked(); return; } // сошлись (или яркость так и не прочлась)

            int to = NextStep(level, cap, _cfg.BrightnessGapDivisor, _cfg.BrightnessSnapPercent);
            _stepped = true;
            Log.Write($"BrightnessCap: шаг схождения {level}% → {to}% (лимит {cap}%)");
            if (to <= cap)
            {
                // финальный шаг — доводим до лимита и закрываем эпизод; подъём во время этого
                // хода начнёт новый эпизод (снова вежливо, с минуты ожидания), не протест
                EpisodeDoneLocked();
            }
            StartRampLocked(level, to);
        }
    }

    private void OnBackoffTick()
    {
        lock (_lock)
        {
            _backoffTimer.Stop(); // одноразовый
            if (!_backoff) return;
            _backoff = false;
        }
        Evaluate(); // пауза вышла — всё ещё выше лимита? снова сходимся
    }

    // Полная остановка (фича выключена / адаптивная яркость): забыть эпизод и паузу.
    private void Halt()
    {
        lock (_lock)
        {
            EpisodeDoneLocked();
            _backoffTimer.Stop();
            _backoff = false;
        }
    }

    private void EnsureConvergeLocked()
    {
        if (_converging) return;
        _converge.Interval = Math.Max(5000, _cfg.BrightnessConvergeMs); // пол — защита от кривого config.json
        _converge.Start();
        _converging = true;
    }

    private void EpisodeDoneLocked()
    {
        _converge.Stop();
        _converging = false;
        _stepped = false;
        CancelRampLocked();
    }

    private void BackoffLocked()
    {
        Log.Write($"BrightnessCap: пользователь поднял яркость после нашего шага — пауза {_cfg.BrightnessBackoffMin} мин");
        EpisodeDoneLocked();
        _backoff = true;
        _backoffTimer.Interval = Math.Clamp(_cfg.BrightnessBackoffMin, 1, 24 * 60) * 60_000;
        _backoffTimer.Start();
    }

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
        _converge.Dispose();
        _backoffTimer.Dispose();
    }
}
