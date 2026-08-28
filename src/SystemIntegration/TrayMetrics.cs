using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;

namespace XiControl.SystemIntegration;

/// <summary>Метрика индикатора в трее (XIC-35). Одновременно показывается одна.</summary>
public enum TrayMetric { Power, Cpu, Gpu, Ram, Temp }

/// <summary>
/// Чистая логика индикатора: парсинг метрики из конфига и компактный текст для значка
/// (в 16 px влезает 2–3 знака — только число, без единиц; единицы — в тултипе).
/// Вынесена из UI ради юнит-тестов.
/// </summary>
public static class TrayMetricFormat
{
    /// <summary>Метрика из строки конфига; null/неизвестное → Power (дефолт).</summary>
    public static TrayMetric ParseKind(string? s) => s?.ToLowerInvariant() switch
    {
        "cpu" => TrayMetric.Cpu,
        "gpu" => TrayMetric.Gpu,
        "ram" => TrayMetric.Ram,
        "temp" => TrayMetric.Temp,
        _ => TrayMetric.Power,
    };

    /// <summary>Ключ конфига/локализации для метрики (обратен <see cref="ParseKind"/>).</summary>
    public static string Key(TrayMetric m) => m switch
    {
        TrayMetric.Cpu => "cpu",
        TrayMetric.Gpu => "gpu",
        TrayMetric.Ram => "ram",
        TrayMetric.Temp => "temp",
        _ => "power",
    };

    /// <summary>
    /// Текст на значке: целое число без знака и единиц («12», «67°», «—» = нет данных).
    /// Единицы — в тултипе: двухэтажный вариант «число + единица» пробовали (приём
    /// TrafficMonitor) — на этом размере значка нижняя строка нечитаема, отказались.
    /// Проценты клэмпятся к 0..100; ватты — по модулю (направление тока — не для трея).
    /// </summary>
    public static string IconText(TrayMetric kind, float value)
    {
        if (float.IsNaN(value)) return "—";
        int n = (int)MathF.Round(MathF.Abs(value));
        return kind switch
        {
            TrayMetric.Temp => Math.Clamp(n, 0, 199).ToString(CultureInfo.InvariantCulture) + "°",
            TrayMetric.Power => Math.Clamp(n, 0, 999).ToString(CultureInfo.InvariantCulture),
            _ => Math.Clamp(n, 0, 100).ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Загрузка CPU из приращений времён GetSystemTimes: kernel включает idle,
    /// поэтому всё время = dBusy, а занятое = dBusy − dIdle.</summary>
    public static float CpuPct(long deltaIdle, long deltaBusy) =>
        deltaBusy > 0 ? Math.Clamp(100f * (deltaBusy - deltaIdle) / deltaBusy, 0f, 100f) : 0f;
}

/// <summary>
/// Загрузка CPU через GetSystemTimes — дешёвый Win32 без perf-counters. Значение — среднее
/// за интервал между вызовами (та же математика, что у «Монитора» до выноса сюда).
/// Первый вызов только запоминает базу и возвращает false.
/// </summary>
public sealed class CpuLoad
{
    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    private long _idle, _kernel, _user;
    private bool _has;

    /// <summary>Забыть базу — следующий вызов начнёт отсчёт заново (повторное открытие виджета).</summary>
    public void Reset() => _has = false;

    public bool TryRead(out float pct)
    {
        pct = float.NaN;
        if (!GetSystemTimes(out long i, out long k, out long u)) return false;
        bool had = _has;
        long dIdle = i - _idle, dBusy = (k - _kernel) + (u - _user);
        (_idle, _kernel, _user, _has) = (i, k, u, true);
        if (!had || dBusy <= 0) return false;
        pct = TrayMetricFormat.CpuPct(dIdle, dBusy);
        return true;
    }
}

/// <summary>Занятая RAM через GlobalMemoryStatusEx — мгновенно и без хэндлов.</summary>
public static class MemoryLoad
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    public static bool TryRead(out float pct, out float usedGb, out float totalGb)
    {
        pct = usedGb = totalGb = float.NaN;
        var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref m)) return false;
        totalGb = m.TotalPhys / 1073741824f;
        usedGb = (m.TotalPhys - m.AvailPhys) / 1073741824f;
        pct = m.MemoryLoad;
        return true;
    }
}

/// <summary>
/// Температура «горячей точки» через Intel DPTF (WMI EsifDeviceInformation) — driver-free:
/// провайдер идёт со штатными драйверами Intel. Максимум среди активных доменов — честная
/// температура самого горячего узла. Класса нет на модели → тихо выключаемся навсегда.
/// (Логика — из «Монитора», вынесена для переиспользования индикатором трея.)
/// </summary>
public sealed class DptfTemperature : IDisposable
{
    private ManagementObjectSearcher? _q;
    private bool _off;

    /// <summary>Класс DPTF на этой модели есть (хотя бы один домен отвечал).</summary>
    public bool Present { get; private set; }

    /// <summary>Максимальная температура доменов, °C; NaN — данных нет (или класс отсутствует).</summary>
    public float ReadMaxC()
    {
        if (_off) return float.NaN;
        try
        {
            _q ??= new ManagementObjectSearcher(@"root\wmi",
                "SELECT Temperature FROM EsifDeviceInformation");
            int max = 0; bool any = false;
            foreach (ManagementObject o in _q.Get())
            {
                any = true;
                object? t = o["Temperature"];
                o.Dispose();
                if (t is null) continue;
                int c = Convert.ToInt32(t, CultureInfo.InvariantCulture);
                if (c > max && c < 130) max = c; // >130 °C — неинициализированный домен, отбрасываем
            }
            if (any) Present = true; else _off = true;
            return max > 0 ? max : float.NaN;
        }
        catch (Exception ex) { Log.Ex("Dptf", ex); _q?.Dispose(); _q = null; _off = true; return float.NaN; }
    }

    public void Dispose() => _q?.Dispose();
}

/// <summary>
/// Мощность CPU package из штатного Windows Energy Meter. На части моделей Xiaomi
/// прошивка не сообщает мощность адаптера/батареи при работе от сети, но Intel RAPL
/// остаётся доступен через этот performance provider. Отрицательный знак сохраняет
/// соглашение PowerDraw: расход отрицательный, заряд батареи положительный.
/// </summary>
public sealed class EnergyMeterPower : IDisposable
{
    private ManagementObjectSearcher? _query;
    private bool _off;

    public float ReadWatts()
    {
        if (_off) return float.NaN;
        try
        {
            _query ??= new ManagementObjectSearcher(@"root\cimv2",
                "SELECT Power FROM Win32_PerfFormattedData_PowerMeterCounter_EnergyMeter " +
                "WHERE Name = 'RAPL_Package0_PKG'");
            foreach (ManagementObject item in _query.Get())
            {
                object? raw = item["Power"];
                item.Dispose();
                if (raw is null) continue;
                return ToConsumptionWatts(Convert.ToUInt64(raw, CultureInfo.InvariantCulture));
            }
            return float.NaN;
        }
        catch (Exception ex)
        {
            Log.Ex("EnergyMeter", ex);
            _query?.Dispose();
            _query = null;
            _off = true;
            return float.NaN;
        }
    }

    internal static float ToConsumptionWatts(ulong milliwatts) =>
        milliwatts > 0 ? -(milliwatts / 1000f) : float.NaN;

    public void Dispose() => _query?.Dispose();
}

/// <summary>
/// Источник значения для индикатора: фасад над PowerDraw/CpuLoad/GpuTelemetry/MemoryLoad/DPTF.
/// Всё лениво: создаётся только внутренность выбранной метрики, остальные не трогаются.
/// NaN = данных нет (значок показывает «—»): от сети без заряда, не-Intel GPU, нет DPTF.
/// </summary>
public sealed class TrayMetricSource : IDisposable
{
    private readonly TrayMetric _kind;
    private PowerDraw? _power;
    private EnergyMeterPower? _energyMeter;
    private CpuLoad? _cpu;
    private GpuTelemetry? _gpu;
    private DptfTemperature? _temp;

    public TrayMetricSource(TrayMetric kind) => _kind = kind;

    public float Read() => _kind switch
    {
        TrayMetric.Cpu => (_cpu ??= new CpuLoad()).TryRead(out float c) ? c : float.NaN,
        TrayMetric.Gpu => ReadGpu(),
        TrayMetric.Ram => MemoryLoad.TryRead(out float r, out _, out _) ? r : float.NaN,
        TrayMetric.Temp => (_temp ??= new DptfTemperature()).ReadMaxC(),
        _ => ReadPower(),
    };

    private float ReadPower()
    {
        _power ??= new PowerDraw();
        if (_power.TryReadWatts(out float watts) && !float.IsNaN(watts)) return watts;
        _energyMeter ??= new EnergyMeterPower();
        return _energyMeter.ReadWatts();
    }

    private float ReadGpu()
    {
        _gpu ??= new GpuTelemetry();
        return _gpu.TryRead(out float load, out _, out _) ? load : float.NaN;
    }

    public void Dispose()
    {
        _power?.Dispose();
        _energyMeter?.Dispose();
        _gpu?.Dispose();
        _temp?.Dispose();
    }
}
