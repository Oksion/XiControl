using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XiControl.SystemIntegration;

/// <summary>Действия для «оживления» спец-клавиш: проекция, настройки, Copilot, мультимедиа.</summary>
public static class KeyActions
{
    private const byte VK_LWIN = 0x5B, VK_SHIFT = 0x10, VK_P = 0x50, VK_C = 0x43,
                       VK_S = 0x53, VK_TAB = 0x09;
    // Мультимедиа и «калькулятор» — стандартные VK, их ловит шелл, а не конкретное окно
    private const byte VK_MEDIA_NEXT = 0xB0, VK_MEDIA_PREV = 0xB1, VK_MEDIA_STOP = 0xB2,
                       VK_MEDIA_PLAY_PAUSE = 0xB3, VK_LAUNCH_APP2 = 0xB7;
    private const uint KEYEVENTF_KEYUP = 0x02;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    /// <summary>
    /// Проекция экрана — окно выбора режима (как Win+P). Синтетический Win+P из
    /// elevated-процесса Explorer иногда не подхватывает; нативный DisplaySwitch.exe
    /// открывает то же окно напрямую и надёжнее. Откат — эмуляция Win+P.
    /// </summary>
    public static void Projection()
    {
        try
        {
            string ds = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "DisplaySwitch.exe");
            if (File.Exists(ds)) { Process.Start(new ProcessStartInfo(ds) { UseShellExecute = false }); return; }
        }
        catch (Exception ex) { Log.Ex("KeyActions.Projection", ex); /* откатываемся на Win+P */ }
        WinCombo(VK_P);
    }

    /// <summary>Нейропомощник — открыть Windows Copilot (Win+C).</summary>
    public static void Copilot() => WinCombo(VK_C);

    /// <summary>Ножницы Windows — системный экранный фрагмент (Win+Shift+S).</summary>
    public static void Screenshot() => Combo([VK_LWIN, VK_SHIFT], VK_S);

    /// <summary>Представление задач Windows (Win+Tab).</summary>
    public static void TaskView() => WinCombo(VK_TAB);

    /// <summary>Открыть Параметры Windows (опция "SettingsKey": "settings").</summary>
    public static void OpenSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Ex("KeyActions.OpenSettings", ex); /* URI-обработчик может отсутствовать */ }
    }

    /// <summary>
    /// Запустить команду одной строкой: путь к exe/файлу/URL + аргументы. Путь с пробелами —
    /// в кавычках; строка без кавычек, целиком являющаяся существующим файлом, — тоже путь
    /// (совместимость со старым AiKeyProgram без кавычек).
    /// </summary>
    public static void LaunchCommand(string command)
    {
        var (path, args) = ParseCommand(command, File.Exists);
        Launch(path, args);
    }

    /// <summary>
    /// Разобрать командную строку в (path, args) — чистая функция без побочных эффектов.
    /// <paramref name="fileExists"/> — сидка проверки «строка без кавычек целиком является
    /// существующим файлом» (в проде <see cref="File.Exists(string)"/>); вынесена ради тестов.
    /// args = null, если аргументов нет.
    /// </summary>
    internal static (string Path, string? Args) ParseCommand(string command, Func<string, bool> fileExists)
    {
        command = command.Trim();
        string path;
        string? args = null;
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            if (end > 0) { path = command[1..end]; args = command[(end + 1)..].Trim(); }
            else path = command.Trim('"');
        }
        else
        {
            int sp = command.IndexOf(' ');
            if (sp > 0 && !fileExists(Environment.ExpandEnvironmentVariables(command)))
            { path = command[..sp]; args = command[(sp + 1)..].Trim(); }
            else path = command;
        }
        return (path, string.IsNullOrWhiteSpace(args) ? null : args);
    }

    /// <summary>Запустить произвольную программу/файл/URL (действие "launch" у клавиш).</summary>
    public static void Launch(string path, string? args = null)
    {
        try
        {
            path = Environment.ExpandEnvironmentVariables(path.Trim());
            var psi = new ProcessStartInfo(path) { UseShellExecute = true };
            if (!string.IsNullOrWhiteSpace(args))
                psi.Arguments = Environment.ExpandEnvironmentVariables(args);
            if (File.Exists(path))
                psi.WorkingDirectory = Path.GetDirectoryName(path);
            Process.Start(psi);
        }
        catch (Exception ex) { Log.Ex("KeyActions.Launch", ex); }
    }

    /// <summary>Воспроизведение / пауза. Медиа-клавишу получает владелец медиа-сессии
    /// Windows (SMTC), поэтому работает с любым плеером — своей интеграции не нужно.</summary>
    public static void MediaPlayPause() => TapKey(VK_MEDIA_PLAY_PAUSE);

    /// <summary>Следующий трек.</summary>
    public static void MediaNext() => TapKey(VK_MEDIA_NEXT);

    /// <summary>Предыдущий трек.</summary>
    public static void MediaPrev() => TapKey(VK_MEDIA_PREV);

    /// <summary>Остановить воспроизведение.</summary>
    public static void MediaStop() => TapKey(VK_MEDIA_STOP);

    /// <summary>
    /// Калькулятор — именно клавишей, а не Process.Start: в Win11 это UWP-приложение, а мы
    /// запущены elevated, и запуск UWP из-под админа ненадёжен. VK уходит в шелл, поэтому
    /// калькулятор открывается от имени пользователя — как и должен.
    /// </summary>
    public static void Calculator() => TapKey(VK_LAUNCH_APP2);

    // Нажать и отпустить одну клавишу (медиа/запуск приложения — без модификаторов)
    private static void TapKey(byte vk)
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private static void WinCombo(byte vk)
        => Combo([VK_LWIN], vk);

    private static void Combo(ReadOnlySpan<byte> modifiers, byte vk)
    {
        foreach (byte modifier in modifiers)
            keybd_event(modifier, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        for (int i = modifiers.Length - 1; i >= 0; i--)
            keybd_event(modifiers[i], 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}
