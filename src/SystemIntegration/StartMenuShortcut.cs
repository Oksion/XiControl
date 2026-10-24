using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace XiControl.SystemIntegration;

/// <summary>
/// Ярлык в меню «Пуск» (XIC-106). Пакет winget у нас portable — такой ярлыков не создаёт, а
/// инсталлятора нет, поэтому ярлык кладёт само приложение и только по явному действию человека
/// (тумблер в «Общих» или клик по приветствию первого запуска). Ярлык свой у каждого
/// пользователя — Start Menu\Programs в профиле, как задача автозапуска (XIC-23). Состояние —
/// сам файл, поля в конфиге нет: удалили ярлык руками — тумблер это увидит.
/// Запуск по ярлыку передаёт <see cref="ShowArg"/>: окно настроек открывается сразу, а уже
/// работающий экземпляр поднимает своё (<see cref="InstanceWake"/>).
/// </summary>
public static class StartMenuShortcut
{
    /// <summary>Аргумент запуска по ярлыку: сразу открыть окно настроек.</summary>
    public const string ShowArg = "--show";

    // имя файла не зависит от языка интерфейса: иначе смена языка осиротила бы ярлык
    private static string LinkPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "XiControl.lnk");

    private static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");
    private const int MaxPath = 1024;

    public static bool Exists() => File.Exists(LinkPath);

    /// <summary>Создать ярлык на текущий exe или убрать его. false — не вышло, причина в журнале.</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            if (enabled) Write(Environment.ProcessPath!, LinkPath); // у обычного exe-процесса путь есть всегда
            else File.Delete(LinkPath);                              // файла нет — Delete молча проходит
            return true;
        }
        catch (Exception ex) { Log.Ex("StartMenuShortcut.Set", ex); return false; }
    }

    /// <summary>
    /// Самопочинка, как у автозапуска: ярлык есть, но ведёт на пропавший exe или на сборку старее
    /// текущей — переписываем на себя. Ярлык на ДРУГУЮ папку той же версии не трогаем: две
    /// portable-копии иначе перехватывали бы его друг у друга на каждом старте. Ярлыка нет —
    /// не создаём: без спроса в профиль не пишем. Вызывается один раз на старте.
    /// </summary>
    public static void RepairIfBroken()
    {
        if (Environment.ProcessPath is not { } exe || !Exists()) return;
        if (ReadLink(LinkPath)?.Target is not { Length: > 0 } target) return; // не разобрали — чужое не переписываем

        bool alive = File.Exists(target);
        string? version = null;
        if (alive)
        {
            try { version = FileVersionInfo.GetVersionInfo(target).FileVersion; }
            catch (Exception ex) { Log.Ex("StartMenuShortcut.FileVersion", ex); return; }
        }
        if (!NeedsRepair(alive, version, System.Reflection.Assembly.GetExecutingAssembly().GetName().Version))
            return;

        Log.Write($"StartMenuShortcut: ярлык ведёт на {(alive ? $"старую сборку ({version})" : "пропавший exe")} " +
                  $"{target} — переписываю на текущий");
        try { Write(exe, LinkPath); }
        catch (Exception ex) { Log.Ex("StartMenuShortcut.Repair", ex); }
    }

    /// <summary>Пора ли переписать ярлык: цель пропала или старее нас. Дев-сборки в сверке версий
    /// не участвуют — правило общее с задачей автозапуска (<see cref="AutoStart.IsOutdated"/>).</summary>
    internal static bool NeedsRepair(bool targetExists, string? targetVersion, Version? current)
        => !targetExists || AutoStart.IsOutdated(targetVersion, current);

    // путь ярлыка — параметром ради теста: он пишет и читает .lnk во временной папке и так ловит
    // сдвиг слотов vtable, которого компилятор не видит (COM-вызов просто попал бы не в тот метод)
    internal static void Write(string exe, string linkPath)
    {
        var link = NewLink();
        try
        {
            link.SetPath(exe);
            link.SetArguments(ShowArg);
            link.SetWorkingDirectory(Path.GetDirectoryName(exe)!);
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            ((IPersistFile)link).Save(linkPath, true);
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    /// <summary>Цель и аргументы ярлыка; null — не прочитали (причина в журнале).</summary>
    internal static (string Target, string Args)? ReadLink(string linkPath)
    {
        var link = NewLink();
        try
        {
            ((IPersistFile)link).Load(linkPath, 0); // STGM_READ
            var buf = new char[MaxPath];
            link.GetPath(buf, buf.Length, 0, 0);
            string target = Text(buf);
            link.GetArguments(buf, buf.Length);
            return (target, Text(buf));
        }
        catch (Exception ex) { Log.Ex("StartMenuShortcut.ReadLink", ex); return null; }
        finally { Marshal.ReleaseComObject(link); }

        static string Text(char[] b) => new string(b, 0, Math.Max(0, Array.IndexOf(b, '\0')));
    }

    private static IShellLinkW NewLink() =>
        (IShellLinkW)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ShellLink, throwOnError: true)!)!;

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] pszFile, int cch,
                     nint pfd, uint fFlags);                                           // слот 0
        void f1(); void f2(); void f3(); void f4(); void f5();                         // слоты 1..5 (не используем)
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);     // слот 6
        void GetArguments([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] pszArgs,
                          int cch);                                                    // слот 7
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);           // слот 8
        void f9(); void f10(); void f11(); void f12(); void f13(); void f14(); void f15(); void f16(); // 9..16
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);                // слот 17
    }
}
