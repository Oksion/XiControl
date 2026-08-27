using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace XiControl.Ui;

internal abstract class FlyoutWindow : Window, IDisposable
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const long WsPopup = unchecked((long)0x80000000);
    private const long NativeFrameStyles = 0x00CF0000;
    private const long WsExToolWindow = 0x80;
    private const long NativeFrameExStyles = 0x00020301;
    private const int SwHide = 0;
    private const int DwmNcRenderingPolicy = 2;
    private const int DwmNcRenderingDisabled = 1;
    private const int DwmBorderColor = 34;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmWindowCornerDoNotRound = 1;
    private const uint DwmColorNone = 0xFFFFFFFE;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private bool _disposing;
    private readonly bool _borderless;
    private readonly int _cornerRadiusDips;
    private readonly bool _clipTopScanline;

    protected FlyoutWindow(bool alwaysOnTop = true, bool hideFromTaskbar = true, bool borderless = true,
        bool useAcrylic = true, int cornerRadiusDips = 0, bool clipTopScanline = false)
    {
        _borderless = borderless;
        _cornerRadiusDips = Math.Max(0, cornerRadiusDips);
        _clipTopScanline = clipTopScanline;
        if (useAcrylic) SystemBackdrop = new DesktopAcrylicBackdrop();
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(Handle);
        NativeAppWindow = AppWindow.GetFromWindowId(WindowId);
        if (NativeAppWindow.Presenter is OverlappedPresenter presenter)
        {
            if (borderless)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                ApplyBorderlessChrome();
            }
            presenter.IsAlwaysOnTop = alwaysOnTop;
        }
        if (hideFromTaskbar)
        {
            long style = GetWindowLongPtrW(Handle, GwlExStyle).ToInt64();
            _ = SetWindowLongPtrW(Handle, GwlExStyle,
                new IntPtr((style & ~NativeFrameExStyles) | WsExToolWindow));
        }
        NativeAppWindow.Closing += (_, e) =>
        {
            if (_disposing) return;
            e.Cancel = true;
            Hide();
        };
        Activated += (_, _) =>
        {
            // DWM can recompute its frame on activation and theme changes. Reasserting the
            // attributes keeps dark flyouts from regaining the system's bright one-pixel edge.
            if (_borderless) ApplyBorderlessChrome(refreshFrame: false);
        };
    }

    public bool IsVisible { get; private set; }
    protected IntPtr Handle { get; }
    protected Microsoft.UI.WindowId WindowId { get; }
    protected AppWindow NativeAppWindow { get; }

    public void ShowAt(int x, int y, int width, int height, bool activate = true)
    {
        if (_borderless) ApplyBorderlessChrome(refreshFrame: false);
        NativeAppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
        if (_borderless) UpdateWindowRegion(width, height);
        NativeAppWindow.Show(activate);
        IsVisible = true;
    }

    /// <summary>
    /// AppWindow sizes are physical pixels while WinUI content is measured in DIPs. Probe the
    /// destination monitor before sizing so popups retain their intended layout at 125–300% DPI.
    /// </summary>
    protected Size PhysicalSizeForDips(Rectangle targetArea, int width, int height)
    {
        int dpi = DpiForArea(targetArea);
        return new Size(ScreenMetrics.DipsToPixels(width, dpi), ScreenMetrics.DipsToPixels(height, dpi));
    }

    /// <summary>Move a one-pixel probe to the destination monitor before querying window DPI.</summary>
    protected int DpiForArea(Rectangle targetArea)
    {
        // A visible WinUI window must never be used as a 1x1 DPI probe. Besides producing a
        // visible jump, that transient resize can invalidate activation/composition state.
        if (IsVisible) return unchecked((int)GetDpiForWindow(Handle));

        int probeX = Math.Clamp(targetArea.Left + 1, targetArea.Left, Math.Max(targetArea.Left, targetArea.Right - 1));
        int probeY = Math.Clamp(targetArea.Top + 1, targetArea.Top, Math.Max(targetArea.Top, targetArea.Bottom - 1));
        NativeAppWindow.MoveAndResize(new Windows.Graphics.RectInt32(probeX, probeY, 1, 1));
        return unchecked((int)GetDpiForWindow(Handle));
    }

    public void Hide()
    {
        ShowWindow(Handle, SwHide);
        IsVisible = false;
        OnHidden();
    }

    public virtual void Dispose()
    {
        if (_disposing) return;
        _disposing = true;
        Close();
    }

    protected virtual void OnHidden() { }

    /// <summary>Reapply the client-owned rounded clip after a visible window changes size.</summary>
    protected void MoveAndResize(int x, int y, int width, int height)
    {
        NativeAppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
        if (_borderless) UpdateWindowRegion(width, height);
    }

    private void ApplyBorderlessChrome(bool refreshFrame = true)
    {
        // Strip every native frame bit and use a popup-style top-level HWND. OverlappedPresenter
        // alone can leave a visible non-client strip on recent Windows builds.
        long style = GetWindowLongPtrW(Handle, GwlStyle).ToInt64();
        _ = SetWindowLongPtrW(Handle, GwlStyle, new IntPtr((style & ~NativeFrameStyles) | WsPopup));
        long exStyle = GetWindowLongPtrW(Handle, GwlExStyle).ToInt64();
        _ = SetWindowLongPtrW(Handle, GwlExStyle, new IntPtr(exStyle & ~NativeFrameExStyles));

        int ncRendering = DwmNcRenderingDisabled;
        _ = DwmSetWindowAttribute(Handle, DwmNcRenderingPolicy,
            ref ncRendering, (uint)Marshal.SizeOf<int>());

        // DWM no longer owns either the outline or the corner. The matching HRGN below is the
        // only outer shape, so the XAML surface keeps one clean radius instead of two outlines.
        int cornerPreference = DwmWindowCornerDoNotRound;
        _ = DwmSetWindowAttribute(Handle, DwmWindowCornerPreference,
            ref cornerPreference, (uint)Marshal.SizeOf<int>());

        int borderColor = unchecked((int)DwmColorNone);
        _ = DwmSetWindowAttribute(Handle, DwmBorderColor,
            ref borderColor, (uint)Marshal.SizeOf<int>());

        if (refreshFrame)
        {
            _ = SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
    }

    private void UpdateWindowRegion(int width, int height)
    {
        if (_cornerRadiusDips <= 0 || width <= 0 || height <= 0)
        {
            _ = SetWindowRgn(Handle, IntPtr.Zero, redraw: true);
            return;
        }

        int dpi = unchecked((int)GetDpiForWindow(Handle));
        int radius = ScreenMetrics.DipsToPixels(_cornerRadiusDips, dpi);
        Rectangle bounds = WindowRegionGeometry.Bounds(width, height, _clipTopScanline);
        IntPtr region = CreateRoundRectRgn(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom,
            radius * 2, radius * 2);
        if (region == IntPtr.Zero) return;

        // On success the system owns the region. Delete it only when SetWindowRgn rejects it.
        if (SetWindowRgn(Handle, region, redraw: true) == 0) _ = DeleteObject(region);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr window, IntPtr region,
        [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom,
        int ellipseWidth, int ellipseHeight);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute,
        ref int value, uint valueSize);
}

internal static class WindowRegionGeometry
{
    /// <summary>
    /// WinUI's custom-titlebar swap chain can leave physical scanline zero black even though the
    /// client rect starts at (0,0). Excluding only that scanline exposes the desktop underneath;
    /// all logical sizing and the client-owned rounded clip remain unchanged.
    /// </summary>
    internal static Rectangle Bounds(int width, int height, bool clipTopScanline) =>
        Rectangle.FromLTRB(0, clipTopScanline ? 1 : 0, width + 1, height + 1);
}

internal static class ScreenMetrics
{
    private const uint MonitorDefaultToNearest = 2;

    public static Rectangle WorkingAreaAtCursor()
    {
        GetCursorPos(out var cursor);
        IntPtr monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        return WorkingArea(monitor);
    }

    public static Rectangle WorkingAreaForWindow(IntPtr window)
    {
        IntPtr monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        return WorkingArea(monitor);
    }

    private static Rectangle WorkingArea(IntPtr monitor)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfoW(monitor, ref info)
            ? Rectangle.FromLTRB(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom)
            : new Rectangle(0, 0, 1920, 1080);
    }

    public static Point CursorPosition()
    {
        GetCursorPos(out var point);
        return new Point(point.X, point.Y);
    }

    internal static int DipsToPixels(int dips, int dpi)
    {
        int effectiveDpi = dpi > 0 ? dpi : 96;
        return Math.Max(1, (int)Math.Round(dips * effectiveDpi / 96d, MidpointRounding.AwayFromZero));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
}
