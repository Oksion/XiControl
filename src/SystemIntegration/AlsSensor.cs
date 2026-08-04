using System.Runtime.InteropServices;

namespace XiControl.SystemIntegration;

/// <summary>
/// Датчик освещённости через WinRT LightSensor, активированный РУКАМИ (RoGetActivationFactory)
/// — без проекций Windows SDK: пакетный путь добавил бы Microsoft.Windows.SDK.NET.dll ~25 МБ
/// к 2-МБ exe. IID и порядок методов сняты с системного Windows.Devices.winmd (XIC-30).
///
/// Почему WinRT, а не классический COM Sensor API: наш процесс ВСЕГДА elevated
/// (requireAdministrator), а сенсорный сервис в high-integrity процессе не доставляет ни
/// событий ISensorEvents, ни данных через ISensor.GetData — проверено пробами (без повышения
/// работает, под админом available=true и вечная тишина). WinRT-канал под админом работает.
///
/// Люксы снимает лёгким опросом (GetCurrentReading раз в 1.5 с — локальный вызов, дёшево)
/// выделенный фоновый MTA-поток, живущий до Dispose; дедуп по значению превращает опрос
/// в события. На машине без датчика Start молча не взводится — фича скрыта.
/// </summary>
public sealed class AlsWatcher : IDisposable
{
    private const int PollMs = 1500;
    private const string ClassName = "Windows.Devices.Sensors.LightSensor";

    /// <summary>Освещённость изменилась (фоновый поток!). Отрицательных значений не бывает.</summary>
    public event Action<float>? LuxChanged;

    private Thread? _thread;
    private readonly ManualResetEventSlim _stop = new();
    private volatile bool _started;
    private volatile bool _disposed;

    /// <summary>Есть ли датчик (после Start; до — false).</summary>
    public bool Available => _started;

    /// <summary>Последнее известное значение, лк; NaN — ещё не читалось.</summary>
    public float LastLux { get; private set; } = float.NaN;

    /// <summary>Найти датчик и начать снимать люксы. Идёт в фон: активация и первый вызов
    /// не мгновенны, а зовут нас со старта приложения.</summary>
    public void Start(Action<bool>? ready = null)
    {
        if (_thread is not null || _disposed) return;
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "AlsWatcher" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private void Run(Action<bool>? ready)
    {
        try
        {
            // S_FALSE/CHANGED_MODE не страшны — квартира уже инициализирована CLR
            _ = RoInitialize(1 /* RO_INIT_MULTITHREADED */);

            int hr = WindowsCreateString(ClassName, ClassName.Length, out IntPtr hstr);
            if (hr < 0) { ready?.Invoke(false); return; }
            ILightSensor? sensor;
            try
            {
                var iid = new Guid("45DB8C84-C3A8-471E-9A53-6457FAD87C0E"); // ILightSensorStatics
                hr = RoGetActivationFactory(hstr, ref iid, out ILightSensorStatics? statics);
                if (hr < 0 || statics is null) { ready?.Invoke(false); return; }
                if (statics.GetDefault(out sensor) < 0 || sensor is null)
                {
                    ready?.Invoke(false); // датчика нет — это не ошибка, просто другая машина
                    return;
                }
            }
            finally { _ = WindowsDeleteString(hstr); }

            // Заявляем интервал отчётов — это и есть «клиент заинтересован»: без него сервис
            // через пару минут усыпляет датчик, и GetCurrentReading вечно отдаёт последний кэш
            // (поймано вживую: значение замерло на 815 лк после вспышки фонарика).
            _ = sensor.GetMinimumReportInterval(out uint minMs);
            _ = sensor.PutReportInterval(Math.Max(minMs, 1000));

            _started = true;
            ready?.Invoke(true);

            do
            {
                Poll(sensor);
            }
            while (!_stop.Wait(PollMs)); // поток-хозяин: умрёт он — умрут квартира и RCW
        }
        catch (Exception ex)
        {
            Log.Ex("AlsWatcher", ex); // WinRT мог быть урезан (LTSC-сборки) — деградируем мягко
            ready?.Invoke(false);
        }
    }

    private void Poll(ILightSensor sensor)
    {
        try
        {
            if (sensor.GetCurrentReading(out ILightSensorReading? reading) < 0 || reading is null) return;
            if (reading.GetIlluminanceInLux(out float lux) < 0 || lux < 0) return;
            // дедуп: опрос видит одно и то же значение снова и снова — событие только на смену
            if (!float.IsNaN(LastLux) && Math.Abs(lux - LastLux) < 0.5f) return;
            LastLux = lux;
            if (!_disposed) LuxChanged?.Invoke(lux);
        }
        catch (Exception ex) { Log.Ex("AlsWatcher.Poll", ex); }
    }

    public void Dispose()
    {
        _disposed = true;
        _stop.Set(); // поток-хозяин завершится и отпустит датчик
    }

    // ---- Сырой WinRT-интероп (combase + три интерфейса; порядок методов = vtable из winmd) ----

    [DllImport("combase.dll")]
    private static extern int RoInitialize(int initType);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out ILightSensorStatics? factory);

    // InterfaceIsIInspectable рантайм .NET 5+ не поддерживает (проверено: кидает) — объявляем
    // как IUnknown и падим три слота IInspectable (GetIids/GetRuntimeClassName/GetTrustLevel) руками.

    [ComImport, Guid("45DB8C84-C3A8-471E-9A53-6457FAD87C0E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ILightSensorStatics
    {
        [PreserveSig] int GetIids(out int count, out IntPtr iids);       // IInspectable
        [PreserveSig] int GetRuntimeClassName(out IntPtr name);          // IInspectable
        [PreserveSig] int GetTrustLevel(out int level);                  // IInspectable
        [PreserveSig] int GetDefault([MarshalAs(UnmanagedType.Interface)] out ILightSensor? sensor);
    }

    [ComImport, Guid("F84C0718-0C54-47AE-922E-789F57FB03A0"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ILightSensor
    {
        [PreserveSig] int GetIids(out int count, out IntPtr iids);       // IInspectable
        [PreserveSig] int GetRuntimeClassName(out IntPtr name);          // IInspectable
        [PreserveSig] int GetTrustLevel(out int level);                  // IInspectable
        [PreserveSig] int GetCurrentReading([MarshalAs(UnmanagedType.Interface)] out ILightSensorReading? reading);
        [PreserveSig] int GetMinimumReportInterval(out uint value);      // get_MinimumReportInterval
        [PreserveSig] int PutReportInterval(uint value);                 // put_ReportInterval
        [PreserveSig] int GetReportInterval(out uint value);             // get_ReportInterval
        // add_/remove_ReadingChanged дальше по vtable — не зовём (в elevated хватает опроса)
    }

    [ComImport, Guid("FFDF6300-227C-4D2B-B302-FC0142485C68"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ILightSensorReading
    {
        [PreserveSig] int GetIids(out int count, out IntPtr iids);       // IInspectable
        [PreserveSig] int GetRuntimeClassName(out IntPtr name);          // IInspectable
        [PreserveSig] int GetTrustLevel(out int level);                  // IInspectable
        [PreserveSig] int GetTimestamp(out long universalTime);          // get_Timestamp (DateTime.UniversalTime)
        [PreserveSig] int GetIlluminanceInLux(out float lux);            // get_IlluminanceInLux
    }
}
