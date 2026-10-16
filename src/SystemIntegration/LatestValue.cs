namespace XiControl.SystemIntegration;

/// <summary>
/// Последнее готовое значение медленного источника (WMI), прочитанное на пуле (XIC-81).
/// UI зовёт <see cref="Request"/> на каждом тике и рисует <see cref="Latest"/> — значение
/// прошлого чтения, на тик старее, зато UI-поток не ждёт провайдера ни миллисекунды.
///
/// Цена тика в норме мала (DPTF ~18 мс, MIFS ~10 мс на TM2424), но WMI-провайдер умеет
/// зависать — например, после сна. В UI-потоке это вешало бы весь интерфейс, а с ним и
/// глобальный хук мыши панели (Windows молча снимает хуки неотвечающего потока). Здесь же
/// чтение в полёте одно: зависшее не плодит новых — значение просто стареет, и
/// <see cref="Fresh"/> отдаёт вместо него «нет данных».
/// </summary>
public sealed class LatestValue<T>
{
    private readonly Func<T> _read;
    private readonly Func<long> _now;
    private readonly string _logName;
    private readonly object _gate = new();
    private T _latest;
    private long _at = long.MinValue; // когда прочитано (мс, _now); MinValue — ни разу
    private Task? _pending;            // чтение в полёте; null — свободно

    /// <param name="read">Чтение; зовётся только на пуле, не чаще одного одновременно.</param>
    /// <param name="initial">Значение до первого чтения и после неудачного.</param>
    /// <param name="logName">Имя для лога исключений чтения.</param>
    /// <param name="now">Часы в мс — шов для тестов; по умолчанию <see cref="Environment.TickCount64"/>.</param>
    public LatestValue(Func<T> read, T initial, string logName, Func<long>? now = null)
    {
        _read = read;
        _latest = initial;
        _logName = logName;
        _now = now ?? (() => Environment.TickCount64);
    }

    /// <summary>Последнее прочитанное значение (или начальное).</summary>
    public T Latest { get { lock (_gate) return _latest; } }

    /// <summary>Последнее значение, если оно не старше <paramref name="maxAgeMs"/>; иначе
    /// <paramref name="stale"/>. Чтобы зависший провайдер не выдавал минутной давности
    /// температуру за текущую.</summary>
    public T Fresh(long maxAgeMs, T stale)
    {
        lock (_gate)
            return _at != long.MinValue && _now() - _at <= maxAgeMs ? _latest : stale;
    }

    /// <summary>Чтение в полёте (или завершённая задача, если его нет). Владелец источника ждёт
    /// его перед тем, как освободить то, что читает <c>read</c>: иначе чтение на пуле падает на
    /// уже освобождённом объекте и пишет в журнал ложный ObjectDisposedException (XIC-101).
    /// Не бросает — ReadOnce исключения глотает.</summary>
    public Task Pending { get { lock (_gate) return _pending ?? Task.CompletedTask; } }

    /// <summary>Хоть одно чтение уже завершилось удачно.</summary>
    public bool HasValue { get { lock (_gate) return _at != long.MinValue; } }

    /// <summary>Запустить чтение на пуле, если предыдущее уже закончилось. Возвращает задачу
    /// чтения — новую или ту, что уже в полёте; ждать её вызывающему не обязательно.</summary>
    public Task Request()
    {
        lock (_gate)
        {
            if (_pending is not null) return _pending;
            return _pending = Task.Run(ReadOnce);
        }
    }

    /// <summary>Дождаться ПЕРВОГО значения, но не дольше <paramref name="timeoutMs"/>: когда
    /// от него зависит раскладка окна. Ограничение и есть смысл — зависший провайдер держит
    /// UI не дольше таймаута, а не вечно. Значение уже есть — не ждёт вовсе.</summary>
    public void WaitFirst(int timeoutMs)
    {
        if (!HasValue) Request().Wait(timeoutMs); // ReadOnce не бросает — Wait тоже
    }

    private void ReadOnce()
    {
        try
        {
            T v = _read();
            lock (_gate) { _latest = v; _at = _now(); }
        }
        // старое значение остаётся и стареет само — Fresh спрячет его, когда пора
        catch (Exception ex) { Log.Ex(_logName, ex); }
        finally { lock (_gate) _pending = null; }
    }
}
