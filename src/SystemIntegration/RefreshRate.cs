using System.Runtime.InteropServices;
using XiControl.Config;

namespace XiControl.SystemIntegration;

/// <summary>
/// Частота обновления основного экрана через ChangeDisplaySettings (чистый Win32,
/// прошивка не нужна). Разрешение и глубина цвета не трогаются; если точной частоты
/// в списке режимов нет — берётся ближайшая поддерживаемая (напр. 90 вместо 120).
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
    private static extern bool EnumDisplaySettingsW(string? deviceName, int modeNum, ref Devmode devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsW(ref Devmode devMode, uint flags);

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
        bool online = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
        int hz = online ? cfg.AcRefreshRate : cfg.BatteryRefreshRate;
        Task.Run(() =>
        {
            if (!Apply(hz))
                Log.Write($"RefreshRate: не удалось установить {hz} Гц");
        });
    }

    /// <summary>Какая частота реально включится для hz: ближайшая поддерживаемая (null — не определить).</summary>
    public static int? Resolve(int hz)
    {
        if (hz <= 0) return null;
        try
        {
            var cur = NewDevmode();
            if (!EnumDisplaySettingsW(null, EnumCurrentSettings, ref cur)) return null;
            int best = Nearest(cur, hz);
            return best == 0 ? null : best;
        }
        catch (Exception ex) { Log.Ex("RefreshRate.Resolve", ex); return null; }
    }

    /// <summary>Установить ближайшую к hz поддерживаемую частоту. true — установлена (или уже стояла).
    /// Можно звать с любого потока; параллельные вызовы сериализуются.</summary>
    public static bool Apply(int hz)
    {
        if (hz <= 0) return false; // мусор из config.json: иначе |f−hz| выберет минимальную частоту
        lock (Sync)
        {
            try
            {
                var cur = NewDevmode();
                if (!EnumDisplaySettingsW(null, EnumCurrentSettings, ref cur)) return false;

                int best = Nearest(cur, hz);
                if (best == 0) return false;
                if ((int)cur.dmDisplayFrequency == best) return true; // уже стоит — не мигаем экраном

                cur.dmDisplayFrequency = (uint)best;
                cur.dmFields = DmPelsWidth | DmPelsHeight | DmBitsPerPel | DmDisplayFrequency;
                return ChangeDisplaySettingsW(ref cur, CdsUpdateRegistry) == DispChangeSuccessful;
            }
            catch (Exception ex) { Log.Ex("RefreshRate.Apply", ex); return false; }
        }
    }

    // Ближайшая к hz частота среди режимов с текущим разрешением и глубиной цвета (0 — нет ни одной)
    private static int Nearest(Devmode cur, int hz)
    {
        int best = 0;
        var probe = NewDevmode();
        for (int i = 0; EnumDisplaySettingsW(null, i, ref probe); i++)
        {
            if (probe.dmPelsWidth != cur.dmPelsWidth || probe.dmPelsHeight != cur.dmPelsHeight ||
                probe.dmBitsPerPel != cur.dmBitsPerPel) continue;
            int f = (int)probe.dmDisplayFrequency;
            if (f <= 1) continue; // 0/1 — «аппаратная по умолчанию», не частота
            if (best == 0 || Math.Abs(f - hz) < Math.Abs(best - hz) ||
                (Math.Abs(f - hz) == Math.Abs(best - hz) && f > best)) best = f;
        }
        return best;
    }
}
