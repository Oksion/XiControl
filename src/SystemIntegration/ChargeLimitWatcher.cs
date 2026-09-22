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

/// <summary>
/// Наблюдение «заряд дошёл до порога» (XIC-75). Событие одно, потребителей у него двое:
/// вебхук (розетка выключается сама) и — когда дойдут руки до XIC-74 — уведомление человеку
/// на моделях без аппаратного лимита. Поэтому наблюдатель знает только про заряд и порог,
/// а что с этим делать, решает подписчик.
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
    private bool _fired; // о пороге этой зарядки уже сообщили

    /// <summary>Заряд дошёл до порога: (текущий %, порог %). Один раз за зарядку.</summary>
    public Action<int, int>? Reached;

    public ChargeLimitWatcher(AppConfig cfg, IPowerEvents power, IAppTimer? timer = null)
    {
        _cfg = cfg;
        _power = power;
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
        if (!_power.IsOnline) _fired = false; // отключили — следующая зарядка сообщит снова
        if (_cfg.ChargeCare && _power.IsOnline) _timer.Start();
        else _timer.Stop();
    }

    private void Check()
    {
        int limit = _cfg.CarePercent();
        var what = Decide(_cfg.ChargeCare, _power.IsOnline, limit, _power.BatteryLifePercent, _fired);
        switch (what)
        {
            case ChargeWatch.Reset:
                _fired = false;
                _timer.Stop();
                break;
            case ChargeWatch.Fire:
                _fired = true;
                Reached?.Invoke((int)Math.Round(_power.BatteryLifePercent * 100), limit);
                break;
            default:
                if (!_cfg.ChargeCare) _timer.Stop(); // порог выключили, пока мы ждали
                break;
        }
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
