using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace XiControl.SystemIntegration;

/// <summary>
/// Загрузка NPU — как её показывает диспетчер задач (XIC-86). Windows (WDDM/MCDM) отдаёт работу
/// нейропроцессора в тех же perf-счётчиках, что и GPU: <c>\GPU Engine(*)\Utilization Percentage</c>,
/// экземпляр на пару «процесс × движок», у движков NPU тип <c>engtype_Neural</c>. Driver-free:
/// PDH — обычный user-mode API, ни драйвера, ни прав администратора. Подсмотрено в форке
/// Xiaomi-PC-Manager-Extended, но сводим иначе — см. <see cref="Aggregate"/>.
///
/// Как у <see cref="CpuLoad"/>: значение — среднее за интервал между вызовами, первый вызов после
/// открытия (или <see cref="Reset"/>) только взводит счётчик и возвращает false.
///
/// Открытие запроса недешёвое (~215 мс на TM2424 — PDH разворачивает ~800 экземпляров «GPU
/// Engine»), дальше ~2,6 мс на чтение. Поэтому «Монитор» читает его на пуле, а запрос держит
/// открытым между показами. Вызовы <see cref="TryRead"/> — не больше одного одновременно.
/// </summary>
public sealed class NpuLoad : IDisposable
{
    // Класс устройств ComputeAccelerator — сюда Windows кладёт NPU (Intel AI Boost, Qualcomm
    // Hexagon, AMD XDNA); у каждого установленного — нумерованный подключ 0000, 0001, …
    private const string AcceleratorClass =
        @"SYSTEM\CurrentControlSet\Control\Class\{f01a9d53-3ff6-48d2-9f97-c8a7004be10c}";
    private const string Counter = @"\GPU Engine(*)\Utilization Percentage";
    private const string NeuralSuffix = "_engtype_Neural";

    private IntPtr _query, _counter;
    private volatile bool _primed; // сбрасывает Reset с UI-потока, читает TryRead на пуле
    private bool _off;

    /// <summary>В системе установлен нейропроцессор — под него резервируется ряд.</summary>
    public static bool Present
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(AcceleratorClass);
                return key?.GetSubKeyNames().Any(n => n.Length == 4 && n.All(char.IsAsciiDigit)) == true;
            }
            catch (Exception ex) { Log.Ex("NpuLoad.Present", ex); return false; }
        }
    }

    /// <summary>Забыть базу: следующий вызов начнёт отсчёт заново (повторное открытие виджета),
    /// иначе первое значение размазалось бы по всему времени, что виджет был закрыт. Запрос
    /// не закрываем — переоткрытие стоило бы ~215 мс; следующий вызов лишь взведёт счётчик.
    /// Можно звать с любого потока.</summary>
    public void Reset() => _primed = false;

    public bool TryRead(out float pct)
    {
        pct = float.NaN;
        if (_off) return false;
        try
        {
            if (_query == IntPtr.Zero)
            {
                if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0
                    || PdhAddEnglishCounter(_query, Counter, IntPtr.Zero, out _counter) != 0)
                {
                    Log.Write("NpuLoad: счётчики «GPU Engine» недоступны — ряда NPU не будет");
                    Close();
                    _off = true;
                    return false;
                }
            }
            // экземпляры появляются и исчезают вместе с процессами — «нет данных» в моменте не сбой
            if (PdhCollectQueryData(_query) != 0) return false;
            if (!_primed) { _primed = true; return false; } // rate-счётчику нужны две точки

            pct = Aggregate(ReadInstances());
            return true;
        }
        catch (Exception ex)
        {
            Log.Ex("NpuLoad", ex);
            Close();
            _off = true;
            return false;
        }
    }

    /// <summary>
    /// Свести экземпляры в одно число, как диспетчер задач: внутри движка — сумма по процессам,
    /// между движками — максимум. Форк складывал всё подряд, и у NPU с несколькими движками
    /// сумма завышалась (две половины работы на двух движках — это не 100%).
    /// Имя экземпляра: <c>pid_1234_luid_0x0_0xD1A4_phys_0_eng_0_engtype_Neural</c> — движок
    /// задаёт всё после <c>pid_N_</c>. Чистая функция — под тестами.
    /// </summary>
    internal static float Aggregate(IEnumerable<(string Name, double Value)> instances)
    {
        var perEngine = new Dictionary<string, double>();
        foreach (var (name, value) in instances)
        {
            if (!name.EndsWith(NeuralSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            if (double.IsNaN(value) || value <= 0) continue;
            int luid = name.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
            string engine = luid >= 0 ? name[luid..] : name;
            perEngine[engine] = perEngine.GetValueOrDefault(engine) + value;
        }
        return perEngine.Count == 0 ? 0f : (float)Math.Clamp(perEngine.Values.Max(), 0, 100);
    }

    private List<(string, double)> ReadInstances()
    {
        var list = new List<(string, double)>();
        uint size = 0;
        int st = PdhGetFormattedCounterArray(_counter, PdhFmtDouble, ref size, out _, IntPtr.Zero);
        if (st != PdhMoreData || size == 0) return list; // экземпляров нет вовсе — загрузка 0
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(_counter, PdhFmtDouble, ref size, out uint count, buf) != 0) return list;
            int stride = Marshal.SizeOf<PdhFmtCounterValueItem>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(buf + i * stride);
                if (item.CStatus != 0) continue; // PDH_CSTATUS_VALID_DATA / NEW_DATA = 0
                list.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.DoubleValue));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    private void Close()
    {
        // закрытие при выходе/сбросе: не закрылось — делать с этим нечего, хэндл всё равно забываем
        if (_query != IntPtr.Zero) _ = PdhCloseQuery(_query);
        _query = _counter = IntPtr.Zero;
    }

    public void Dispose() => Close();

    private const uint PdhFmtDouble = 0x00000200;
    private const int PdhMoreData = unchecked((int)0x800007D2);

    // PDH_FMT_COUNTERVALUE_ITEM_W: имя + PDH_FMT_COUNTERVALUE { DWORD CStatus; union { …, double } }.
    // Объединение выровнено по 8 — значение на смещении 8 внутри, поэтому явная раскладка, а не
    // «магическое» 24 из форка: размер берётся из структуры. Смещения — для x64 (проект только x64).
    [StructLayout(LayoutKind.Explicit)]
    private struct PdhFmtCounterValueItem
    {
        [FieldOffset(0)] public IntPtr Name;
        [FieldOffset(8)] public uint CStatus;
        [FieldOffset(16)] public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr items);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr query);
}
