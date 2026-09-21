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

/// <summary>Прод-реализация на System.Threading.Timer: тикает в пуле потоков, Start/Stop
/// потокобезопасны и работают с любого потока. Для логики, живущей вне UI-потока
/// (BrightnessCapGuard: события яркости приходят с пула, WinForms-таймер оттуда не тикает
/// никогда — см. UiTimer ниже). Подписчик Tick сам отвечает за свою потокобезопасность.
///
/// <b>Исключение подписчика ловится здесь и никуда не выпускается.</b> Тик исполняется в потоке
/// пула, а необработанное исключение оттуда убивает процесс целиком — молча, без диалога и без
/// строки в журнале. Так и выглядела жалоба из XIC-63: «сначала перестали работать жесты, потом
/// зависла и закрылась». Ловить в каждом подписчике по отдельности — значит однажды забыть;
/// здесь одна точка на все таймеры пула.</summary>
public sealed class WorkerTimer : IAppTimer
{
    private readonly System.Threading.Timer _t;

    public event Action? Tick;

    public WorkerTimer() => _t = new System.Threading.Timer(_ => Fire());

    private void Fire()
    {
        try { Tick?.Invoke(); }
        catch (Exception ex) { Log.Ex("WorkerTimer.Tick", ex); }
    }

    public int Interval { get; set; } = 100;
    public void Start() => _t.Change(Interval, Interval);
    public void Stop() => _t.Change(Timeout.Infinite, Timeout.Infinite);
    public void Dispose() => _t.Dispose();
}

/// <summary>Прод-реализация поверх System.Windows.Forms.Timer (тикает в UI-потоке).
/// Start — только с потока с насосом сообщений: WinForms-таймер, стартованный с фонового
/// потока (напр. SystemEvents), не тикает никогда. Событийные источники это учитывают —
/// SystemEventsSource маршалит события питания и экрана, TrayApp — клавиши прошивки.</summary>
public sealed class UiTimer : IAppTimer
{
    private readonly System.Windows.Forms.Timer _t = new();

    public event Action? Tick;

    // как и у WorkerTimer, исключение подписчика не выпускаем: в UI-потоке оно всплыло бы
    // диалогом «Необработанное исключение» поверх работы пользователя
    public UiTimer() => _t.Tick += (_, _) =>
    {
        try { Tick?.Invoke(); }
        catch (Exception ex) { Log.Ex("UiTimer.Tick", ex); }
    };

    public int Interval { get => _t.Interval; set => _t.Interval = value; }
    public void Start() => _t.Start();
    public void Stop() => _t.Stop();
    public void Dispose() => _t.Dispose();
}
