using Microsoft.Win32;
using XiControl.Wmi;

namespace XiControl.SystemIntegration;

/// <summary>
/// Переустанавливает лимит заряда после сна и смены питания (AC↔батарея).
/// EC сбрасывает защиту на этих событиях (проверено вживую), поэтому её нужно
/// переармливать, пока пользователь хочет «беречь батарею».
/// Дополнительно ре-арм на входе в сон/гибернацию и при завершении сеанса — иначе EC
/// остаётся без защиты на весь период «выключено» и батарея заряжается до 100%
/// («Выключение» в Windows 11 с быстрым запуском — это тоже гибернация). Служба
/// оригинального MIControl делает так же (suspend-колбэк срабатывает и на входе).
/// </summary>
public sealed class ChargeGuard : IDisposable
{
    private readonly IMifsClient _mifs;
    private readonly IPowerEvents _power;
    private readonly Func<int> _limitWanted;   // желаемый порог %, 100 = «беречь» выкл (из настроек/UI)
    private readonly IAppTimer _debounce;

    public ChargeGuard(IMifsClient mifs, IPowerEvents power, Func<int> limitWanted, IAppTimer? debounce = null)
    {
        _mifs = mifs;
        _power = power;
        _limitWanted = limitWanted;

        // события StatusChange могут сыпаться пачкой — гасим дребезг
        _debounce = debounce ?? new UiTimer();
        _debounce.Interval = 1500;
        _debounce.Tick += () => { _debounce.Stop(); Reapply(); };

        _power.PowerModeChanged += OnPowerModeChanged;
        _power.SessionEnding += OnSessionEnding;
    }

    private void OnPowerModeChanged(PowerModes mode)
    {
        // Resume — выход из сна; StatusChange — смена питания AC↔батарея
        if (mode is PowerModes.Resume or PowerModes.StatusChange)
        {
            _debounce.Stop();
            _debounce.Interval = 1500;
            _reason = mode == PowerModes.Resume ? "после сна" : "смена питания";
            _debounce.Start();
        }
        // Suspend — вход в сон/гибернацию/«выключение» с быстрым запуском: ре-арм сразу,
        // без дебаунса — после этого события наш код уже не выполнится
        else if (mode == PowerModes.Suspend)
        {
            _debounce.Stop();
            Reapply("перед сном", hurry: true);
        }
    }

    // Завершение сеанса (shutdown/restart/logoff) — последняя возможность заармить EC
    // перед периодом «выключено»; заодно закрывает окно, когда дебаунс (1.5 с) не успел
    private void OnSessionEnding() => Reapply("завершение сеанса", hurry: true);

    private string _reason = "старт";

    /// <summary>Применить желаемый порог заряда прямо сейчас (напр. при старте).</summary>
    public void Reapply() => Reapply(_reason);

    /// <summary>
    /// Переармить EC. <paramref name="hurry"/> — времени почти нет (уход в сон, завершение
    /// сеанса): пишем одной командой, без сброса в «выкл» и без чтения-назад.
    ///
    /// Сначала СПРАШИВАЕМ, что стоит сейчас. Раньше писали всегда и вслепую, а запись — это
    /// ре-арм off→on, то есть короткий промежуток совсем без защиты. Перед сном такая
    /// «профилактика» могла обернуться ночью на 100%: система засыпает ровно в промежутке.
    /// Совпало с желаемым — ничего не трогаем, это и быстрее, и безопаснее (XIC-64).
    /// </summary>
    public void Reapply(string reason, bool hurry = false)
    {
        try
        {
            // ре-арм только когда «беречь» включено (порог < 100)
            int pct = _limitWanted();
            if (pct >= 100) return;

            int? now = _mifs.GetChargeLimit();
            if (now == pct) return;   // EC уже держит нужный порог — молча и не трогая

            if (!_mifs.SetChargeLimit(pct, resetFirst: !hurry))
            {
                Log.Write($"ChargeGuard ({reason}): прошивка отвергла порог {pct}% (было {Show(now)})");
                return;
            }

            // Спешка — верить на слово: лишний WMI-вызов в момент засыпания дороже проверки.
            if (hurry) { Log.Write($"ChargeGuard ({reason}): {Show(now)} → {pct}%"); return; }

            // Чтение-назад. Прошивка отвечает «принято» и на команду, которую EC не удержал:
            // без сверки такой отказ выглядел бы успехом, и батарея тихо уходила бы выше порога.
            int? after = _mifs.GetChargeLimit();
            if (after == pct) Log.Write($"ChargeGuard ({reason}): {Show(now)} → {pct}%");
            else Log.Write($"ChargeGuard ({reason}): порог не удержался — просили {pct}%, " +
                           $"в прошивке {Show(after)} (было {Show(now)})");
        }
        catch (Exception ex) { Log.Ex("ChargeGuard.Reapply", ex); /* железо могло быть недоступно */ }
    }

    private static string Show(int? pct) => pct is int v ? $"{v}%" : "неизвестно";

    public void Dispose()
    {
        _power.PowerModeChanged -= OnPowerModeChanged;
        _power.SessionEnding -= OnSessionEnding;
        _debounce.Dispose();
    }
}
