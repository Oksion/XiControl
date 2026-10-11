using System.Management;

namespace XiControl.Wmi;

/// <summary>
/// WMI-подписка на класс событий, переживающая сбои по <see cref="WmiRetry"/>: первый старт
/// повторяется ограниченно, обрыв работавшего потока — бессрочно. Общая часть
/// MifsEventWatcher и BrightnessWatcher. События приходят на потоке пула.
/// </summary>
/// <remarks>
/// Неудачный <c>Start()</c> System.Management сначала поднимает <c>Stopped</c> (status =
/// InvalidClass и т.п.) на своём потоке и ждёт обработчик, и только потом бросает. Поэтому
/// никаких блокировок вокруг <c>Start()</c> (обработчик повис бы на ней — проверено) и
/// <c>Stopped</c> считаем обрывом, только если поток событий действительно работал.
/// </remarks>
internal sealed class WmiEventSubscription : IDisposable
{
    private readonly string _name;
    private readonly ManagementEventWatcher _watcher;
    private readonly System.Threading.Timer _retry;
    private volatile bool _disposed, _running, _everStarted;
    private int _starting;      // 1 — Start уже идёт (с таймера и снаружи одновременно не стартуем)
    private int _firstFailures; // трогает только Start, а он не реентерабелен

    public WmiEventSubscription(string name, string ns, string eventClass, Action<ManagementBaseObject> onEvent)
    {
        _name = name;
        _watcher = new ManagementEventWatcher(new ManagementScope(ns), new WqlEventQuery($"SELECT * FROM {eventClass}"));
        _watcher.EventArrived += (_, e) =>
        {
            try { onEvent(e.NewEvent); }
            catch { /* игнор битых событий */ }
        };
        _watcher.Stopped += OnStopped;
        _retry = new System.Threading.Timer(_ => Start());
    }

    public void Start()
    {
        if (_disposed || _running || Interlocked.Exchange(ref _starting, 1) == 1) return;
        try
        {
            _watcher.Start();
            _running = true;
            if (!_everStarted && _firstFailures > 0)
                Log.Write($"{_name}: подписка поднялась с попытки {_firstFailures + 1}");
            _everStarted = true;
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            Log.Ex($"{_name}.Start", ex);
            if (!_everStarted) _firstFailures++;
            if (WmiRetry.NextDelay(_everStarted, _firstFailures) is { } delay) Arm(delay);
            else Log.Write($"{_name}: подписка не поднялась за {WmiRetry.FirstStartAttempts} попыток — без событий до перезапуска");
        }
        finally { Volatile.Write(ref _starting, 0); }
    }

    // поток событий оборвался (например, рестарт winmgmt) — иначе события молча умрут.
    // Stopped от неудачного Start (поток не работал) сюда тоже приходит — его разбирает Start.
    private void OnStopped(object sender, StoppedEventArgs e)
    {
        if (_disposed || !_running) return;
        _running = false;
        Log.Write($"{_name}: поток событий остановлен ({e.Status}), переподключение");
        Arm(WmiRetry.Reconnect);
    }

    private void Arm(TimeSpan delay)
    {
        try { _retry.Change(delay, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* Dispose обогнал ретрай — выходим */ }
    }

    public void Dispose()
    {
        _disposed = true;
        _retry.Dispose();
        try { _watcher.Stop(); } catch { /* WMI мог уже умереть при выходе — не критично */ }
        _watcher.Dispose();
    }
}
