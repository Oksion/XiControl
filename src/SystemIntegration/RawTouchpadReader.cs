using System.Runtime.InteropServices;

namespace XiControl.SystemIntegration;

/// <summary>
/// Сырые касания Precision Touchpad через Raw Input (XIC-61). Driver-free: штатный Win32 плюс
/// разбор HID через <c>hid.dll</c>, права администратора не нужны — проверено пробником
/// (<c>reference/touchpad-edges</c>), он работал обычным процессом.
///
/// Живёт на СВОЁМ потоке с окном-только-для-сообщений: Raw Input требует HWND и цикла сообщений,
/// а вешать это на UI-поток нельзя — поток касаний идёт сотнями отчётов в секунду и мешал бы
/// панели и OSD. Наружу отдаётся готовый кадр контактов в долях панели; решение о жесте —
/// <see cref="TouchpadEdgeGesture"/>, оно чистое и под тестами.
///
/// <b>Координаты — доли, а не миллиметры.</b> Физические единицы PTP объявляются в дюймах
/// с показателем (на TM2424 <c>Units=0x13</c>, <c>UnitExponent=-2</c>), и трактовка «на глазок»
/// однажды уже дала 54,7 мм вместо 139. Доля от логического диапазона от этого не зависит.
///
/// Тачпада нет или коллекция не PTP — тихо не стартуем и пишем строку в журнал: молчащая фича
/// без следа выглядит как поломка при разборе чужого log.txt.
/// </summary>
public sealed class RawTouchpadReader : IDisposable
{
    private const int WM_INPUT = 0x00FF, WM_CLOSE = 0x0010, WM_DESTROY = 0x0002;
    private const uint RIDEV_INPUTSINK = 0x00000100, RIDEV_REMOVE = 0x00000001;
    private const uint RID_INPUT = 0x10000003, RIM_TYPEHID = 2, RIDI_PREPARSEDDATA = 0x20000005;
    private const ushort PageGeneric = 0x01, UsageX = 0x30, UsageY = 0x31;
    private const ushort PageDigitizer = 0x0D, UsageTouchPad = 0x05, UsageTipSwitch = 0x42, UsageContactId = 0x51;
    private const int HidpSuccess = 0x00110000;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly Action<IReadOnlyList<TouchContact>> _onFrame;
    private readonly Dictionary<IntPtr, DeviceInfo> _devices = [];
    private readonly List<TouchContact> _frame = [];
    private readonly byte[] _buffer = new byte[8192];

    private Thread? _thread;
    private IntPtr _hwnd;
    private WndProc? _proc;          // держим ссылку: GC не должен собрать делегат под окном
    private volatile bool _stopping;

    public RawTouchpadReader(Action<IReadOnlyList<TouchContact>> onFrame) => _onFrame = onFrame;

    /// <summary>Читаем ли мы сейчас касания.</summary>
    public bool Running => _thread is not null;

    /// <summary>Поднять чтение. Повторный вызов игнорируется.</summary>
    public void Start()
    {
        if (_thread is not null) return;
        _stopping = false;
        _thread = new Thread(Pump) { IsBackground = true, Name = "XiRawTouchpad" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Остановить чтение и дождаться потока. Идемпотентно.</summary>
    public void Stop()
    {
        var t = _thread;
        if (t is null) return;
        _stopping = true;
        if (_hwnd != IntPtr.Zero) PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        if (!t.Join(2000))
        {
            // Ссылку НЕ обнуляем: иначе следующий Start поднял бы второй читатель, оба окна
            // получали бы WM_INPUT, и каждый жест считался бы дважды. Застрявший одиночка
            // безопаснее пары.
            Log.Write("RawTouchpad: поток не завершился за 2 с — чтение больше не поднимаем");
            return;
        }
        _thread = null;
    }

    private void Pump()
    {
        try
        {
            _proc = WindowProc;
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = GetModuleHandleW(null),
                lpszClassName = "XiControlRawTouchpad",
            };
            // класс мог остаться от прошлого запуска в этом же процессе — это не ошибка
            RegisterClassExW(ref wc);

            _hwnd = CreateWindowExW(0, wc.lpszClassName, "", 0, 0, 0, 0, 0,
                HwndMessage, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            { Log.Write($"RawTouchpad: окно не создано ({Marshal.GetLastWin32Error()})"); return; }

            var rid = new RAWINPUTDEVICE
            { usUsagePage = PageDigitizer, usUsage = UsageTouchPad, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd };
            if (!RegisterRawInputDevices([rid], 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            {
                Log.Write("RawTouchpad: Precision Touchpad не найден — краевые ползунки недоступны");
                return;
            }
            Log.Write("RawTouchpad: чтение касаний запущено");

            while (!_stopping && GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }
        catch (Exception ex) { Log.Ex("RawTouchpad.Pump", ex); }
        finally
        {
            Unregister();
            foreach (var d in _devices.Values) Marshal.FreeHGlobal(d.Preparsed);
            _devices.Clear();
            _hwnd = IntPtr.Zero;
        }
    }

    private static void Unregister()
    {
        try
        {
            var rid = new RAWINPUTDEVICE
            { usUsagePage = PageDigitizer, usUsage = UsageTouchPad, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero };
            RegisterRawInputDevices([rid], 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        }
        catch (Exception ex) { Log.Ex("RawTouchpad.Unregister", ex); }
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_INPUT:
                try { OnInput(lParam); }
                catch (Exception ex) { Log.Ex("RawTouchpad.OnInput", ex); }
                break;
            case WM_CLOSE:
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void OnInput(IntPtr lParam)
    {
        uint size = (uint)_buffer.Length;
        var pin = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
        try
        {
            uint got = GetRawInputData(lParam, RID_INPUT, pin.AddrOfPinnedObject(), ref size,
                (uint)Marshal.SizeOf<RAWINPUTHEADER>());
            if (got == unchecked((uint)-1) || got == 0) return;
            if (BitConverter.ToUInt32(_buffer, 0) != RIM_TYPEHID) return;

            IntPtr device = Marshal.ReadIntPtr(pin.AddrOfPinnedObject(), 8);
            if (Describe(device) is not DeviceInfo info) return;

            int hdr = Marshal.SizeOf<RAWINPUTHEADER>();
            uint reportSize = BitConverter.ToUInt32(_buffer, hdr);
            uint reportCount = BitConverter.ToUInt32(_buffer, hdr + 4);
            if (reportSize == 0 || reportCount == 0) return;

            var report = new byte[reportSize];
            for (uint r = 0; r < reportCount; r++)
            {
                Array.Copy(_buffer, hdr + 8 + (int)(r * reportSize), report, 0, (int)reportSize);
                _frame.Clear();
                foreach (var link in info.Links)
                {
                    // Tip Switch — это КНОПКА, а не значение: HidP_GetUsageValue на ней всегда
                    // отвечает ошибкой, и строгая проверка отбрасывала бы каждый контакт.
                    // Нажатые кнопки коллекции отдаёт HidP_GetUsages списком usage-кодов.
                    if (!IsTouching(info.Preparsed, link, report, reportSize)) continue;
                    if (HidP_GetUsageValue(0, PageGeneric, link, UsageX, out uint x,
                            info.Preparsed, report, reportSize) != HidpSuccess) continue;
                    if (HidP_GetUsageValue(0, PageGeneric, link, UsageY, out uint y,
                            info.Preparsed, report, reportSize) != HidpSuccess) continue;
                    // Contact Identifier есть не у всех прошивок; без него различаем пальцы
                    // по номеру коллекции — жесту нужна только различимость, не сам номер.
                    int id = HidP_GetUsageValue(0, PageDigitizer, link, UsageContactId, out uint raw,
                        info.Preparsed, report, reportSize) == HidpSuccess ? (int)raw : link;

                    _frame.Add(new TouchContact(id,
                        Fraction(x, info.XMin, info.XMax), Fraction(y, info.YMin, info.YMax)));
                }
                _onFrame(_frame);
            }
        }
        finally { pin.Free(); }
    }

    // Палец на панели: ищем Tip Switch среди нажатых кнопок этой коллекции. Буфер с запасом —
    // кнопок в PTP-коллекции единицы, а перевыделять его на каждый контакт незачем.
    private readonly ushort[] _usages = new ushort[32];

    private bool IsTouching(IntPtr preparsed, ushort link, byte[] report, uint reportSize)
    {
        uint length = (uint)_usages.Length;
        if (HidP_GetUsages(0, PageDigitizer, link, _usages, ref length, preparsed, report, reportSize) != HidpSuccess)
            return false;
        for (uint i = 0; i < length; i++)
            if (_usages[i] == UsageTipSwitch) return true;
        return false;
    }

    private static double Fraction(uint raw, int min, int max) =>
        max > min ? Math.Clamp((raw - (double)min) / (max - min), 0, 1) : 0;

    // Дескриптор устройства разбираем один раз: он не меняется, а HidP_GetValueCaps недёшев.
    private DeviceInfo? Describe(IntPtr device)
    {
        if (_devices.TryGetValue(device, out var known)) return known.Links.Length > 0 ? known : null;

        uint size = 0;
        // первый вызов только спрашивает размер: успех — это 0, а не число байт
        if (GetRawInputDeviceInfoW(device, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size) != 0 || size == 0)
            return null;
        IntPtr pp = Marshal.AllocHGlobal((int)size);
        var empty = new DeviceInfo(pp, [], 0, 0, 0, 0);

        if (GetRawInputDeviceInfoW(device, RIDI_PREPARSEDDATA, pp, ref size) == unchecked((uint)-1) ||
            HidP_GetCaps(pp, out HIDP_CAPS caps) != HidpSuccess)
        { _devices[device] = empty; return null; }

        ushort count = caps.NumberInputValueCaps;
        var all = new HIDP_VALUE_CAPS[count];
        if (count == 0 || HidP_GetValueCaps(0, all, ref count, pp) != HidpSuccess)
        { _devices[device] = empty; return null; }

        var xs = all.Take(count).Where(c => c.UsagePage == PageGeneric && c.NotRange_Usage == UsageX).ToArray();
        var ys = all.Take(count).Where(c => c.UsagePage == PageGeneric && c.NotRange_Usage == UsageY).ToArray();
        if (xs.Length == 0 || ys.Length == 0) { _devices[device] = empty; return null; }

        var info = new DeviceInfo(pp, [.. xs.Select(c => c.LinkCollection)],
            xs[0].LogicalMin, xs[0].LogicalMax, ys[0].LogicalMin, ys[0].LogicalMax);
        _devices[device] = info;
        Log.Write($"RawTouchpad: контактов {info.Links.Length}, X {info.XMin}..{info.XMax}, Y {info.YMin}..{info.YMax}");
        return info;
    }

    public void Dispose() => Stop();

    private sealed record DeviceInfo(IntPtr Preparsed, ushort[] Links, int XMin, int XMax, int YMin, int YMax);

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW c);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? n);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint s);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint hdr);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputDeviceInfoW(IntPtr h, uint cmd, IntPtr data, ref uint size);
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
    [DllImport("hid.dll")] private static extern int HidP_GetValueCaps(int type, [In, Out] HIDP_VALUE_CAPS[] caps, ref ushort len, IntPtr pp);
    [DllImport("hid.dll")] private static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint value, IntPtr pp, byte[] report, uint len);
    [DllImport("hid.dll")] private static extern int HidP_GetUsages(int type, ushort page, ushort link, [In, Out] ushort[] usages, ref uint length, IntPtr pp, byte[] report, uint len);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice; public IntPtr wParam; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_VALUE_CAPS
    {
        public ushort UsagePage; public byte ReportID; [MarshalAs(UnmanagedType.U1)] public bool IsAlias;
        public ushort BitField, LinkCollection, LinkUsage, LinkUsagePage;
        [MarshalAs(UnmanagedType.U1)] public bool IsRange, IsStringRange, IsDesignatorRange, IsAbsolute, HasNull;
        public byte Reserved; public ushort BitSize, ReportCount;
        public ushort Reserved2a, Reserved2b, Reserved2c, Reserved2d, Reserved2e;
        public uint UnitsExp, Units; public int LogicalMin, LogicalMax, PhysicalMin, PhysicalMax;
        public ushort NotRange_Usage, NotRange_Reserved1, NotRange_StringIndex, NotRange_Reserved2,
                      NotRange_DesignatorIndex, NotRange_Reserved3, NotRange_DataIndex, NotRange_Reserved4;
    }
}
