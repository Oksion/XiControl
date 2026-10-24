using System.Runtime.InteropServices;

namespace XiControl.SystemIntegration;

/// <summary>
/// Будильник уже запущенного экземпляра (XIC-106). Вторая копия (мьютекс в Program занят) раньше
/// молча выходила — клик по ярлыку в «Пуске» не давал ничего, кроме UAC. Теперь она сигналит
/// именованное событие, и работающий экземпляр открывает окно настроек. Именованного события
/// хватает: передавать нечего, а pipe или широковещательное сообщение — лишняя механика.
///
/// Мьютекс глобальный, а событие — в пространстве сеанса (<c>Local\</c>): копия, запущенная у
/// другого пользователя при быстром переключении, события не найдёт и честно скажет, что XiControl
/// уже работает, — вместо того чтобы открыть окно на рабочем столе, которого она не видит.
///
/// Создаётся сразу за мьютексом, задолго до готовности UI: auto-reset событие держит сигнал, пока
/// его не заберут, поэтому ранний вызов не теряется — окно откроется, как только TrayApp подпишется.
/// </summary>
public sealed class InstanceWake : IDisposable
{
    private const string EventName = @"Local\XiControlShow";
    private const uint ASFW_ANY = uint.MaxValue;

    private readonly EventWaitHandle _event = new(false, EventResetMode.AutoReset, EventName);
    private RegisteredWaitHandle? _wait;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);

    /// <summary>Подписка на будильник. Колбэк приходит с пула — маршалинг в UI на вызывающем.</summary>
    public void Listen(Action onWake)
    {
        _wait = ThreadPool.RegisterWaitForSingleObject(_event, (_, _) =>
        {
            try { onWake(); }
            catch (Exception ex) { Log.Ex("InstanceWake", ex); }
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>
    /// Вторая копия: разбудить первую. false — в этом сеансе будить некого: работает копия у
    /// другого пользователя или старая версия без будильника.
    /// </summary>
    public static bool Signal()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(EventName);
            // окно поднимет ДРУГОЙ процесс, а на передний план Windows пускает только того, кто
            // получил последний ввод, — это мы: нас только что запустили кликом. Делимся правом,
            // иначе окно откроется позади активного (та же грабля, что в XIC-90)
            _ = AllowSetForegroundWindow(ASFW_ANY);
            return ev.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (Exception ex) { Log.Ex("InstanceWake.Signal", ex); return false; }
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _event.Dispose();
    }
}
