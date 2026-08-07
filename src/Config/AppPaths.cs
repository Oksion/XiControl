namespace XiControl.Config;

/// <summary>
/// Где живут данные приложения (XIC-34). По умолчанию — %APPDATA%\XiControl, но рядом с exe
/// можно развернуть ПОРТАТИВНУЮ копию: настройки и лог лягут туда же, и папку с программой
/// можно унести на другой диск или пережить переустановку Windows, ничего не потеряв.
///
/// Портативный режим включается двумя способами: рядом с exe уже лежит config.json (значит
/// пользователь его туда положил осознанно) или лежит файл-метка (.portable / portable.txt).
/// Каталог программы бывает недоступен для записи (Program Files, WinGet\Packages) — тогда
/// честно откатываемся в %APPDATA%, иначе установленная копия молча теряла бы настройки.
///
/// api.json (HTTP API) сюда НЕ относится: он остаётся в %ProgramData% под ACL «запись только
/// администраторам» — на этом держится то, что API нельзя включить правкой пользовательского
/// конфига (XIC-13).
/// </summary>
public static class AppPaths
{
    /// <summary>Имена файла-метки. `.portable` — как в VS Code / Windows Terminal; `portable.txt`
    /// обязателен потому, что Проводник Windows не даёт создать файл с именем, начинающимся
    /// с точки, — без второго варианта фича упёрлась бы в юзабилити на ровном месте.</summary>
    public static readonly string[] MarkerNames = [".portable", "portable.txt"];

    private static readonly Lazy<Resolution> Resolved = new(ResolveCurrent,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Каталог данных (config.json, log.txt).</summary>
    public static string DataDir => Resolved.Value.Dir;

    /// <summary>Работаем рядом с exe.</summary>
    public static bool Portable => Resolved.Value.Portable;

    /// <summary>
    /// Почему портативный режим не включился, хотя метка есть (каталог не пишется), либо null.
    /// Отдельным полем, а не записью в лог: <see cref="Log"/> сам спрашивает <see cref="DataDir"/>,
    /// и запись во время инициализации вошла бы в рекурсию. Печатает Program после старта.
    /// </summary>
    public static string? FallbackReason => Resolved.Value.FallbackReason;

    /// <summary>Каталог рядом с exe (для single-file публикации Assembly.Location пуст —
    /// поэтому только ProcessPath).</summary>
    public static string? ExeDir =>
        Environment.ProcessPath is { } p ? Path.GetDirectoryName(p) : null;

    /// <summary>Стандартный каталог данных пользователя.</summary>
    public static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XiControl");

    /// <summary>Результат выбора каталога.</summary>
    public readonly record struct Resolution(string Dir, bool Portable, string? FallbackReason);

    /// <summary>
    /// Чистая логика выбора — вся ветвистость здесь, ввод-вывод передаётся делегатами
    /// (поэтому покрывается юнит-тестами без реальных папок).
    /// </summary>
    /// <param name="exeDir">Каталог программы; null — неизвестен (тогда только AppData).</param>
    /// <param name="appDataDir">Стандартный каталог пользователя.</param>
    /// <param name="fileExists">Проверка наличия файла.</param>
    /// <param name="canWrite">Проверка «в каталог можно писать».</param>
    public static Resolution Resolve(string? exeDir, string appDataDir,
        Func<string, bool> fileExists, Func<string, bool> canWrite)
    {
        if (string.IsNullOrEmpty(exeDir)) return new(appDataDir, false, null);

        bool hasConfig = fileExists(Path.Combine(exeDir, "config.json"));
        bool hasMarker = MarkerNames.Any(m => fileExists(Path.Combine(exeDir, m)));
        if (!hasConfig && !hasMarker) return new(appDataDir, false, null);

        // каталог программы может быть только для чтения — тогда портативность невозможна
        if (!canWrite(exeDir))
            return new(appDataDir, false,
                $"каталог программы недоступен для записи ({exeDir}) — настройки остаются в {appDataDir}");

        return new(exeDir, true, null);
    }

    private static Resolution ResolveCurrent()
    {
        try
        {
            var r = Resolve(ExeDir, AppDataDir, File.Exists, CanWrite);
            // метка есть, конфига рядом ещё нет — переносим накопленные настройки, чтобы
            // включение портативного режима не выглядело как «сбросились все настройки»
            if (r.Portable) SeedFromAppData(r.Dir);
            return r;
        }
        catch (Exception ex)
        {
            // любая неожиданность в определении путей не должна мешать запуску
            return new(AppDataDir, false, $"не удалось определить каталог данных: {ex.Message}");
        }
    }

    // Проба записи надёжнее разбора ACL: интересует факт, а не теоретические права
    private static bool CanWrite(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, $".write-probe-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private static void SeedFromAppData(string portableDir)
    {
        try
        {
            string target = Path.Combine(portableDir, "config.json");
            string source = Path.Combine(AppDataDir, "config.json");
            if (!File.Exists(target) && File.Exists(source)) File.Copy(source, target);
        }
        catch { /* не скопировалось — стартуем с дефолтов, это не повод падать */ }
    }
}
