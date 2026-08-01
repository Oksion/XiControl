namespace XiControl.SystemIntegration;

/// <summary>
/// Таймер за интерфейсом: дебаунс guard-ов (а позже — жесты Mi-кнопки) тестируется
/// без цикла сообщений WinForms — фейк дёргает Tick вручную.
/// В DI-контейнер НЕ регистрировать: каждому потребителю нужен свой экземпляр
/// (guard-ы независимо стартуют/стопят), singleton сломал бы дебаунс.
/// </summary>
public interface IAppTimer : IDisposable
{
    int Interval { get; set; }
    event Action? Tick;
    void Start();
    void Stop();
}

/// <summary>Прод-реализация поверх System.Windows.Forms.Timer (тикает в UI-потоке).
/// Start — только с потока с насосом сообщений: WinForms-таймер, стартованный с фонового
/// потока (напр. SystemEvents), не тикает никогда. Событийные источники это учитывают —
/// SystemEventsSource маршалит события питания и экрана, TrayApp — клавиши прошивки.</summary>
public sealed class UiTimer : IAppTimer
{
    private readonly System.Windows.Forms.Timer _t = new();

    public event Action? Tick;

    public UiTimer() => _t.Tick += (_, _) => Tick?.Invoke();

    public int Interval { get => _t.Interval; set => _t.Interval = value; }
    public void Start() => _t.Start();
    public void Stop() => _t.Stop();
    public void Dispose() => _t.Dispose();
}
