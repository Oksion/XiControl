using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XiControl.SystemIntegration;

/// <summary>
/// Открыть веб-ссылку в браузере пользователя — без прав администратора. Мы elevated
/// (манифест requireAdministrator), и обычный ShellExecute запускает закрытый браузер
/// тоже elevated: сёрфинг и загрузки (пакеты драйверов!) идут с админским токеном. Поэтому
/// просим открыть ссылку сам рабочий стол Explorer (medium IL) — классический приём
/// «IShellDispatch2::ShellExecute через IShellWindows»: процесс браузера порождает Explorer,
/// и он наследует его обычный токен. Откат — прежний прямой ShellExecute.
/// </summary>
public static class ShellOpen
{
    /// <summary>Открыть http/https-ссылку; остальные схемы (file:, ms-settings: и т.п.) не открываем.</summary>
    public static void OpenUrl(string url)
    {
        if (!TryWebUrl(url, out string safe)) { Log.Write($"ShellOpen: не http(s)-ссылка, не открываем: {url}"); return; }
        try { if (ViaDesktopShell(safe)) return; }
        catch (Exception ex) { Log.Ex("ShellOpen.ViaDesktopShell", ex); }
        try { Process.Start(new ProcessStartInfo(safe) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Ex("ShellOpen.OpenUrl", ex); /* нет браузера */ }
    }

    /// <summary>Только абсолютные http/https с хостом; наружу — нормализованный
    /// (экранированный) вид, чтобы в шелл не ушли пробелы и кавычки.</summary>
    internal static bool TryWebUrl(string? url, out string safe)
    {
        safe = "";
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u)
            || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(u.Host))
            return false;
        safe = u.AbsoluteUri;
        return true;
    }

    // Рабочий стол → его IShellBrowser → активный IShellView → фон папки (IShellFolderViewDual)
    // → .Application (IShellDispatch2) → ShellExecute. Всё исполняется в процессе Explorer.
    private static bool ViaDesktopShell(string url)
    {
        if (Type.GetTypeFromCLSID(CLSID_ShellWindows) is not Type t
            || Activator.CreateInstance(t) is not IShellWindows windows)
            return false;
        object loc = CSIDL_DESKTOP, root = null!; // root = VT_EMPTY
        if (windows.FindWindowSW(ref loc, ref root, SWC_DESKTOP, out int hwnd, SWFO_NEEDDISPATCH, out object desktop) != 0
            || desktop is not IOleServiceProvider sp)
            return false;
        Guid sid = SID_STopLevelBrowser, iidBrowser = typeof(IShellBrowser).GUID, iidDispatch = IID_IDispatch;
        if (sp.QueryService(ref sid, ref iidBrowser, out object b) != 0 || b is not IShellBrowser browser
            || browser.QueryActiveShellView(out IShellView view) != 0
            || view.GetItemObject(SVGIO_BACKGROUND, ref iidDispatch, out object folderView) != 0)
            return false;
        // Explorer запускает браузер сам — без разрешения тот не выйдет на передний план
        if (GetWindowThreadProcessId(hwnd, out uint pid) != 0) AllowSetForegroundWindow(pid);
        ((dynamic)folderView).Application.ShellExecute(url, "", "", "open", SW_SHOWNORMAL);
        return true;
    }

    private static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IID_IDispatch = new("00020400-0000-0000-C000-000000000046");
    private const int CSIDL_DESKTOP = 0, SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1, SW_SHOWNORMAL = 1;
    private const uint SVGIO_BACKGROUND = 0;

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        int f0(); int f1(); int f2(); int f3(); int f4(); int f5(); int f6(); int f7(); // слоты 0..7 (не используем)
        [PreserveSig]
        int FindWindowSW([MarshalAs(UnmanagedType.Struct)] ref object pvarLoc,
                         [MarshalAs(UnmanagedType.Struct)] ref object pvarLocRoot, int swClass, out int phwnd,
                         int swfwOptions, [MarshalAs(UnmanagedType.IDispatch)] out object ppdispOut); // слот 8
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject); // слот 0
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        int f0(); int f1(); int f2(); int f3(); int f4(); int f5();
        int f6(); int f7(); int f8(); int f9(); int f10(); int f11(); // слоты 0..11 (не используем)
        [PreserveSig]
        int QueryActiveShellView(out IShellView ppshv); // слот 12
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        int f0(); int f1(); int f2(); int f3(); int f4(); int f5();
        int f6(); int f7(); int f8(); int f9(); int f10(); int f11(); // слоты 0..11 (не используем)
        [PreserveSig]
        int GetItemObject(uint uItem, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv); // слот 12
    }
}
