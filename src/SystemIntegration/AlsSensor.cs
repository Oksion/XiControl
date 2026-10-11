using System.Runtime.InteropServices;
using Microsoft.Win32;
using XiControl.Config;

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
/// выделенный фоновый MTA-поток, живущий до Dispose. На машине без датчика Start молча не
/// взводится — фича скрыта.
///
/// Датчик стримит только по спросу (XIC-84): пока включена авто-яркость (<see cref="SetDemand"/>)
/// или открыт живой индикатор люксов (<see cref="Watch"/>). Без спроса поток спит на событии
/// без таймаута, подписки и заявленного интервала нет — сенсорный стек не будим. Наличие
/// датчика (<see cref="Available"/>) известно и так — из активации на старте.
///
/// Драйверу не доверяем: после сна он подмешивает в поток нули и «половинки» (XIC-76), поэтому
/// наружу идут не сырые чтения, а уровень, прошедший <see cref="LuxGate"/>. Для разбора таких
/// историй — трассировка сырого потока (<see cref="AlsTrace"/>, поле AlsTrace в config.json).
/// </summary>
public sealed class AlsWatcher : IDisposable
{
    private const int PollMs = 1500;
    private const int HeartbeatPolls = 40; // сводка трассировки раз в ~минуту
    private const string ClassName = "Windows.Devices.Sensors.LightSensor";

    /// <summary>Освещённость изменилась (фоновый поток!). Отрицательных значений не бывает.</summary>
    public event Action<float>? LuxChanged;

    private readonly AppConfig? _cfg;
    private readonly IPowerEvents? _power;
    private readonly LuxGate _gate = new();
    private readonly object _pollLock = new(); // Poll зовут двое: цикл опроса и колбэк события
    private long _lastTs;                      // метка последнего чтения — свежее ли оно (для трассировки)
    private DateTime _lastPollUtc;             // когда читали в последний раз — признак сна (IsSleepGap)
    private int _events, _fresh;               // счётчики сводки трассировки
    private float _tracedLevel = float.NaN;    // последний уровень, о котором написали в трассировку
    private Thread? _thread;
    private readonly ManualResetEventSlim _stop = new();
    private readonly AutoResetEvent _demandChanged = new(false);
    private volatile bool _started;
    private volatile bool _disposed;
    private volatile bool _wanted;    // авто-яркость включена (SetDemand)
    private int _viewers;             // открытые живые индикаторы люксов (Watch)
    private volatile bool _streaming; // подписка и интервал заявлены; иначе запоздалое событие не читаем

    /// <summary>Нужен ли сейчас поток люксов: авто-яркость или хоть один живой индикатор.</summary>
    internal bool Demand => _wanted || Volatile.Read(ref _viewers) > 0;

    /// <summary>Нужен ли свет авто-яркости. Без спроса (и без <see cref="Watch"/>) датчик
    /// не стримит; можно звать до <see cref="Start"/>.</summary>
    public void SetDemand(bool on)
    {
        _wanted = on;
        _demandChanged.Set();
    }

    /// <summary>Держать датчик, пока жива аренда — для живого индикатора люксов в настройках,
    /// когда авто-яркость выключена. Dispose повторно безопасен.</summary>
    public IDisposable Watch()
    {
        Interlocked.Increment(ref _viewers);
        _demandChanged.Set();
        return new Lease(this);
    }

    private sealed class Lease(AlsWatcher owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            Interlocked.Decrement(ref owner._viewers);
            owner._demandChanged.Set();
        }
    }

    /// <summary>Оба параметра опциональны: без конфига нет трассировки, без событий питания —
    /// паузы после сна (тесты собирают watcher голым, COM трогает только Start).</summary>
    public AlsWatcher(AppConfig? cfg = null, IPowerEvents? power = null)
    {
        _cfg = cfg;
        _power = power;
        if (_power is not null) _power.PowerModeChanged += OnPowerModeChanged;
    }

    private bool Tracing => _cfg?.AlsTrace == true;

    // После сна драйвер перезапускает сенсор, а переходный процесс старта — главный подозреваемый
    // в мусоре (XIC-76): даём ему отстояться той же паузой, что и при нашем старте. Событие
    // доходит не всегда (короткий Modern Standby — нет), основной признак сна — IsSleepGap.
    private void OnPowerModeChanged(PowerModes mode)
    {
        if (mode == PowerModes.Resume) lock (_pollLock) WokeLocked("== выход из сна: пауза ворот, свет до сна забыт");
        else if (mode == PowerModes.Suspend && Tracing) lock (_pollLock) AlsTrace.Write("== уход в сон");
    }

    /// <summary>Свет снова неизвестен — машина проснулась (фоновый поток!). Уровень до сна
    /// забыт, следующий придёт, когда ворота подтвердят свежий свет (~6 с).</summary>
    public event Action? LuxLost;

    // Проснулись (событие Resume или пауза между чтениями — что раньше; второе лишь продлит паузу,
    // LuxLost повторно не придёт): свет до сна устарел, сенсор перезапускается — пауза ворот и
    // забыть уровень.
    // Звать под _pollLock. LuxLost — тоже под ним, как и LuxChanged: так подписчик видит события
    // строго по порядку (уровень до сна не догонит «забудь»). Подписчик (авто-яркость) в нас
    // не зовёт — взаимной блокировки нет.
    private void WokeLocked(string trace)
    {
        bool had = !float.IsNaN(LastLux);
        _gate.Restart(Environment.TickCount64);
        _tracedLevel = float.NaN;
        LastLux = float.NaN;
        if (Tracing) AlsTrace.Write(trace);
        if (had && !_disposed) LuxLost?.Invoke();
    }

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

            _ = sensor.GetMinimumReportInterval(out uint minMs);
            var sink = new ReadingSink(() => Poll(sensor, fromEvent: true));
            _started = true;
            ready?.Invoke(true);

            // Поток-хозяин: умрёт он — умрут квартира, RCW и подписка. Спит на «стоп / спрос
            // сменился»; пока стримим — просыпается ещё и на опрос раз в PollMs.
            WaitHandle[] wake = [_stop.WaitHandle, _demandChanged];
            long token = 0;
            int hrSub = -1, polls = 0;
            try
            {
                while (true)
                {
                    if (Demand != _streaming)
                    {
                        if (_streaming) StopStream(sensor, hrSub, token);
                        else { hrSub = StartStream(sensor, sink, minMs, out token); polls = 0; }
                    }
                    if (_streaming)
                    {
                        Poll(sensor, fromEvent: false);
                        if (++polls % HeartbeatPolls == 0) Heartbeat();
                    }
                    if (WaitHandle.WaitAny(wake, _streaming ? PollMs : Timeout.Infinite) == 0) break;
                }
            }
            finally
            {
                if (_streaming) StopStream(sensor, hrSub, token);
                GC.KeepAlive(sink); // CCW должен жить, пока жива подписка
            }
        }
        catch (Exception ex)
        {
            Log.Ex("AlsWatcher", ex); // WinRT мог быть урезан (LTSC-сборки) — деградируем мягко
            ready?.Invoke(false);
        }
    }

    // Датчик СТРИМИТ, только пока у него есть событийный подписчик: одного опроса и
    // заявленного ReportInterval мало — сервис усыпляет сенсор, и GetCurrentReading
    // вечно отдаёт последний кэш (поймано вживую: 815 лк с застывшей меткой времени
    // даже для свежих клиентов). Поэтому: интервал + подписка ReadingChanged; опрос
    // остаётся страховкой на случай, если события в elevated-процессе не доедут.
    private int StartStream(ILightSensor sensor, ReadingSink sink, uint minMs, out long token)
    {
        uint interval = Math.Max(minMs, 1000);
        _ = sensor.PutReportInterval(interval);
        int hr = sensor.AddReadingChanged(sink, out token);
        if (hr < 0) Log.Write($"AlsWatcher: подписка ReadingChanged не удалась (0x{hr:X8}) — остаёмся на опросе");
        lock (_pollLock)
        {
            _gate.Restart(Environment.TickCount64); // наш старт — тоже перезапуск сенсора
            _lastPollUtc = default;                 // пауза без спроса — не сон
            if (Tracing) AlsTrace.Write($"== старт: интервал {interval} мс (мин. {minMs}), подписка {(hr < 0 ? $"НЕ удалась 0x{hr:X8}" : "есть")}");
        }
        _streaming = true;
        return hr;
    }

    // Спрос пропал: отписка и интервал по умолчанию (0) — сенсор может уснуть. Уровень
    // забываем: к следующему включению он устареет, а подписчик (авто-яркость) по LuxLost
    // сбросит своё окно медианы.
    private void StopStream(ILightSensor sensor, int hrSub, long token)
    {
        _streaming = false;
        if (hrSub >= 0) _ = sensor.RemoveReadingChanged(token);
        _ = sensor.PutReportInterval(0);
        lock (_pollLock) WokeLocked("== стоп: спроса нет, подписка снята");
    }

    private void Poll(ILightSensor sensor, bool fromEvent)
    {
        if (!_streaming) return; // запоздалое событие после StopStream
        try
        {
            if (sensor.GetCurrentReading(out ILightSensorReading? reading) < 0 || reading is null) return;
            if (reading.GetIlluminanceInLux(out float lux) < 0 || lux < 0) return;
            _ = reading.GetTimestamp(out long ts); // не отдал — 0: для трассировки не беда

            float level;
            lock (_pollLock)
            {
                // Сон замечаем здесь, под замком, а не в цикле опроса: после разморозки первым
                // может проснуться поток события — и без этой проверки ворота засчитали бы часы
                // сна как «кандидат продержался» и приняли бы свет до сна (поймано трассировкой:
                // «1 лк» при закрытии крышки принят как уровень через 13 с сна).
                DateTime nowUtc = DateTime.UtcNow;
                if (_lastPollUtc != default && IsSleepGap(nowUtc - _lastPollUtc))
                    WokeLocked($"== датчик молчал {(nowUtc - _lastPollUtc).TotalSeconds:0} с — машина спала: пауза ворот, свет до сна забыт");
                _lastPollUtc = nowUtc;

                level = _gate.Accept(Environment.TickCount64, lux);
                bool fresh = ts != _lastTs;
                _lastTs = ts;
                if (fromEvent) _events++;
                if (fresh) _fresh++;
                // копии кэша на ровном свету не пишем — иначе файл распухнет ни о чём
                if (Tracing && (fresh || fromEvent))
                    AlsTrace.Write($"{(fromEvent ? "событие" : "опрос  ")} {lux,8:0.#} лк  ts={Stamp(ts)}{(fresh ? "" : " (копия)")}  → {(float.IsNaN(level) ? "—" : $"{level:0.#}")}");
                // решение ворот видно и тогда, когда его принесла копия (её строку не пишем)
                if (!float.IsNaN(level) && !LuxGate.Same(level, _tracedLevel))
                {
                    if (Tracing) AlsTrace.Write($"== ворота приняли уровень {level:0.#} лк (был {(float.IsNaN(_tracedLevel) ? "—" : $"{_tracedLevel:0.#}")})");
                    _tracedLevel = level;
                }
                if (float.IsNaN(level)) return; // уровня нет: старт или пробуждение, ворота ждут

                // шлём КАЖДЫЙ сэмпл, без дедупа по значению: медианному фильтру авто-яркости нужен
                // равномерный поток (дедуп исказил бы взвешивание по времени) — ворота поток не
                // прореживают, а лишь держат прежний уровень, пока новый не подтвердится.
                // Под замком: иначе уровень до сна, вычисленный за миг до пробуждения, ушёл бы
                // подписчику уже после LuxLost. Подписчик (авто-яркость) в нас не зовёт —
                // взаимной блокировки нет.
                LastLux = level;
                if (!_disposed) LuxChanged?.Invoke(level);
            }
        }
        catch (Exception ex) { Log.Ex("AlsWatcher.Poll", ex); }
    }

    /// <summary>Порог «машина спала»: цикл просыпается раз в 1.5 с, и даже тяжёлый затык
    /// планировщика до 10 с не дотягивает.</summary>
    public const int SleepGapMs = 10_000;

    /// <summary>Спала ли машина между двумя чтениями датчика (опросом или событием — цикл опроса
    /// гарантирует чтение не реже раза в 1.5 с). Свой признак сна, а не
    /// событие Resume: на коротком Modern Standby SystemEvents его не доставляет (проверено
    /// трассировкой на TM2424, XIC-76), а процесс при этом замораживается. Часы — настенные:
    /// они идут при любом виде сна; сдвиг времени синхронизацией даст ложное срабатывание,
    /// но его цена — лишь 3 с паузы ворот.</summary>
    public static bool IsSleepGap(TimeSpan sincePrevPoll) => sincePrevPoll.TotalMilliseconds > SleepGapMs;

    // Сводка раз в минуту: живы ли события после сна — главный вопрос шага 0 XIC-76
    private void Heartbeat()
    {
        lock (_pollLock)
        {
            if (Tracing) AlsTrace.Write($"-- за минуту: событий {_events}, свежих чтений {_fresh}");
            _events = _fresh = 0;
        }
    }

    // DateTime.UniversalTime WinRT = FILETIME (тики с 1601 года, UTC)
    private static string Stamp(long universalTime) =>
        universalTime <= 0 ? "?" : $"{DateTime.FromFileTimeUtc(universalTime).ToLocalTime():HH:mm:ss.fff}";

    public void Dispose()
    {
        _disposed = true;
        if (_power is not null) _power.PowerModeChanged -= OnPowerModeChanged;
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
        [PreserveSig] int AddReadingChanged(                             // add_ReadingChanged
            [MarshalAs(UnmanagedType.Interface)] IReadingChangedHandler handler, out long token);
        [PreserveSig] int RemoveReadingChanged(long token);              // remove_ReadingChanged
    }

    /// <summary>ITypedEventHandler&lt;LightSensor, LightSensorReadingChangedEventArgs&gt; —
    /// параметризованный IID посчитан по алгоритму WinRT pinterface (UUID v5 от сигнатуры).
    /// WinRT-делегат наследует IUnknown (не IInspectable) — слотов-паддингов нет.</summary>
    [ComImport, Guid("1ECF183A-9F0A-5F73-9225-5A33EAB5594F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IReadingChangedHandler
    {
        [PreserveSig] int Invoke(IntPtr sender, IntPtr args);
    }

    // Приёмник события: аргументы не разбираем — свежее значение возьмёт тот же Poll
    private sealed class ReadingSink(Action onReading) : IReadingChangedHandler
    {
        public int Invoke(IntPtr sender, IntPtr args)
        {
            try { onReading(); } catch { /* колбэк в чужой поток — не роняем */ }
            return 0;
        }
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

/// <summary>
/// Ворота потока люксов (XIC-76): наружу идёт не сырое чтение, а уровень, который ПРОДЕРЖАЛСЯ.
///
/// Драйвер датчика на TM2424 после сна подмешивает в поток нули и «половинки» (проба: 181 → 92 →
/// 0 → 181, у каждого своя свежая метка времени, мусор живёт 1–2 с). Медиана «инерции» тут
/// бессильна: мусора набегает около половины окна, она прыгает между 0 и 180, экран мигает.
///
/// Правило: <b>отклонение от текущего уровня принимается, только если продержалось
/// <see cref="ConfirmMs"/></b>. Подтверждать надо именно временем, а не числом чтений: датчик
/// выдаёт новое чтение лишь при смене света, и в погасшей комнате придёт одно «0», а дальше —
/// копии с замершей меткой. Эти копии и есть подтверждение («значение всё ещё такое»), поэтому
/// ворота смотрят на значение, а не на метку. Порог по малости значения (как MIN_PLAUSIBLE_LUX
/// у micontrol) не годится — честные 0 лк в тёмной комнате бывают.
///
/// Пока отклонение не подтверждено, наружу отдаётся ПРЕЖНИЙ уровень: медианное окно
/// авто-яркости взвешивает свет по времени (docs/13 §8), и дырки в потоке обесценили бы
/// инерцию. После (пере)запуска сенсора — наш старт, выход из сна — ворота <see cref="SettleMs"/>
/// не принимают ничего: переходный процесс старта даёт мусор (ночная трассировка: 20–32 лк при
/// закрытой крышке, 836 → 457 → 10 лк при открытии). И забывают уровень до сна — он устарел
/// (закрыли крышку в темноте, открыли утром): до первого подтверждённого свежего уровня наружу
/// не идёт ничего.
///
/// Время передаётся снаружи (мс, монотонно) — ворота чистые, под тестами.
/// </summary>
public sealed class LuxGate
{
    /// <summary>Сколько отклонение должно продержаться. Самый долгий мусор в пробе — 2 с.</summary>
    public const int ConfirmMs = 3000;

    /// <summary>Пауза после (пере)запуска сенсора.</summary>
    public const int SettleMs = 3000;

    // ±25 % — порядок гистерезиса авто-яркости по умолчанию (0.1 в лог-шкале ≈ 26 %): мельче —
    // дрожание датчика, а не смена условий. Пол в 1 лк держит околонулевые значения.
    private const float Tolerance = 0.25f;

    private float _level = float.NaN;     // принятый уровень
    private float _candidate = float.NaN; // отклонение, ждущее подтверждения
    private long _since;                  // с какого момента кандидат держится
    private long _quietUntil = long.MinValue;

    /// <summary>Считаем ли два чтения «одним и тем же светом».</summary>
    public static bool Same(float a, float b) =>
        Math.Abs(a - b) <= Math.Max(1f, Tolerance * Math.Max(a, b));

    /// <summary>Сенсор (пере)запущен: <see cref="SettleMs"/> чтения не принимаются, уровень и
    /// кандидат забыты — до первого подтверждённого свежего уровня <see cref="Accept"/> отдаёт NaN.</summary>
    public void Restart(long nowMs)
    {
        _quietUntil = nowMs + SettleMs;
        _level = float.NaN;
        _candidate = float.NaN;
    }

    /// <summary>Пропустить чтение. Возврат — уровень для потребителей (само чтение либо
    /// удержанный прежний); NaN — ни один уровень ещё не принят.</summary>
    public float Accept(long nowMs, float lux)
    {
        if (nowMs < _quietUntil) return _level;

        if (!float.IsNaN(_level) && Same(lux, _level))
        {
            _candidate = float.NaN;
            return _level = lux;              // свет прежний — плавный дрейф идёт без задержки
        }
        if (float.IsNaN(_candidate) || !Same(lux, _candidate))
        {
            _candidate = lux;                 // новое отклонение: засекаем, наружу — прежний
            _since = nowMs;
            return _level;
        }
        _candidate = lux;
        if (nowMs - _since < ConfirmMs) return _level;

        _candidate = float.NaN;               // продержалось — свет правда изменился
        return _level = lux;
    }
}

/// <summary>
/// Трассировка сырого потока датчика в als-trace.txt рядом с log.txt (диагностика XIC-76,
/// включается полем AlsTrace в config.json). Отдельный файл, а не log.txt: при мусоре поток
/// идёт ~раз в секунду и за час вытеснил бы из общего лога всё остальное.
/// </summary>
internal static class AlsTrace
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Sync = new();

    private static string Path_ => Path.Combine(AppPaths.DataDir, "als-trace.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                string path = Path_;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // ротация переименованием, а не удалением: свежий сон не должен пропасть целиком
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Move(path, Path.ChangeExtension(path, ".old.txt"), overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch { /* диагностика не должна ронять приложение */ }
    }
}
