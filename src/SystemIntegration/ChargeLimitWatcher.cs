using XiControl.Config;

namespace XiControl.SystemIntegration;

/// <summary>Что делать на очередной проверке заряда (чистое решение — под тестами).</summary>
public enum ChargeWatch
{
    /// <summary>Ничего: порог не достигнут либо о нём уже сообщили.</summary>
    Idle,

    /// <summary>Порог достигнут впервые за эту зарядку — сообщать.</summary>
    Fire,

    /// <summary>Зарядник отключён — взводимся заново: следующая зарядка сообщит снова.</summary>
    Reset,
}

/// <summary>За каким порогом следим и чей он: аппаратный (прошивка сама остановит заряд)
/// или программный (остановить мы не можем — можно только сказать человеку).</summary>
public readonly record struct ChargeTarget(int Limit, bool Soft);

/// <summary>
/// Наблюдение «заряд дошёл до порога». Событие одно, потребителей двое: вебхук (XIC-75 —
/// розетка выключается сама) и предупреждение человеку на моделях без аппаратного лимита
/// (XIC-74). Поэтому наблюдатель знает только про заряд и порог, а что с этим делать,
/// решает подписчик.
///
/// Опрос, а не событие: Windows не умеет будить по «батарея доросла до N%», а
/// <c>PowerModeChanged</c> с процентами не приходит. Раз в 30 секунд — заряд от 60 до 80
/// идёт минутами, чаще смысла нет; при отключённом пороге или на батарее таймер стоит.
/// </summary>
public sealed class ChargeLimitWatcher : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly IPowerEvents _power;
    private readonly IAppTimer _timer;
    private readonly Func<long> _clock;
    private bool _fired;      // о пороге этой зарядки уже сообщили
    private long _firedAt;    // когда именно — отсюда считаются напоминания
    private int _reminders;   // сколько напоминаний уже отправили за эту зарядку

    /// <summary>Заряд дошёл до порога: (текущий %, порог, напоминание ли это). Первое
    /// срабатывание — одно за зарядку; дальше — только напоминания программного порога.</summary>
    public Action<int, ChargeTarget, bool>? Reached;

    public ChargeLimitWatcher(AppConfig cfg, IPowerEvents power, IAppTimer? timer = null, Func<long>? clock = null)
    {
        _cfg = cfg;
        _power = power;
        _clock = clock ?? (static () => Environment.TickCount64);
        _timer = timer ?? new UiTimer();
        _timer.Interval = 30_000;
        _timer.Tick += Check;
    }

    /// <summary>
    /// Перевзвести по текущему состоянию — звать на старте, на смене питания и после
    /// изменения порога. Наблюдаем только когда есть чего ждать: включён режим «беречь»
    /// и мы на зарядке.
    /// </summary>
    public void Rearm()
    {
        if (!_power.IsOnline) Forget();        // отключили — следующая зарядка сообщит снова
        if (Target() is not null && _power.IsOnline) _timer.Start();
        else _timer.Stop();
    }

    private void Check()
    {
        if (Target() is not ChargeTarget t)
        {
            _timer.Stop();                     // следить стало не за чем (выключили обе опции)
            return;
        }

        float life = _power.BatteryLifePercent;
        switch (Decide(true, _power.IsOnline, t.Limit, life, _fired))
        {
            case ChargeWatch.Reset:
                Forget();
                _timer.Stop();
                return;
            case ChargeWatch.Fire:
                _fired = true;
                _firedAt = _clock();
                Reached?.Invoke((int)Math.Round(life * 100), t, false);
                return;
        }

        // Первое срабатывание уже было. Аппаратному порогу добавить нечего — прошивка сама
        // остановила заряд; а программный держится только на человеке, и если он не подошёл,
        // зарядка идёт дальше — напоминаем, пока провод в розетке.
        if (!t.Soft || !_fired) return;
        if (!ShouldRemind(_power.IsOnline, life, t.Limit, _clock() - _firedAt,
                Math.Max(1, _cfg.SoftChargeRepeatMin) * 60_000L, _reminders, Math.Max(0, _cfg.SoftChargeRepeatMax)))
            return;
        _reminders++;
        _firedAt = _clock();
        Reached?.Invoke((int)Math.Round(life * 100), t, true);
    }

    private void Forget()
    {
        _fired = false;
        _reminders = 0;
    }

    /// <summary>
    /// За каким порогом следить. Программный порог предлагается ТОЛЬКО там, где аппаратного
    /// нет: иначе мы подсовывали бы костыль рядом с настоящим решением и будили человека
    /// ради того, что прошивка делает сама.
    /// </summary>
    public static ChargeTarget? Target(bool care, int careLimit, bool softOn, int softLimit, bool hardwareMissing) =>
        hardwareMissing
            ? softOn ? new ChargeTarget(Math.Clamp(softLimit, 20, 100), true) : null
            : care ? new ChargeTarget(careLimit, false) : null;

    private ChargeTarget? Target() => Target(_cfg.ChargeCare, _cfg.CarePercent(),
        _cfg.SoftChargeAlert, _cfg.SoftChargeLimitPercent, _cfg.ChargeLimitUnsupported);

    /// <summary>
    /// Пора ли напомнить. Напоминаем, пока выполняются все условия сразу: зарядник в розетке,
    /// заряд всё ещё выше порога (человек мог выдернуть и снова воткнуть при меньшем заряде),
    /// прошло достаточно времени и лимит напоминаний не исчерпан. Последнее — не формальность:
    /// приложение, которое пищит каждые четверть часа до утра, выключают целиком.
    /// </summary>
    public static bool ShouldRemind(bool online, float life, int limit, long sinceMs, long everyMs, int sent, int max)
    {
        if (!online || sent >= max) return false;
        if (life is < 0f or > 1f || life * 100f < limit) return false;
        return sinceMs >= everyMs;
    }

    /// <summary>
    /// Решение по одному замеру. Вынесено отдельно и без таймеров — здесь единственная
    /// нетривиальная логика: «один раз за зарядку» и что считать концом зарядки.
    ///
    /// <paramref name="life"/> — доля 0..1 из WinForms-семантики: значение вне диапазона
    /// означает «батарея неизвестна» (так отвечает машина без батареи или сразу после
    /// пробуждения), и на нём мы молчим, а не считаем 255 % за достигнутый порог.
    /// </summary>
    public static ChargeWatch Decide(bool care, bool online, int limit, float life, bool fired)
    {
        if (!online) return fired ? ChargeWatch.Reset : ChargeWatch.Idle;
        if (!care || fired) return ChargeWatch.Idle;
        if (life is < 0f or > 1f) return ChargeWatch.Idle; // «неизвестно» — не повод сообщать
        return life * 100f >= limit ? ChargeWatch.Fire : ChargeWatch.Idle;
    }

    public void Dispose()
    {
        _timer.Tick -= Check;
        _timer.Dispose();
    }
}
