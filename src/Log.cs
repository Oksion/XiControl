namespace XiControl;

/// <summary>
/// Мини-лог log.txt в каталоге данных (%APPDATA%\XiControl либо папка программы в портативном
/// режиме — см. <see cref="Config.AppPaths"/>) — чтобы «у кого-то не работает» можно было
/// разобрать без отладчика. Ошибки самого лога глотаются.
/// </summary>
internal static class Log
{
    private static readonly object Sync = new();

    /// <summary>Путь к журналу; вычисляется лениво — каталог данных определяется при первом
    /// обращении, а до него лог уже мог понадобиться.</summary>
    internal static string LogPath => Path.Combine(Config.AppPaths.DataDir, "log.txt");

    /// <summary>Опция «Логирование» (AppConfig.LogEnabled): false — не пишем вообще ничего.
    /// До загрузки конфига — включено, чтобы не потерять ошибки самого старта.</summary>
    public static volatile bool Enabled = true;

    public static void Ex(string where, Exception ex) => Write($"{where}: {ex.GetType().Name}: {ex.Message}");

    public static void Write(string message)
    {
        if (!Enabled) return;
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 256 * 1024)
                    File.Delete(LogPath); // простая ротация: начать заново
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { /* лог не должен ронять приложение */ }
    }
}
