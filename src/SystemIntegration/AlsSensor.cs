using System.Runtime.InteropServices;

namespace XiControl.SystemIntegration;

/// <summary>
/// Датчик освещённости через штатный Sensor API (COM, sensorsapi) — driver-free: тот же
/// канал, которым пользуется адаптивная яркость самой Windows. WinRT-путь отвергнут
/// осознанно: проекции тянут Microsoft.Windows.SDK.NET.dll (~25 МБ) — раздули бы лёгкий
/// framework-dependent exe на порядок (XIC-30).
///
/// Люксы приходят СОБЫТИЕМ (ISensorEvents.OnDataUpdated) — без опроса, как мы любим.
/// Вся COM-работа идёт на MTA-потоках пула: инициализация в Task.Run, колбэки сенсорного
/// сервиса приходят на RPC-потоках — STA главного потока не участвует, маршалинг не нужен.
/// На машине без датчика (десктоп, другая модель) Start молча не взводится — фича скрыта.
/// </summary>
public sealed class AlsWatcher : IDisposable
{
    /// <summary>Свежие люксы с датчика (поток пула!). Отрицательных не бывает.</summary>
    public event Action<float>? LuxChanged;

    private ISensor? _sensor;
    private SensorSink? _sink;   // держим ссылку: COM видит только IUnknown, GC — только нас
    private volatile bool _started;
    private volatile bool _disposed;

    /// <summary>Есть ли датчик (после Start; до — false).</summary>
    public bool Available => _started;

    /// <summary>Последнее известное значение, лк; NaN — ещё не было события.</summary>
    public float LastLux { get; private set; } = float.NaN;

    /// <summary>Найти датчик и подписаться. Идёт в фон: CoCreateInstance + RPC к сенсорному
    /// сервису не мгновенны, а вызывают нас со старта приложения.</summary>
    public void Start(Action<bool>? ready = null) => Task.Run(() =>
    {
        try
        {
            var manager = (ISensorManager)new SensorManagerComObject();
            int hr = manager.GetSensorsByType(SensorGuids.TypeAmbientLight, out ISensorCollection? sensors);
            if (hr < 0 || sensors is null || sensors.GetCount(out uint n) < 0 || n == 0)
            {
                ready?.Invoke(false); // датчика нет — это не ошибка, просто другая машина
                return;
            }

            // на TM2424 в коллекции ДВА ALS-датчика — берём первый в состоянии READY (0)
            for (uint i = 0; i < n && _sensor is null; i++)
                if (sensors.GetAt(i, out ISensor? s) >= 0 && s is not null &&
                    s.GetState(out int state) >= 0 && state == 0)
                    _sensor = s;
            if (_sensor is null) { ready?.Invoke(false); return; }

            // интерес только к данным — состояние/уход переживём без событий
            var dataUpdated = SensorGuids.EventDataUpdated;
            _sensor.SetEventInterest(new[] { dataUpdated }, 1);
            _sink = new SensorSink(OnLux);
            _sensor.SetEventSink(_sink);

            // Стартовое значение придёт СОБЫТИЕМ: сервис пушит текущее при подписке (проверено
            // на железе). GetData до раскрутки датчика отдаёт отчёт без поля люксов
            // (ERROR_NOT_FOUND) — пробуем, но не рассчитываем.
            if (_sensor.GetData(out ISensorDataReport? report) >= 0 && report is not null)
                ReadLux(report);

            _started = true;
            ready?.Invoke(true);
        }
        catch (Exception ex)
        {
            Log.Ex("AlsWatcher.Start", ex); // сервис сенсоров мог быть выключен — деградируем мягко
            ready?.Invoke(false);
        }
    });

    private void ReadLux(ISensorDataReport report)
    {
        var key = SensorGuids.DataLightLevelLux;
        if (report.GetSensorValue(ref key, out PropVariant v) < 0) return;
        try
        {
            // датчик отдаёт VT_R4; чужая прошивка может прислать иначе — не падаем
            if (v.TryGetFloat(out float lux) && lux >= 0)
            {
                LastLux = lux;
                LuxChanged?.Invoke(lux);
            }
        }
        finally { v.Clear(); }
    }

    private void OnLux(ISensorDataReport report)
    {
        if (_disposed) return;
        try { ReadLux(report); }
        catch (Exception ex) { Log.Ex("AlsWatcher.OnLux", ex); }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _sensor?.SetEventSink(null); } catch { /* сервис мог уже уйти */ }
        _sink = null;
        _sensor = null;
    }

    // ---- COM-интероп Sensor API (только то, что зовём; остальные слоты держат vtable) ----

    [ComImport, Guid("77A1C827-FCD2-4689-8915-9D613CC5FA3E")]
    internal class SensorManagerComObject { }

    [ComImport, Guid("BD77DB67-45A8-42DC-8D00-6DCF15F8377A"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISensorManager
    {
        [PreserveSig] int GetSensorsByCategory(in Guid category, out ISensorCollection? sensors);
        [PreserveSig] int GetSensorsByType(in Guid type, out ISensorCollection? sensors);
        [PreserveSig] int GetSensorByID(in Guid id, out ISensor? sensor);
        [PreserveSig] int SetEventSink(IntPtr events);
        [PreserveSig] int RequestPermissions(IntPtr hwnd, ISensorCollection sensors, bool modal);
    }

    [ComImport, Guid("23571E11-E545-4DD8-A337-B89BF44B10DF"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISensorCollection
    {
        [PreserveSig] int GetAt(uint index, out ISensor? sensor);
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Add(ISensor sensor);
        [PreserveSig] int Remove(ISensor sensor);
        [PreserveSig] int RemoveByID(in Guid id);
        [PreserveSig] int Clear();
    }

    [ComImport, Guid("5FA08F80-2657-458E-AF75-46F73FA6AC5C"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISensor
    {
        [PreserveSig] int GetID(out Guid id);
        [PreserveSig] int GetCategory(out Guid category);
        [PreserveSig] int GetSensorType(out Guid type); // имя своё (слот тот же): GetType прятал бы object.GetType — CS0108
        [PreserveSig] int GetFriendlyName([MarshalAs(UnmanagedType.BStr)] out string name);
        [PreserveSig] int GetProperty(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int GetProperties(IntPtr keys, out IntPtr values);
        [PreserveSig] int GetSupportedDataFields(out IntPtr keys);
        [PreserveSig] int SetProperties(IntPtr values, out IntPtr results);
        [PreserveSig] int SupportsDataField(ref PropertyKey key, out short supported);
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetData(out ISensorDataReport? report);
        [PreserveSig] int SupportsEvent(in Guid eventGuid, out short supported);
        [PreserveSig] int GetEventInterest(out IntPtr values, out uint count);
        [PreserveSig] int SetEventInterest([MarshalAs(UnmanagedType.LPArray)] Guid[]? values, uint count);
        [PreserveSig] int SetEventSink(ISensorEvents? events);
    }

    [ComImport, Guid("0AB9DF9B-C4B5-4796-8898-0470706A2E1D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISensorDataReport
    {
        [PreserveSig] int GetTimestamp(out SystemTime time);
        [PreserveSig] int GetSensorValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int GetSensorValues(IntPtr keys, out IntPtr values);
    }

    [ComImport, Guid("5D8DCC91-4641-47E7-B7C3-B74F48A6C391"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISensorEvents
    {
        [PreserveSig] int OnStateChanged(ISensor sensor, int state);
        [PreserveSig] int OnDataUpdated(ISensor sensor, ISensorDataReport report);
        [PreserveSig] int OnEvent(ISensor sensor, in Guid eventId, IntPtr values);
        [PreserveSig] int OnLeave(in Guid sensorId);
    }

    // Приёмник событий: .NET-объект агилен для COM, колбэки приходят на RPC-потоках пула
    private sealed class SensorSink(Action<ISensorDataReport> onData) : ISensorEvents
    {
        public int OnStateChanged(ISensor sensor, int state) => 0;
        public int OnDataUpdated(ISensor sensor, ISensorDataReport report) { onData(report); return 0; }
        public int OnEvent(ISensor sensor, in Guid eventId, IntPtr values) => 0;
        public int OnLeave(in Guid sensorId) => 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey(Guid fmtid, uint pid)
    {
        public Guid Fmtid = fmtid;
        public uint Pid = pid;
    }

    /// <summary>Минимальный PROPVARIANT: нам нужен только float (VT_R4), остальное — Clear.</summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public float R4;
        [FieldOffset(8)] public double R8;
        [FieldOffset(8)] public uint U4;

        private const ushort VtR4 = 4, VtR8 = 5, VtU4 = 19, VtI4 = 3;

        public readonly bool TryGetFloat(out float value)
        {
            switch (Vt)
            {
                case VtR4: value = R4; return true;
                case VtR8: value = (float)R8; return true;
                case VtU4 or VtI4: value = U4; return true;
                default: value = 0; return false;
            }
        }

        public void Clear() => _ = PropVariantClear(ref this); // hr освобождения некритичен (CA1806)

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant pvar);
    }

    internal static class SensorGuids
    {
        /// <summary>SENSOR_TYPE_AMBIENT_LIGHT — он же виден в DeviceId WinRT-датчика.</summary>
        public static readonly Guid TypeAmbientLight = new("97F115C8-599A-4153-8894-D2D12899918A");

        /// <summary>SENSOR_EVENT_DATA_UPDATED.</summary>
        public static readonly Guid EventDataUpdated = new("2ED0F2A4-0087-41D3-87DB-67AA5ECAEF94");

        /// <summary>SENSOR_DATA_TYPE_LIGHT_LEVEL_LUX (fmtid общий для light-данных, pid 2).</summary>
        public static PropertyKey DataLightLevelLux =>
            new(new Guid("E4C77CE2-DCB7-46E9-8439-4FEC548833A6"), 2);
    }
}
