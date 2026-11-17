namespace XiControl.SystemIntegration;

/// <summary>
/// Сильное нажатие как клик другой кнопкой (XIC-108, issue #107) — чистая логика, без хука.
///
/// Беда в порядке событий: тачпад нажимает левую кнопку раньше, чем давление дойдёт до порога
/// сильного нажатия, и без драйвера этот левый клик не отменить. Поэтому левое нажатие при пальце
/// на тачпаде придерживаем (проглатываем в хуке) и ждём: продавили — вместо левого клика
/// выполняется назначенный, не продавили — отдаём левое нажатие как было.
///
/// Замер на TM2424 (зонд, 3 сильных нажатия): порог 500 приходит через 110–210 мс после левого
/// нажатия, обычный клик держится 250–350 мс. Отсюда окно ожидания <see cref="DefaultWindowMs"/>.
/// Тап-клик сюда не попадает: Windows шлёт его нажатие, когда палец уже оторван.
///
/// Потокобезопасна: хук и таймер зовут с потока хука, <see cref="Heavy"/> — с потока касаний.
/// </summary>
public sealed class HeavyClickGate(int windowMs = HeavyClickGate.DefaultWindowMs, int moveTolerancePx = 4)
{
    public const int DefaultWindowMs = 300;

    /// <summary>Что сделать хосту после события. Ввод синтезируется только им.</summary>
    public enum Effect { None, InjectLeftDown, InjectLeftUp, InjectLeftClick, RunAction }

    private enum State
    {
        Idle,
        Pending,   // левое нажатие проглочено, ждём сильного нажатия
        Heavy,     // продавили — проглотить и отпускание, выполнить действие
        Flushing,  // отдали левое нажатие синтезом, а он ещё не прошёл хук
    }

    private readonly object _lock = new();
    private State _state;
    private long _since;
    private int _x, _y;

    /// <summary>Ждём ли сейчас (хосту — держать таймер).</summary>
    public bool Pending { get { lock (_lock) return _state == State.Pending; } }

    /// <summary>Физическое левое нажатие. true — проглотить.</summary>
    public bool LeftDown(bool touching, long nowMs, int x, int y)
    {
        lock (_lock)
        {
            // тап, внешняя мышь, повтор посреди ожидания — не наше
            if (!touching || _state != State.Idle) return false;
            (_state, _since, _x, _y) = (State.Pending, nowMs, x, y);
            return true;
        }
    }

    /// <summary>Физическое левое отпускание. Swallow — проглотить, Effect — что синтезировать.</summary>
    public (bool Swallow, Effect Effect) LeftUp()
    {
        lock (_lock)
        {
            switch (_state)
            {
                case State.Pending: _state = State.Idle; return (true, Effect.InjectLeftClick); // быстрый клик
                case State.Heavy: _state = State.Idle; return (true, Effect.RunAction);
                case State.Flushing:
                    // отпускание обогнало наше нажатие в очереди ввода — пропусти его сейчас, и
                    // кнопка залипнет нажатой; синтезируем отпускание вслед за нажатием
                    return (true, Effect.InjectLeftUp);
                default: return (false, Effect.None);
            }
        }
    }

    /// <summary>Наше синтезированное левое нажатие прошло хук.</summary>
    public void InjectedDownSeen()
    {
        lock (_lock)
            if (_state == State.Flushing) _state = State.Idle;
    }

    /// <summary>Движение указателя: повели палец с нажатием — это перетаскивание, отдаём нажатие сразу.</summary>
    public Effect Move(int x, int y)
    {
        lock (_lock)
        {
            if (_state != State.Pending) return Effect.None;
            if (Math.Abs(x - _x) <= moveTolerancePx && Math.Abs(y - _y) <= moveTolerancePx) return Effect.None;
            return Flush();
        }
    }

    /// <summary>Тик таймера: окно вышло, а сильного нажатия не было — отдаём левое нажатие.</summary>
    public Effect Tick(long nowMs)
    {
        lock (_lock)
            return _state == State.Pending && nowMs - _since >= windowMs ? Flush() : Effect.None;
    }

    /// <summary>Сильное нажатие (поток касаний). true — поймали в окне, клик будет заменён.</summary>
    public bool Heavy()
    {
        lock (_lock)
        {
            if (_state != State.Pending) return false;
            _state = State.Heavy;
            return true;
        }
    }

    // Под замком. Отпускание после этого пропускаем, только когда наше нажатие уже прошло.
    private Effect Flush()
    {
        _state = State.Flushing;
        return Effect.InjectLeftDown;
    }
}
