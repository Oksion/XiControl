using System.Drawing;
using System.Runtime.InteropServices;

namespace XiControl.Ui;

internal sealed class NativeTrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 24;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonDown = 0x0204;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;
    private const uint NinBalloonUserClick = 0x0405;
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NimSetVersion = 4;
    private const uint NotifyIconVersion4 = 4;
    private const uint NifMessage = 0x1;
    private const uint NifIcon = 0x2;
    private const uint NifTip = 0x4;
    private const uint NifInfo = 0x10;
    private const uint NifShowTip = 0x80;
    private const uint NiifInfo = 0x1;

    private readonly NativeWindow _window;
    private readonly uint _id;
    private readonly uint _taskbarCreated;
    private Icon? _icon;
    private string _tooltip = "Xi Control";
    private bool _added;
    private bool _version4;

    public NativeTrayIcon(uint id = 1)
    {
        _id = id;
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        _window = new NativeWindow(WindowProc);
    }

    public event Action? Activated;
    public event Action? ContextRequested;
    public event Action? BalloonActivated;

    public Icon? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            if (_added) Modify(NifIcon);
        }
    }

    public string Tooltip
    {
        get => _tooltip;
        set
        {
            _tooltip = Truncate(value, 127);
            if (_added) Modify(NifTip | NifShowTip);
        }
    }

    public void Show()
    {
        if (_added) return;
        var data = Data(NifMessage | NifIcon | NifTip | NifShowTip);
        if (!ShellNotifyIconW(NimAdd, ref data))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        _added = true;
        var version = Data(0);
        version.TimeoutOrVersion = NotifyIconVersion4;
        _version4 = ShellNotifyIconW(NimSetVersion, ref version);
    }

    public void ShowBalloon(string title, string text)
    {
        if (!_added) return;
        var data = Data(NifInfo);
        data.InfoTitle = Truncate(title, 63);
        data.Info = Truncate(text, 255);
        data.InfoFlags = NiifInfo;
        ShellNotifyIconW(NimModify, ref data);
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = Data(0);
            ShellNotifyIconW(NimDelete, ref data);
            _added = false;
            _version4 = false;
        }
        _window.Dispose();
    }

    private void Modify(uint flags)
    {
        var data = Data(flags);
        ShellNotifyIconW(NimModify, ref data);
    }

    private NotifyIconData Data(uint flags) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = _window.Handle,
        Id = _id,
        Flags = flags,
        CallbackMessage = CallbackMessage,
        Icon = _icon?.Handle ?? IntPtr.Zero,
        Tip = _tooltip,
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    private IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == _taskbarCreated && _added)
        {
            _added = false;
            Show();
            return IntPtr.Zero;
        }
        if (message == CallbackMessage)
        {
            uint notification = NotificationCode(lParam, _version4);
            // Explorer normally uses NIN_*/WM_CONTEXTMENU for v4, but some taskbar/overflow
            // transitions still deliver the legacy button-up codes. Accept both encodings.
            if (IsActivationNotification(notification)) Activated?.Invoke();
            else if (IsContextNotification(notification)) ContextRequested?.Invoke();
            else if (notification == NinBalloonUserClick) BalloonActivated?.Invoke();
            return IntPtr.Zero;
        }
        return NativeWindow.DefaultWindowProc(window, message, wParam, lParam);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    internal static uint NotificationCode(IntPtr lParam, bool version4)
    {
        uint raw = unchecked((uint)lParam.ToInt64());
        // Legacy messages contain only the notification and v4 packs the icon id into HIWORD.
        // LOWORD is therefore correct for both formats, including the observed case where
        // NIM_SETVERSION reports failure but Explorer has already started sending v4 payloads.
        _ = version4;
        return raw & 0xFFFF;
    }

    internal static bool IsActivationNotification(uint notification) =>
        notification is NinSelect or NinKeySelect or WmLButtonUp;

    internal static bool IsContextNotification(uint notification) =>
        notification is WmContextMenu or WmRButtonDown or WmRButtonUp;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string message);
}
