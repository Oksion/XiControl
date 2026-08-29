using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace XiControl.SystemIntegration;

/// <summary>
/// Мгновенная мощность батареи через штатный Battery API (SetupAPI + IOCTL_BATTERY_QUERY_STATUS) —
/// чистый Win32, driver-free. В отличие от WMI BatteryStatus, IOCTL заставляет драйвер опросить ACPI
/// _BST в момент вызова, поэтому значение живое и не «залипает». (WMI отдаёт кэш, который обновляется,
/// только когда _BST дёргает кто-то ещё — например открытый HWiNFO; закрыли — снова застывший кэш.)
/// Rate знаковый: &lt;0 разряд, &gt;0 заряд, мВт. Недоступен → монитор мягко откатывается на WMI.
/// </summary>
public sealed class PowerDraw : IDisposable
{
    private static Guid GUID_DEVICE_BATTERY = new("72631E54-78A4-11D0-BCF7-00AA00B7B32A");

    private const uint DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    private const uint IOCTL_BATTERY_QUERY_TAG = 0x294040, IOCTL_BATTERY_QUERY_STATUS = 0x29404C;
    private const int BATTERY_UNKNOWN_RATE = unchecked((int)0x80000000);
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    private SafeFileHandle? _h;
    private uint _tag;
    private bool _off; // Battery API не заводится на этой машине (нет батареи и т.п.) — больше не пытаемся

    /// <summary>
    /// Последнее чтение вернуло «не знаю» (BATTERY_UNKNOWN_RATE): прошивка не сообщает ток
    /// вовсе. Нулевой ток сюда НЕ попадает — «от сети без заряда» это честный ноль, а не
    /// отсутствие данных, и подменять его чужой величиной нельзя (см. TrayMetricSource).
    /// </summary>
    public bool RateUnknown { get; private set; }

    /// <summary>
    /// Мощность батареи, Вт со знаком (+ заряд, − разряд). NaN = от сети без тока / значение неизвестно.
    /// Возвращает false, только если Battery API недоступен целиком → монитор берёт WMI-фолбэк.
    /// </summary>
    public bool TryReadWatts(out float watts)
    {
        watts = float.NaN;
        RateUnknown = false;
        if (_off) return false;
        try
        {
            if (_h is null || _h.IsInvalid)
            {
                _h = OpenBattery();
                if (_h is null) { _off = true; return false; } // батареи/API нет → WMI-фолбэк навсегда
            }
            if (!QueryTag(out _tag)) { _h.Dispose(); _h = null; return true; } // тег протух → переоткрыть на след. тике

            var wait = new BATTERY_WAIT_STATUS { BatteryTag = _tag };
            if (DeviceIoControl(_h, IOCTL_BATTERY_QUERY_STATUS, ref wait, Marshal.SizeOf<BATTERY_WAIT_STATUS>(),
                    out BATTERY_STATUS st, Marshal.SizeOf<BATTERY_STATUS>(), out _, IntPtr.Zero))
            {
                RateUnknown = st.Rate == BATTERY_UNKNOWN_RATE;
                if (!RateUnknown && st.Rate != 0)
                    watts = st.Rate / 1000f; // мВт → Вт, знак сохранён (+ заряд, − разряд)
            }
            return true;
        }
        catch (Exception ex) { Log.Ex("PowerDraw", ex); _h?.Dispose(); _h = null; _off = true; return false; }
    }

    private bool QueryTag(out uint tag)
    {
        uint timeout = 0; // не ждать — вернуть текущий тег
        return DeviceIoControl(_h!, IOCTL_BATTERY_QUERY_TAG, ref timeout, sizeof(uint),
            out tag, sizeof(uint), out _, IntPtr.Zero) && tag != 0;
    }

    private static SafeFileHandle? OpenBattery()
    {
        IntPtr set = SetupDiGetClassDevs(ref GUID_DEVICE_BATTERY, IntPtr.Zero, IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == INVALID_HANDLE_VALUE) return null;
        try
        {
            var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref GUID_DEVICE_BATTERY, i, ref ifData); i++)
            {
                string? path = GetDevicePath(set, ref ifData);
                if (path is null) continue;
                var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (!h.IsInvalid) return h;
                h.Dispose();
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return null;
    }

    // SP_DEVICE_INTERFACE_DETAIL_DATA имеет переменную длину — зовём дважды (размер, затем данные);
    // путь идёт сразу за DWORD cbSize (смещение 4), значение cbSize на x64 = 8.
    private static string? GetDevicePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA ifData)
    {
        SetupDiGetDeviceInterfaceDetail(set, ref ifData, IntPtr.Zero, 0, out uint size, IntPtr.Zero);
        if (size == 0) return null;
        IntPtr detail = Marshal.AllocHGlobal((int)size);
        try
        {
            Marshal.WriteInt32(detail, 8); // cbSize (x64)
            return SetupDiGetDeviceInterfaceDetail(set, ref ifData, detail, size, out _, IntPtr.Zero)
                ? Marshal.PtrToStringUni(detail + 4)
                : null;
        }
        finally { Marshal.FreeHGlobal(detail); }
    }

    public void Dispose() => _h?.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BATTERY_WAIT_STATUS
    {
        public uint BatteryTag, Timeout, PowerState, LowCapacity, HighCapacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BATTERY_STATUS
    {
        public uint PowerState, Capacity, Voltage;
        public int Rate; // знаковый: + заряд, − разряд, мВт
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA ifData, IntPtr detail, uint detailSize, out uint required, IntPtr devInfo);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref uint inBuf, int inSize, out uint outBuf, int outSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref BATTERY_WAIT_STATUS inBuf, int inSize, out BATTERY_STATUS outBuf, int outSize, out uint returned, IntPtr overlapped);
}
