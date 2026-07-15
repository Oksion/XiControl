using System.Management;

namespace XiControl.SystemIntegration;

/// <summary>
/// Яркость встроенного экрана через WMI (root\wmi, WmiMonitorBrightness*) — это ACPI-подсветка,
/// driver-free: тот же канал, которым яркость крутит сама Windows. На несовместимой панели
/// (внешний монитор, десктоп) вызовы кидают — ловим, логируем, деградируем мягко.
/// </summary>
public static class Brightness
{
    private const string ScopePath = @"root\wmi";

    /// <summary>Текущая яркость 0–100, либо null (панель не отдаёт WMI-яркость).</summary>
    public static int? Get()
    {
        try
        {
            using var s = new ManagementObjectSearcher(ScopePath,
                "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject mo in s.Get())
                using (mo)
                    return Convert.ToInt32(mo["CurrentBrightness"]);
        }
        catch (Exception ex) { Log.Ex("Brightness.Get", ex); }
        return null;
    }

    /// <summary>
    /// Установить яркость (0–100). WMI-вызов может подтормаживать — уводим в фон, как смену
    /// видеорежима. Если нужное значение уже стоит — не трогаем (без лишнего моргания и без
    /// паразитного WmiMonitorBrightnessEvent, который иначе запишется как «пользовательский»).
    /// </summary>
    public static void Apply(int percent)
    {
        int lvl = Math.Clamp(percent, 0, 100);
        Task.Run(() =>
        {
            try
            {
                if (Get() == lvl) return;
                using var s = new ManagementObjectSearcher(ScopePath,
                    "SELECT * FROM WmiMonitorBrightnessMethods");
                foreach (ManagementObject mo in s.Get())
                    using (mo)
                    {
                        try
                        {
                            using var args = mo.GetMethodParameters("WmiSetBrightness");
                            args["Timeout"] = (uint)1;
                            args["Brightness"] = (byte)lvl;
                            mo.InvokeMethod("WmiSetBrightness", args, null);
                        }
                        catch (Exception ex) { Log.Ex("Brightness.Apply.instance", ex); /* внешний монитор и т.п. */ }
                    }
            }
            catch (Exception ex) { Log.Ex("Brightness.Apply", ex); }
        });
    }
}

/// <summary>
/// Подписка на изменение яркости экрана (WmiMonitorBrightnessEvent). Событие приходит на
/// потоке пула — подписчик сам решает, что с ним делать. Переживает рестарт WMI: при обрыве
/// переподключается с задержкой. На панели без поддержки — тихо не стартует (как MifsEventWatcher).
/// </summary>
public sealed class BrightnessWatcher : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private readonly ManagementEventWatcher _watcher;
    private readonly System.Threading.Timer _retry;
    private volatile bool _disposed;
    private volatile bool _everStarted;

    /// <summary>Новая яркость 0–100.</summary>
    public event Action<int>? Changed;

    public BrightnessWatcher()
    {
        var scope = new ManagementScope(@"\\.\root\wmi");
        _watcher = new ManagementEventWatcher(scope, new WqlEventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
        _watcher.EventArrived += OnArrived;
        _watcher.Stopped += OnStopped;
        _retry = new System.Threading.Timer(_ => Start());
    }

    public void Start()
    {
        if (_disposed) return;
        try
        {
            _watcher.Start();
            _everStarted = true;
        }
        catch (Exception ex)
        {
            Log.Ex("BrightnessWatcher.Start", ex);
            // хоть раз стартовали → это обрыв (WMI перезапускается), пробуем снова; если нет —
            // класса событий на этой машине скорее всего нет, не спамим ретраями
            if (_everStarted) _retry.Change(RetryDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnStopped(object sender, StoppedEventArgs e)
    {
        if (_disposed) return;
        _retry.Change(RetryDelay, Timeout.InfiniteTimeSpan);
    }

    private void OnArrived(object sender, EventArrivedEventArgs e)
    {
        try
        {
            if (e.NewEvent["Brightness"] is { } v) Changed?.Invoke(Convert.ToInt32(v));
        }
        catch { /* игнор битых событий */ }
    }

    public void Dispose()
    {
        _disposed = true;
        _retry.Dispose();
        try { _watcher.Stop(); } catch { }
        _watcher.Dispose();
    }
}
