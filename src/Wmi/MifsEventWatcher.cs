namespace XiControl.Wmi;

/// <summary>
/// Подписка на события клавиш прошивки (HID_EVENT20). Отдаёт (code, value):
/// code = EventDetail[1], value = EventDetail[2]. События приходят на потоке пула —
/// подписчик сам маршалит в UI-поток. Первый старт повторяется ограниченно (WMI бывает
/// не готов сразу после входа в сеанс), обрыв потока — переподключение (см. WmiRetry).
/// </summary>
public sealed class MifsEventWatcher : IKeyEventSource
{
    private readonly WmiEventSubscription _sub;

    /// <summary>(code, value) при нажатии.</summary>
    public event Action<byte, byte>? KeyPressed;

    public MifsEventWatcher() =>
        _sub = new WmiEventSubscription(nameof(MifsEventWatcher), Mifs.Namespace, Mifs.EventClass, e =>
        {
            if (e["EventDetail"] is byte[] d && d.Length > 2)
                KeyPressed?.Invoke(d[1], d[2]);
        });

    public void Start() => _sub.Start();

    public void Dispose() => _sub.Dispose();
}
