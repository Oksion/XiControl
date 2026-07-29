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
            _debounce.Start();
        }
        // Suspend — вход в сон/гибернацию/«выключение» с быстрым запуском: ре-арм сразу,
        // без дебаунса — после этого события наш код уже не выполнится
        else if (mode == PowerModes.Suspend)
        {
            _debounce.Stop();
            Reapply();
        }
    }

    // Завершение сеанса (shutdown/restart/logoff) — последняя возможность заармить EC
    // перед периодом «выключено»; заодно закрывает окно, когда дебаунс (1.5 с) не успел
    private void OnSessionEnding() => Reapply();

    /// <summary>Применить желаемый порог заряда прямо сейчас (напр. при старте).</summary>
    public void Reapply()
    {
        try
        {
            // ре-арм только когда «беречь» включено (порог < 100); отказ прошивки — в лог:
            // молча оставить батарею на 100% хуже, чем след в log.txt
            int pct = _limitWanted();
            if (pct < 100 && !_mifs.SetChargeLimit(pct))
                Log.Write($"ChargeGuard.Reapply: прошивка отвергла порог {pct}%");
        }
        catch (Exception ex) { Log.Ex("ChargeGuard.Reapply", ex); /* железо могло быть недоступно */ }
    }

    public void Dispose()
    {
        _power.PowerModeChanged -= OnPowerModeChanged;
        _power.SessionEnding -= OnSessionEnding;
        _debounce.Dispose();
    }
}
