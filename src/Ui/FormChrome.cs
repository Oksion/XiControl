using System.Runtime.InteropServices;

namespace XiControl.Ui;

/// <summary>
/// Win32-хром окон, общий для форм приложения: тёмный заголовок DWM и WM_SETREDRAW
/// (полная заморозка перерисовки — SuspendLayout замораживает только компоновку,
/// без этого пересборка видимого окна мигает белым). Флайауты подключатся в Фазе 6.4.
/// </summary>
public static class FormChrome
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int WM_SETREDRAW = 0x000B;

    /// <summary>Тёмный/светлый заголовок окна (на старых Windows атрибута нет — молча пропускаем).</summary>
    public static void SetDwmDark(Form f, bool dark)
    {
        if (!f.IsHandleCreated) return;
        try { int v = dark ? 1 : 0; _ = DwmSetWindowAttribute(f.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int)); }
        catch { /* старая Windows — не критично */ }
    }

    /// <summary>Вкл/выкл перерисовку окна целиком; после включения нужен Refresh().</summary>
    public static void SetRedraw(Control c, bool on)
        => SendMessage(c.Handle, WM_SETREDRAW, (IntPtr)(on ? 1 : 0), IntPtr.Zero);

    /// <summary>
    /// Системные скроллбары контрола под тему: «DarkMode_Explorer» — тёмная полоса, как в
    /// Проводнике (Win10 1809+). WinForms сам рисует классическую светлую — на тёмном окне
    /// она выглядит чужеродно. Имя темы полуофициальное, но стабильное; на старых Windows
    /// вызов молча не сработает — останется светлая (не критично).
    /// </summary>
    public static void SetDarkScrollbars(Control c, bool dark)
    {
        try { _ = SetWindowTheme(c.Handle, dark ? "DarkMode_Explorer" : "Explorer", null); }
        catch { /* нет uxtheme/темы — не критично */ }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string? appName, string? idList);
}
