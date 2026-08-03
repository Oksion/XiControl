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

    /// <summary>
    /// Метки наших записей яркости: событие WmiMonitorBrightnessEvent с помеченным значением —
    /// наше (восстановление слота, шаг плавного хода), любое другое — человек. Сравнение по
    /// значению надёжнее окна затишья по времени: WMI-вызовы асинхронные и по таймингу не
    /// выстраиваются, а плавный ход длиннее любого разумного окна (XIC-29).
    /// </summary>
    public static readonly OwnWrites Own = new();

    /// <summary>Текущая яркость 0–100, либо null (панель не отдаёт WMI-яркость).</summary>
    public static int? Get()
    {
        try
        {
            using var s = new ManagementObjectSearcher(ScopePath,
                "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject mo in s.Get())
                using (mo)
                    return Convert.ToInt32(mo["CurrentBrightness"], System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) { Log.Ex("Brightness.Get", ex); }
        return null;
    }

    /// <summary>
    /// Установить яркость (0–100). WMI-вызов может подтормаживать — уводим в фон, как смену
    /// видеорежима. Если нужное значение уже стоит — не трогаем (без лишнего моргания и без
    /// паразитного WmiMonitorBrightnessEvent; метку Own в этом случае тоже не ставим — событие
    /// не придёт, а протухшая метка позже «съела» бы настоящий пользовательский выбор).
    /// </summary>
    public static void Apply(int percent)
    {
        int lvl = Math.Clamp(percent, 0, 100);
        Task.Run(() =>
        {
            try
            {
                if (Get() == lvl) return;
                Own.Note(lvl);
                Set(lvl);
            }
            catch (Exception ex) { Log.Ex("Brightness.Apply", ex); }
        });
    }

    /// <summary>
    /// Плавный ход от <paramref name="from"/> к <paramref name="to"/> шагами по 1% (шкала
    /// непрерывная, проверено на TM2424). Весь путь занимает ~<paramref name="durationMs"/>
    /// независимо от дельты. Идёт в фоне: WmiSetBrightness небыстрый, спуск на 30% — 30 вызовов,
    /// UI-поток их не ждёт. Отмена — пользователь схватил ползунок или сменились условия.
    /// </summary>
    public static void Ramp(int from, int to, int durationMs, CancellationToken ct)
    {
        from = Math.Clamp(from, 0, 100);
        to = Math.Clamp(to, 0, 100);
        int delta = Math.Abs(from - to);
        if (delta == 0) return;
        int interval = Math.Max(30, durationMs / delta); // пол на случай кривого config.json
        int step = to > from ? 1 : -1;
        Own.Note(from); // запоздалое событие исходного уровня во время хода — эхо, не действие человека
        Task.Run(() =>
        {
            try
            {
                for (int v = from + step; ; v += step)
                {
                    if (ct.IsCancellationRequested) return;
                    Own.Note(v);
                    Set(v);
                    if (v == to) return;
                    if (ct.WaitHandle.WaitOne(interval)) return; // сон с мгновенной отменой
                }
            }
            catch (Exception ex) { Log.Ex("Brightness.Ramp", ex); }
        }, ct); // отменили до старта — ход и не начнётся (CA2016)
    }

    // Синхронная запись во все панели (вызывать только с фонового потока).
    private static void Set(int lvl)
    {
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
                catch (Exception ex) { Log.Ex("Brightness.Set.instance", ex); /* внешний монитор и т.п. */ }
            }
    }
}

/// <summary>
/// Учёт значений яркости, выставленных нами недавно. Метка живёт по TTL и НЕ снимается при
/// проверке: WMI-события приходят с потоков пула вразнобой и могут дублироваться, а «съеденная»
/// первой проверкой метка делала бы дубль нашей же записи «пользовательским» — на живом железе
/// это давало ложный протест и замораживало схождение на полпути. TTL короткий: протухшая метка
/// не должна проглотить настоящий пользовательский выбор того же значения. Чистая логика —
/// тестируется с явным временем.
/// </summary>
public sealed class OwnWrites
{
    private const int TtlMs = 10_000;

    private readonly Dictionary<int, long> _until = [];  // значение → тик, до которого метка жива
    private readonly object _lock = new();

    public void Note(int level) => Note(level, Environment.TickCount64);

    public void Note(int level, long nowMs)
    {
        lock (_lock)
        {
            // заодно прибираем протухшее — словарь не растёт бесконечно
            foreach (var k in _until.Where(p => nowMs > p.Value).Select(p => p.Key).ToArray())
                _until.Remove(k);
            _until[level] = nowMs + TtlMs;
        }
    }

    /// <summary>true — событие с этим значением наше (недавно писали его сами).</summary>
    public bool IsOwn(int level) => IsOwn(level, Environment.TickCount64);

    public bool IsOwn(int level, long nowMs)
    {
        lock (_lock) return _until.TryGetValue(level, out long until) && nowMs <= until;
    }
}

/// <summary>
/// Детект адаптивной яркости (ADAPTBRIGHT в активной схеме питания) — чистый Win32 из
/// powrprof.dll, без запуска powercfg. С ней лимит яркости не работает: Windows поднимала бы
/// яркость по датчику, мы — возвращали, получилась бы качель (XIC-29).
/// </summary>
public static class AdaptiveBrightness
{
    private static readonly Guid SubVideo = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid AdaptBright = new("fbd9aa66-9553-4097-ba44-ed6e9d65eab8");

    [System.Runtime.InteropServices.DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr rootKey, out IntPtr scheme);

    [System.Runtime.InteropServices.DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootKey, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);

    [System.Runtime.InteropServices.DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(IntPtr rootKey, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    /// <summary>Включена ли адаптивная яркость для указанного источника питания.
    /// Ошибка чтения (нет датчика, урезанная схема) трактуется как «выключена» —
    /// лучше работающий лимит, чем молча отключённая фича.</summary>
    public static bool IsEnabled(bool ac)
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var p) != 0 || p == IntPtr.Zero) return false;
            try
            {
                var scheme = System.Runtime.InteropServices.Marshal.PtrToStructure<Guid>(p);
                var sub = SubVideo;
                var setting = AdaptBright;
                uint v;
                uint r = ac
                    ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out v)
                    : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out v);
                return r == 0 && v != 0;
            }
            finally { LocalFree(p); }
        }
        catch (Exception ex)
        {
            Log.Ex("AdaptiveBrightness.IsEnabled", ex);
            return false;
        }
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
            if (e.NewEvent["Brightness"] is { } v) Changed?.Invoke(Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture));
        }
        catch { /* игнор битых событий */ }
    }

    public void Dispose()
    {
        _disposed = true;
        _retry.Dispose();
        try { _watcher.Stop(); } catch { /* WMI мог уже умереть при выходе — не критично */ }
        _watcher.Dispose();
    }
}
