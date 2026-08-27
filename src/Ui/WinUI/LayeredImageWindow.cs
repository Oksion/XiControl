using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace XiControl.Ui;

/// <summary>
/// A tiny non-activating image surface for OEM OSD artwork. WinUI 3 currently forces an opaque
/// composition background even when the XAML root is transparent, so the already-rendered Xiaomi
/// PNG is handed directly to DWM with its original per-pixel alpha channel.
/// </summary>
internal sealed class LayeredImageWindow : IDisposable
{
    private const uint WsPopup = 0x80000000;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExNoActivate = 0x08000000;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const byte AcSrcOver = 0;
    private const byte AcSrcAlpha = 1;
    private const uint UlwAlpha = 2;
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;

    private readonly WindowProcedure _windowProcedure;
    private readonly string _className = $"XiControl.OemOsd.{Guid.NewGuid():N}";
    private readonly IntPtr _module;
    private ushort _atom;

    public LayeredImageWindow()
    {
        _windowProcedure = WindowProc;
        _module = GetModuleHandleW(null);
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            Instance = _module,
            ClassName = _className,
            WindowProcedure = _windowProcedure,
        };
        _atom = RegisterClassExW(ref windowClass);
        if (_atom == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        Handle = CreateWindowExW(WsExTopmost | WsExToolWindow | WsExLayered | WsExNoActivate,
            _className, string.Empty, WsPopup, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, _module, IntPtr.Zero);
        if (Handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    public IntPtr Handle { get; private set; }

    public int DpiForArea(Rectangle area)
    {
        int x = Math.Clamp(area.Left + 1, area.Left, Math.Max(area.Left, area.Right - 1));
        int y = Math.Clamp(area.Top + 1, area.Top, Math.Max(area.Top, area.Bottom - 1));
        _ = SetWindowPos(Handle, new IntPtr(-1), x, y, 1, 1, 0x0010);
        uint dpi = GetDpiForWindow(Handle);
        return dpi > 0 ? unchecked((int)dpi) : 96;
    }

    public void Show(Bitmap image, Point location)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(Handle == IntPtr.Zero, this);

        using var premultiplied = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppPArgb);
        using (Graphics graphics = Graphics.FromImage(premultiplied))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImageUnscaled(image, 0, 0);
        }

        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        IntPtr memoryDc = CreateCompatibleDC(screenDc);
        IntPtr dib = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;
        try
        {
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = premultiplied.Width,
                    Height = -premultiplied.Height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = BiRgb,
                },
            };
            dib = CreateDIBSection(screenDc, ref info, DibRgbColors, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            CopyPixels(premultiplied, bits);
            previous = SelectObject(memoryDc, dib);
            var destination = new NativePoint(location.X, location.Y);
            var size = new NativeSize(premultiplied.Width, premultiplied.Height);
            var source = new NativePoint(0, 0);
            var blend = new BlendFunction
            {
                BlendOp = AcSrcOver,
                SourceConstantAlpha = 255,
                AlphaFormat = AcSrcAlpha,
            };
            if (!UpdateLayeredWindow(Handle, screenDc, ref destination, ref size, memoryDc,
                    ref source, 0, ref blend, UlwAlpha))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            _ = ShowWindow(Handle, SwShowNoActivate);
        }
        finally
        {
            if (previous != IntPtr.Zero) _ = SelectObject(memoryDc, previous);
            if (dib != IntPtr.Zero) _ = DeleteObject(dib);
            if (memoryDc != IntPtr.Zero) _ = DeleteDC(memoryDc);
            _ = ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public void Hide()
    {
        if (Handle != IntPtr.Zero) _ = ShowWindow(Handle, SwHide);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            _ = DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }
        if (_atom != 0)
        {
            _ = UnregisterClassW(_className, _module);
            _atom = 0;
        }
        GC.KeepAlive(_windowProcedure);
    }

    private static void CopyPixels(Bitmap bitmap, IntPtr destination)
    {
        int rowBytes = checked(bitmap.Width * 4);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var row = new byte[rowBytes];
            for (int y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, rowBytes);
                Marshal.Copy(row, 0, IntPtr.Add(destination, y * rowBytes), rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam) =>
        DefWindowProcW(window, message, wParam, lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public WindowProcedure WindowProcedure;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize(int width, int height)
    {
        public int Width = width;
        public int Height = height;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClassW(string className, IntPtr instance);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage,
        out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr value);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destinationDc,
        ref NativePoint destination, ref NativeSize size, IntPtr sourceDc, ref NativePoint source,
        uint colorKey, ref BlendFunction blend, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
}
