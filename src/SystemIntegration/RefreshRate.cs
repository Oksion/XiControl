using System.Runtime.InteropServices;
using XiControl.Config;

namespace XiControl.SystemIntegration;

/// <summary>
/// Частота обновления ВСТРОЕННОЙ панели ноутбука через ChangeDisplaySettingsEx (чистый Win32,
/// прошивка не нужна). Разрешение и глубина цвета не трогаются; если точной частоты
/// в списке режимов нет — берётся ближайшая поддерживаемая (напр. 90 вместо 120).
///
/// Целимся именно в панель, а не в «дисплей по умолчанию»: `null` в этих API означает
/// ОСНОВНОЙ экран, и стоит пользователю назначить основным внешний монитор, как авто-герцовка
/// начинала бы крутить частоту ему — не экономя батарею и портя картинку там, где не просили
/// (XIC-21). Панель ищется через CCD (<see cref="InternalPanel"/>) при каждом применении:
/// GDI-имена вида \\.\DISPLAY1 перетасовываются при переподключении монитора, поэтому кэш
/// давал бы ровно тот же класс багов.
/// </summary>
public static class RefreshRate
{
    private const int EnumCurrentSettings = -1;
    private const uint DmBitsPerPel = 0x40000, DmPelsWidth = 0x80000, DmPelsHeight = 0x100000, DmDisplayFrequency = 0x400000;
    private const uint CdsUpdateRegistry = 0x1; // сохранить в реестре — частота переживает перезагрузку
    private const int DispChangeSuccessful = 0;

    private static readonly object Sync = new(); // фоновые применения не должны пересекаться

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Devmode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsExW(string deviceName, int modeNum, ref Devmode devMode, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string deviceName, ref Devmode devMode, IntPtr hwnd, uint flags, IntPtr param);

    private static Devmode NewDevmode() => new() { dmSize = (ushort)Marshal.SizeOf<Devmode>() };

    /// <summary>
    /// Применить частоту по текущему питанию (сеть/батарея), если авто-герцовка включена.
    /// Сам переход — в фоновом потоке: смена видеорежима длится до секунд и не должна
    /// держать UI-поток (у видимой панели живёт глобальный хук мыши — Windows молча
    /// снимает хуки, чей поток не отвечает, и закрытие по клику вне панели отвалится).
    /// </summary>
    public static void ApplyForPower(AppConfig cfg)
    {
        if (!cfg.RefreshRateFeature || !cfg.AutoRefreshRate) return; // фича убрана — экран не трогаем
        int hz = PowerLine.IsOnline() ? cfg.AcRefreshRate : cfg.BatteryRefreshRate;
        Task.Run(() =>
        {
            // «панели нет» — штатная ситуация (крышка закрыта, «только второй экран»), про неё
            // ApplyCore уже написал сам; дублировать её как «не удалось» — врать в лог
            if (ApplyCore(hz) == ApplyResult.Failed)
                Log.Write($"RefreshRate: не удалось установить {hz} Гц");
        });
    }

    /// <summary>Какая частота реально включится для hz на встроенной панели: ближайшая
    /// поддерживаемая (null — не определить или панели нет в активных путях).</summary>
    public static int? Resolve(int hz)
    {
        if (hz <= 0) return null;
        try
        {
            if (InternalPanel() is not string panel) return null;
            var cur = NewDevmode();
            if (!EnumDisplaySettingsExW(panel, EnumCurrentSettings, ref cur, 0)) return null;
            int best = Nearest(panel, cur, hz);
            return best == 0 ? null : best;
        }
        catch (Exception ex) { Log.Ex("RefreshRate.Resolve", ex); return null; }
    }

    /// <summary>Поддерживаемые частоты текущего разрешения встроенной панели.
    /// Пустой массив означает, что панель сейчас не активна или драйвер не отдал режимы.</summary>
    public static int[] Supported()
    {
        try
        {
            if (InternalPanel() is not string panel) return [];
            var cur = NewDevmode();
            if (!EnumDisplaySettingsExW(panel, EnumCurrentSettings, ref cur, 0)) return [];
            return SupportedRates(panel, cur);
        }
        catch (Exception ex) { Log.Ex("RefreshRate.Supported", ex); return []; }
    }

    /// <summary>Автопереключение имеет смысл только при наличии хотя бы двух режимов.</summary>
    public static bool SupportsAutomaticSwitching() => HasMultipleRates(Supported());

    internal static bool HasMultipleRates(IEnumerable<int> rates) =>
        rates.Where(x => x > 1).Distinct().Take(2).Count() == 2;

    /// <summary>Установить ближайшую к hz поддерживаемую частоту встроенной панели.
    /// true — установлена (или уже стояла). Можно звать с любого потока; параллельные
    /// вызовы сериализуются.</summary>
    public static bool Apply(int hz) => ApplyCore(hz) == ApplyResult.Ok;

    /// <summary>Переключить встроенную панель на следующую поддерживаемую частоту и вернуть
    /// реально установленное значение. null — панель не активна или смена не удалась.</summary>
    public static int? Cycle()
    {
        lock (Sync)
        {
            try
            {
                if (InternalPanel() is not string panel) return null;
                var cur = NewDevmode();
                if (!EnumDisplaySettingsExW(panel, EnumCurrentSettings, ref cur, 0)) return null;

                var rates = SupportedRates(panel, cur);
                if (NextRate((int)cur.dmDisplayFrequency, rates) is not int next) return null;
                if ((int)cur.dmDisplayFrequency == next) return next;

                cur.dmDisplayFrequency = (uint)next;
                cur.dmFields = DmPelsWidth | DmPelsHeight | DmBitsPerPel | DmDisplayFrequency;
                return ChangeDisplaySettingsExW(panel, ref cur, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero)
                    == DispChangeSuccessful ? next : null;
            }
            catch (Exception ex) { Log.Ex("RefreshRate.Cycle", ex); return null; }
        }
    }

    /// <summary>Следующая частота по возрастанию; после максимальной — минимальная.</summary>
    internal static int? NextRate(int current, IEnumerable<int> supported)
    {
        int[] rates = supported.Where(x => x > 1).Distinct().Order().ToArray();
        if (rates.Length == 0) return null;
        foreach (int rate in rates)
            if (rate > current) return rate;
        return rates[0];
    }

    /// <summary>«Панели нет» — не сбой, а нормальный расклад, и звать его так в логе нельзя:
    /// разбирая чужой log.txt, «не удалось установить» отправит искать несуществующую поломку.</summary>
    private enum ApplyResult { Ok, NoPanel, Failed }

    private static ApplyResult ApplyCore(int hz)
    {
        if (hz <= 0) return ApplyResult.Failed; // мусор из config.json: иначе |f−hz| выберет минимальную частоту
        lock (Sync)
        {
            try
            {
                // панели нет среди активных путей (крышка закрыта / «только второй экран») —
                // менять нечего. Откатываться на «основной экран» нельзя: это вернуло бы XIC-21
                // и увело бы частоту чужому монитору.
                if (InternalPanel() is not string panel)
                {
                    Log.Write("RefreshRate: встроенная панель не активна — частоту не трогаем");
                    return ApplyResult.NoPanel;
                }

                var cur = NewDevmode();
                if (!EnumDisplaySettingsExW(panel, EnumCurrentSettings, ref cur, 0)) return ApplyResult.Failed;

                int best = Nearest(panel, cur, hz);
                if (best == 0) return ApplyResult.Failed;
                if ((int)cur.dmDisplayFrequency == best) return ApplyResult.Ok; // уже стоит — не мигаем экраном

                cur.dmDisplayFrequency = (uint)best;
                cur.dmFields = DmPelsWidth | DmPelsHeight | DmBitsPerPel | DmDisplayFrequency;
                return ChangeDisplaySettingsExW(panel, ref cur, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero) == DispChangeSuccessful
                    ? ApplyResult.Ok : ApplyResult.Failed;
            }
            catch (Exception ex) { Log.Ex("RefreshRate.Apply", ex); return ApplyResult.Failed; }
        }
    }

    // Ближайшая к hz частота среди режимов с текущим разрешением и глубиной цвета (0 — нет ни одной)
    private static int Nearest(string panel, Devmode cur, int hz)
    {
        int best = 0;
        foreach (int f in SupportedRates(panel, cur))
        {
            if (best == 0 || Math.Abs(f - hz) < Math.Abs(best - hz) ||
                (Math.Abs(f - hz) == Math.Abs(best - hz) && f > best)) best = f;
        }
        return best;
    }

    private static int[] SupportedRates(string panel, Devmode cur)
    {
        var rates = new HashSet<int>();
        var probe = NewDevmode();
        for (int i = 0; EnumDisplaySettingsExW(panel, i, ref probe, 0); i++)
        {
            if (probe.dmPelsWidth != cur.dmPelsWidth || probe.dmPelsHeight != cur.dmPelsHeight ||
                probe.dmBitsPerPel != cur.dmBitsPerPel) continue;
            int f = (int)probe.dmDisplayFrequency;
            if (f <= 1) continue; // 0/1 — «аппаратная по умолчанию», не частота
            rates.Add(f);
        }
        return rates.Order().ToArray();
    }

    // ---- Поиск встроенной панели (CCD API, user32; driver-free) ----

    private const uint QdcOnlyActivePaths = 0x2;
    // INTERNAL — именно 0x80000000; 0xFFFFFFFF это OTHER, перепутать легко (проверено на TM2424:
    // единственный активный путь рапортует 0x80000000)
    private const uint OutputInternal = 0x80000000;    // DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL
    private const uint OutputEmbeddedDp = 11;          // ..._DISPLAYPORT_EMBEDDED (так рапортует часть панелей)
    private const uint GetSourceName = 1;              // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
    private const int ErrorSuccess = 0;

    /// <summary>
    /// GDI-имя встроенной панели (\\.\DISPLAY1) или null, если её нет среди активных путей —
    /// крышка закрыта, режим «только второй экран», либо это вовсе не ноутбук. Перечитываем
    /// каждый раз: имена не стабильны между переподключениями монитора.
    /// </summary>
    private static string? InternalPanel()
    {
        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint pathCount, out uint modeCount) != ErrorSuccess)
            return null;

        var paths = new DisplayConfigPathInfo[pathCount];
        var modes = new DisplayConfigModeInfo[modeCount]; // содержимое не нужно, но буфер обязателен
        if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != ErrorSuccess)
            return null;

        for (int i = 0; i < pathCount; i++)
        {
            uint tech = paths[i].targetInfo.outputTechnology;
            if (tech != OutputInternal && tech != OutputEmbeddedDp) continue;

            var name = new DisplayConfigSourceDeviceName
            {
                header = new DisplayConfigDeviceInfoHeader
                {
                    type = GetSourceName,
                    size = (uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>(),
                    adapterId = paths[i].sourceInfo.adapterId,
                    id = paths[i].sourceInfo.id,
                },
            };
            if (DisplayConfigGetDeviceInfo(ref name) == ErrorSuccess && !string.IsNullOrEmpty(name.viewGdiDeviceName))
                return name.viewGdiDeviceName;
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo { public Luid adapterId; public uint id, modeInfoIdx, statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigRational { public uint Numerator, Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        public Luid adapterId;
        public uint id, modeInfoIdx, outputTechnology, rotation, scaling;
        public DisplayConfigRational refreshRate;
        public uint scanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo sourceInfo;
        public DisplayConfigPathTargetInfo targetInfo;
        public uint flags;
    }

    // Содержимое режима нам не нужно — важен только размер (union из target/source-режимов).
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DisplayConfigModeInfo { public uint infoType; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader { public uint type, size; public Luid adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DisplayConfigPathInfo[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DisplayConfigModeInfo[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName requestPacket);
}
