using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static XiControl.SystemIntegration.TouchpadHapticsProtocol;

namespace XiControl.SystemIntegration;

/// <summary>
/// Сила вибрации и порог нажатия тачпада BLTP7853 (XIC-77) через его вендорскую коллекцию
/// COL05 — тот же канал, что у Xiaomi PC Manager, без драйверов: обычные
/// <c>HidD_SetOutputReport</c>/<c>HidD_GetInputReport</c>. Байты — в
/// <see cref="TouchpadHapticsProtocol"/>, здесь только HID и тайминги.
///
/// Осторожность намеренная: чужие записи в тачпад уже давали ghost-touch, поэтому шлём только
/// команды из кода Xiaomi, каждую запись проверяем чтением обратно и ничего не пишем без
/// явного выбора пользователя. Коллекцию узнаём не по одному имени, а ещё и по дескриптору
/// (UsagePage 0xFF01, отчёты по 33 байта): на другой модели тот же путь с другим протоколом
/// не должен получить ни байта. Операции последовательные — тачпад отвечает на последний
/// запрос, перемешанные пары запрос/ответ дали бы чужие данные.
/// </summary>
public sealed class TouchpadHaptics
{
    // 120 мс — пауза PC Manager между кадрами; на 10 мс ответ приходит нулевым (проверено)
    private const int SettleMs = 120;
    private const int ReadAttempts = 3;

    private readonly object _lock = new();
    private string? _path;
    private bool _searched;

    /// <summary>Прочитать обе настройки. null — тачпада нет или он не ответил.</summary>
    public TouchpadHapticsState? Read()
    {
        lock (_lock)
        {
            using var h = Open();
            if (h is null) return null;
            var vib = ReadValues(h, CmdVibration, 2);
            var press = ReadValues(h, CmdPressure, 4);
            var slide = ReadValues(h, CmdSlide, 1);
            if (vib is null || press is null || slide is null) return null;
            return new TouchpadHapticsState(MatchVibration(vib[0], vib[1]), press[0], slide[0]);
        }
    }

    /// <summary>Записать силу вибрации; true — тачпад подтвердил значение чтением.</summary>
    public bool SetVibration(HapticsVibration level) =>
        Write(CmdVibration, VibrationValues(level), v => MatchVibration(v[0], v[1]) == level);

    /// <summary>Записать порог нажатия; второе значение прошивка пересчитывает сама,
    /// поэтому сверяем только порог.</summary>
    public bool SetPressure(int threshold) =>
        Write(CmdPressure, PressureValues(threshold), v => v[0] == threshold);

    /// <summary>Сила щелчка краевых ползунков (команда 0x58, XIC-73).</summary>
    public bool SetSlide(int strength) =>
        Write(CmdSlide, [(ushort)strength], v => v[0] == strength);

    /// <summary>Один щелчок мотора (XIC-73). Зовётся на шаге краевого ползунка, поэтому не
    /// ждёт: если в этот момент идёт запись настроек (~0,5 с), щелчок просто пропускается —
    /// жест важнее отклика. false — не отправлен.</summary>
    public bool Pulse()
    {
        if (!Monitor.TryEnter(_lock)) return false;
        try
        {
            using var h = Open();
            return h is not null && Send(h, PulseFrame());
        }
        finally { Monitor.Exit(_lock); }
    }

    private bool Write(byte cmd, ushort[] values, Func<ushort[], bool> confirmed)
    {
        lock (_lock)
        {
            using var h = Open();
            if (h is null) return false;
            if (!Send(h, WriteFrame(cmd, values))) return false;
            Thread.Sleep(SettleMs);
            if (!Send(h, CommitFrame(cmd))) return false;
            Thread.Sleep(SettleMs);
            var back = ReadValues(h, cmd, values.Length);
            if (back is not null && confirmed(back)) return true;
            Log.Write($"TouchpadHaptics: 0x{cmd:X2} не подтвердилась, прочитано {(back is null ? "ничего" : string.Join(",", back))}");
            return false;
        }
    }

    private static ushort[]? ReadValues(SafeFileHandle h, byte cmd, int count)
    {
        // пустой ответ — «ещё не готов», а не отказ: повторяем запрос целиком
        for (int i = 0; i < ReadAttempts; i++)
        {
            if (!Send(h, ReadRequest(cmd, count))) return null;
            Thread.Sleep(SettleMs);
            var r = new byte[FrameLength];
            r[0] = ReportId;
            if (!HidD_GetInputReport(h, r, FrameLength)) continue;
            if (ParseResponse(r, count) is { } v) return v;
        }
        return null;
    }

    private static bool Send(SafeFileHandle h, byte[] frame)
    {
        if (HidD_SetOutputReport(h, frame, FrameLength)) return true;
        Log.Write($"TouchpadHaptics: SetOutputReport 0x{frame[8]:X2} -> {Marshal.GetLastWin32Error()}");
        return false;
    }

    // Хэндл на операцию, как у PC Manager: держать открытым незачем, а после сна/переподключения
    // старый хэндл протухает молча
    private SafeFileHandle? Open()
    {
        if (!_searched) { _path = FindCollection(); _searched = true; }
        if (_path is null) return null;
        var h = CreateFileW(_path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (!h.IsInvalid) return h;
        Log.Write($"TouchpadHaptics: открыть коллекцию не удалось ({Marshal.GetLastWin32Error()})");
        h.Dispose();
        _searched = false; // устройство могло переподключиться под другим путём — поищем снова
        return null;
    }

    private static string? FindCollection()
    {
        HidD_GetHidGuid(out Guid guid);
        IntPtr set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) return null;
        try
        {
            var di = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref di); i++)
            {
                string? path = InterfacePath(set, ref di);
                if (path is null || !path.Contains("bltp7853", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsVendorChannel(path)) return path;
            }
            return null;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private static string? InterfacePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA di)
    {
        SetupDiGetDeviceInterfaceDetailW(set, ref di, IntPtr.Zero, 0, out int size, IntPtr.Zero);
        if (size <= 0) return null;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.WriteInt32(buf, 8); // cbSize SP_DEVICE_INTERFACE_DETAIL_DATA_W на x64
            return SetupDiGetDeviceInterfaceDetailW(set, ref di, buf, size, out _, IntPtr.Zero)
                ? Marshal.PtrToStringUni(buf + 4) : null;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>Вендорская коллекция с нашим протоколом: UsagePage 0xFF01, отчёты по 33 байта.
    /// Открываем без прав доступа — только спросить дескриптор.</summary>
    private static bool IsVendorChannel(string path)
    {
        using var h = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid || !HidD_GetPreparsedData(h, out IntPtr pp)) return false;
        try
        {
            return HidP_GetCaps(pp, out HIDP_CAPS c) == HIDP_STATUS_SUCCESS &&
                c.UsagePage == 0xFF01 && c.OutputReportByteLength == FrameLength && c.InputReportByteLength == FrameLength;
        }
        finally { HidD_FreePreparsedData(pp); }
    }

    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    private const int DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetInputReport(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid g, IntPtr enumerator, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, int index, ref SP_DEVICE_INTERFACE_DATA di);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA di,
        IntPtr detail, int size, out int required, IntPtr devInfo);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
}
