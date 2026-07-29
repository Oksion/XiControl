using System.Runtime.InteropServices;

namespace XiControl.SystemIntegration;

/// <summary>
/// Телеметрия встроенного GPU через Intel IGCL — user-mode API графического драйвера
/// (<c>ControlLib.dll</c> приезжает со штатным драйвером Intel и лежит в System32). Мы ничего не
/// устанавливаем и не требуем прав администратора — это тот же driver-free принцип, что и WMI.
/// Отдаём загрузку %, мощность Вт и частоту МГц: температуры и обороты вентиляторов IGCL на
/// встроенном GPU не даёт (все <c>ctlEnum*</c> отвечают отказом — это пути для дискретных Arc).
///
/// Загрузка и мощность — приращения накопительных счётчиков (секунды активности и джоули),
/// поэтому первый вызов только запоминает базу и возвращает false. Инициализация ленивая: пока
/// «Монитор» не открыли, IGCL в процесс не подгружается вообще.
///
/// Нет драйвера Intel (в т.ч. AMD-модели) или структура телеметрии разложена иначе →
/// выключаемся навсегда, <see cref="Available"/> остаётся false, «Монитор» просто не рисует ряд.
/// </summary>
public sealed class GpuTelemetry : IDisposable
{
    // ctl_power_telemetry_t на ControlLib 1.2.291.0; проверено на TM2424 (Intel, 0x8086:0xB080).
    // Внутри — цепочка ctl_oc_telemetry_item_t: uint bSupported; uint units; uint type; pad; double value.
    private const int TelemetrySize = 512;
    private const int OffTime = 8;      // timeStamp, секунды
    private const int OffEnergy = 32;   // gpuEnergyCounter, джоули
    private const int OffClock = 80;    // gpuCurrentClockFrequency, МГц
    private const int OffActive = 128;  // globalActivityCounter, секунды активности
    private const int ValueOffset = 16; // double внутри элемента

    // ctl_units_t / ctl_data_type_t — сверяем на старте, чтобы не считать мусор при другой раскладке
    private const uint UnitsMHz = 0, UnitsJoules = 6, UnitsSeconds = 7, TypeDouble = 9;

    private const int InitArgsSize = 36; // ctl_init_args_t: Size, Version, AppVersion, flags, SupportedVersion, GUID
    private const int AppVersion = 1 << 16; // ctl_version_info_t — это uint32 (major << 16 | minor), т.е. 1.0

    private IntPtr _api, _dev, _buf;
    private bool _off;      // IGCL не поднялся / раскладка не та — больше не пытаемся
    private bool _tried;    // инициализацию уже делали (успешно или нет)

    private double _prevTime, _prevEnergy, _prevActive;
    private bool _hasPrev;

    /// <summary>IGCL поднялся и телеметрия отдаёт нужные поля (ряд GPU в «Мониторе» имеет смысл).</summary>
    public bool Available => !_off && _tried;

    /// <summary>Забыть базу — следующий вызов начнёт отсчёт заново (при повторном открытии виджета,
    /// иначе первое значение размажется по всему времени, что «Монитор» был закрыт).</summary>
    public void Reset() => _hasPrev = false;

    /// <summary>
    /// Загрузка GPU (0..100), мощность (Вт) и текущая частота (МГц). false — телеметрия недоступна
    /// либо это первая выборка и разностей ещё нет; значения в этом случае не осмысленны.
    /// </summary>
    public bool TryRead(out float loadPct, out float watts, out float mhz)
    {
        loadPct = watts = mhz = float.NaN;
        if (!_tried) Init();
        if (_off) return false;

        try
        {
            if (!Fetch()) { _off = true; return false; }

            double t = Value(OffTime), energy = Value(OffEnergy), active = Value(OffActive);
            mhz = (float)Value(OffClock);

            if (!_hasPrev)
            {
                (_prevTime, _prevEnergy, _prevActive) = (t, energy, active);
                _hasPrev = true;
                return false; // база есть, разности будут со следующего тика
            }

            double dt = t - _prevTime;
            if (dt <= 0) return false; // счётчик времени не двинулся (или перезапустился) — тик пропускаем

            loadPct = (float)Math.Clamp((active - _prevActive) / dt * 100.0, 0.0, 100.0);
            watts = (float)Math.Max((energy - _prevEnergy) / dt, 0.0);
            (_prevTime, _prevEnergy, _prevActive) = (t, energy, active);
            return true;
        }
        catch (Exception ex) { Log.Ex("GpuTelemetry.Read", ex); _off = true; return false; }
    }

    private void Init()
    {
        _tried = true;
        try
        {
            IntPtr args = Marshal.AllocHGlobal(InitArgsSize);
            try
            {
                Zero(args, InitArgsSize);
                Marshal.WriteInt32(args, 0, InitArgsSize);
                Marshal.WriteInt32(args, 8, AppVersion);
                if (ctlInit(args, out _api) != 0) { _off = true; return; }
            }
            finally { Marshal.FreeHGlobal(args); }

            uint count = 0;
            if (ctlEnumerateDevices(_api, ref count, null) != 0 || count == 0) { Close(); return; }
            var devices = new IntPtr[count];
            if (ctlEnumerateDevices(_api, ref count, devices) != 0) { Close(); return; }
            _dev = devices[0]; // встроенный GPU — единственный адаптер на наших моделях

            _buf = Marshal.AllocHGlobal(TelemetrySize);
            if (!Fetch() || !LayoutMatches()) { Close(); return; }
        }
        catch (Exception ex) { Log.Ex("GpuTelemetry.Init", ex); Close(); } // нет ControlLib.dll (не Intel) и т.п.
    }

    /// <summary>Поля лежат там, где мы их ждём, и поддержаны железом — иначе цифрам верить нельзя.</summary>
    private bool LayoutMatches() =>
        Item(OffTime, UnitsSeconds) && Item(OffEnergy, UnitsJoules) &&
        Item(OffClock, UnitsMHz) && Item(OffActive, UnitsSeconds);

    private bool Item(int off, uint units) =>
        (uint)Marshal.ReadInt32(_buf, off) == 1 &&
        (uint)Marshal.ReadInt32(_buf, off + 4) == units &&
        (uint)Marshal.ReadInt32(_buf, off + 8) == TypeDouble;

    private bool Fetch()
    {
        Zero(_buf, TelemetrySize);
        Marshal.WriteInt32(_buf, 0, TelemetrySize);
        return ctlPowerTelemetryGet(_dev, _buf) == 0;
    }

    private double Value(int off) => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(_buf, off + ValueOffset));

    private static void Zero(IntPtr p, int len)
    {
        for (int i = 0; i < len; i++) Marshal.WriteByte(p, i, 0);
    }

    private void Close()
    {
        _off = true;
        if (_buf != IntPtr.Zero) { Marshal.FreeHGlobal(_buf); _buf = IntPtr.Zero; }
        if (_api != IntPtr.Zero)
        {
            try
            {
                int r = ctlClose(_api);
                if (r != 0) Log.Write($"GpuTelemetry: ctlClose -> 0x{r:X8}");
            }
            catch (Exception ex) { Log.Ex("GpuTelemetry.Close", ex); }
            _api = IntPtr.Zero;
        }
        _dev = IntPtr.Zero;
    }

    public void Dispose() => Close();

    private const string Lib = "ControlLib.dll";
    [DllImport(Lib)] private static extern int ctlInit(IntPtr args, out IntPtr api);
    [DllImport(Lib)] private static extern int ctlClose(IntPtr api);
    [DllImport(Lib)] private static extern int ctlEnumerateDevices(IntPtr api, ref uint count, IntPtr[]? devices);
    [DllImport(Lib)] private static extern int ctlPowerTelemetryGet(IntPtr device, IntPtr telemetry);
}
