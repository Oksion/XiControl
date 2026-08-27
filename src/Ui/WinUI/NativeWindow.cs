using System.Runtime.InteropServices;

namespace XiControl.Ui;

/// <summary>В WinUI нет обёртки message-only window; tray-callback использует этот тонкий слой Win32.</summary>
internal sealed class NativeWindow : IDisposable
{
    private const int HwndMessage = -3;
    private readonly WndProc _wndProc;
    private readonly string _className = $"XiControl.Native.{Guid.NewGuid():N}";
    private readonly IntPtr _module;
    private ushort _atom;

    public NativeWindow(Func<IntPtr, uint, IntPtr, IntPtr, IntPtr> callback)
    {
        _wndProc = (window, message, wParam, lParam) =>
        {
            try { return callback(window, message, wParam, lParam); }
            catch (Exception ex)
            {
                Log.Ex("NativeWindow.WndProc", ex);
                return DefWindowProcW(window, message, wParam, lParam);
            }
        };
        _module = GetModuleHandleW(null);
        var windowClass = new WndClassEx
        {
            Size = (uint)Marshal.SizeOf<WndClassEx>(),
            Instance = _module,
            ClassName = _className,
            WindowProcedure = _wndProc,
        };
        _atom = RegisterClassExW(ref windowClass);
        if (_atom == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        Handle = CreateWindowExW(0, _className, _className, 0, 0, 0, 0, 0,
            new IntPtr(HwndMessage), IntPtr.Zero, _module, IntPtr.Zero);
        if (Handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    public IntPtr Handle { get; private set; }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }
        if (_atom != 0)
        {
            UnregisterClassW(_className, _module);
            _atom = 0;
        }
        GC.KeepAlive(_wndProc);
    }

    internal static IntPtr DefaultWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam) =>
        DefWindowProcW(window, message, wParam, lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public WndProc WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClassW(string className, IntPtr instance);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
