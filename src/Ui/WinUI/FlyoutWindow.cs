using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace XiControl.Ui;

internal abstract class FlyoutWindow : Window, IDisposable
{
    private bool _disposing;

    protected FlyoutWindow(bool alwaysOnTop = true, bool hideFromTaskbar = true,
        bool useAcrylic = true)
    {
        if (useAcrylic) SystemBackdrop = new DesktopAcrylicBackdrop();
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        NativeAppWindow = AppWindow;
        WindowId = NativeAppWindow.Id;
        if (NativeAppWindow.Presenter is OverlappedPresenter presenter)
        {
            // Windows App SDK owns the border, corner and shadow. Only its title bar is hidden;
            // derived WinUI windows may opt into ExtendsContentIntoTitleBar for drag regions.
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = alwaysOnTop;
        }
        NativeAppWindow.IsShownInSwitchers = !hideFromTaskbar;
        NativeAppWindow.Closing += (_, e) =>
        {
            if (_disposing) return;
            e.Cancel = true;
            Hide();
        };
    }

    public bool IsVisible { get; private set; }
    protected IntPtr Handle { get; }
    protected Microsoft.UI.WindowId WindowId { get; }
    protected AppWindow NativeAppWindow { get; }

    public void ShowAt(int x, int y, int width, int height, bool activate = true)
    {
        NativeAppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
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
        NativeAppWindow.Hide();
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

    /// <summary>Move and resize through AppWindow so its native frame stays in sync.</summary>
    protected void MoveAndResize(int x, int y, int width, int height)
    {
        NativeAppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
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
