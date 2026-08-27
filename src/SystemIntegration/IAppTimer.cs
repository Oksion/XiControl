using Microsoft.UI.Dispatching;

namespace XiControl.SystemIntegration;

/// <summary>Подменяемый таймер приложения: тесты используют ручной fake, production выбирает реализацию по семантике потока.</summary>
public interface IAppTimer : IDisposable
{
    int Interval { get; set; }
    event Action? Tick;
    void Start();
    void Stop();
}

/// <summary>Таймер пула потоков для аппаратных guard-ов и фонового семплирования.</summary>
public sealed class WorkerTimer : IAppTimer
{
    private readonly System.Threading.Timer _timer;

    public WorkerTimer() => _timer = new System.Threading.Timer(_ => Tick?.Invoke());

    public int Interval { get; set; } = 100;
    public event Action? Tick;

    public void Start() => _timer.Change(Interval, Interval);
    public void Stop() => _timer.Change(Timeout.Infinite, Timeout.Infinite);
    public void Dispose() => _timer.Dispose();
}

/// <summary>WinUI DispatcherQueue-таймер; callback всегда выполняется в создавшем его UI-потоке.</summary>
public sealed class UiTimer : IAppTimer
{
    private readonly DispatcherQueueTimer _timer;
    private int _interval = 100;

    public UiTimer()
    {
        var queue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("UiTimer должен создаваться в WinUI-потоке.");
        _timer = queue.CreateTimer();
        _timer.Tick += (_, _) => Tick?.Invoke();
        ApplyInterval();
    }

    public int Interval
    {
        get => _interval;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _interval = value;
            ApplyInterval();
        }
    }

    public event Action? Tick;

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();
    public void Dispose() => _timer.Stop();

    private void ApplyInterval() => _timer.Interval = TimeSpan.FromMilliseconds(_interval);
}
