using Microsoft.Win32;
using XiControl.SystemIntegration;
using XiControl.Wmi;

namespace XiControl.Tests;

/// <summary>Фейк прошивки: пишет вызовы, отвечает настроенными значениями.</summary>
internal sealed class FakeMifsClient : IMifsClient
{
    public readonly List<int> ChargeLimitCalls = [];   // проценты, отправленные в SetChargeLimit
    public readonly List<PerfMode> PerfModeCalls = [];
    public bool SetPerfModeResult = true;
    public bool SetChargeLimitResult = true;   // валидирует ли прошивка (для проверки фолбэка)
    public int? ChargeLimit { get; set; }   // что вернёт GetChargeLimit (null — «не прочиталось»)
    public PerfMode? Mode;               // что вернёт GetPerfMode
    public bool ThrowOnGetPerfMode;      // симуляция недоступного железа
    public bool ThrowOnSetChargeLimit;   // симуляция отказа прошивки на запись

    /// <summary>Сигнал «SetPerfMode вызван» — для ожидания асинхронных Apply (Task.Run в guard-ах).</summary>
    public readonly SemaphoreSlim PerfModeHit = new(0);

    public PerfMode? GetPerfMode() =>
        ThrowOnGetPerfMode ? throw new InvalidOperationException("нет железа") : Mode;

    public bool SetPerfMode(PerfMode mode)
    {
        PerfModeCalls.Add(mode);
        PerfModeHit.Release();
        return SetPerfModeResult;
    }

    public int? GetChargeLimit() => ChargeLimit;

    public bool SetChargeLimit(int percent)
    {
        if (ThrowOnSetChargeLimit) throw new InvalidOperationException("прошивка не ответила");
        ChargeLimitCalls.Add(percent);
        return SetChargeLimitResult;
    }

    public int GetAdapterWatts() => 0;
    public int? GetBatteryHealth() => null;
    public void Dispose() { }
}

/// <summary>Фейк питания: события поднимаются вручную, IsOnline настраивается.</summary>
internal sealed class FakePowerEvents : IPowerEvents
{
    public event Action<PowerModes>? PowerModeChanged;
    public event Action? SessionEnding;

    public bool IsOnline { get; set; } = true;
    public float BatteryLifePercent { get; set; } = 0.5f;

    public void RaisePower(PowerModes mode) => PowerModeChanged?.Invoke(mode);
    public void RaiseSession() => SessionEnding?.Invoke();
    public void Dispose() { }
}

/// <summary>Фейк таймера: Fire() тикает вручную (только если запущен — как настоящий).</summary>
internal sealed class FakeTimer : IAppTimer
{
    public bool Running { get; private set; }
    public int Interval { get; set; }

    public event Action? Tick;

    public void Start() => Running = true;
    public void Stop() => Running = false;

    public void Fire()
    {
        if (Running) Tick?.Invoke();
    }

    public void Dispose() { }
}
