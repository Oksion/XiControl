using System.Text.Json;
using XiControl.Localization;

namespace XiControl.Config;

/// <summary>JSON-хранилище конфига: config.json в каталоге данных (<see cref="AppPaths"/> —
/// %APPDATA%\XiControl либо папка программы в портативном режиме; явная папка — для тестов).</summary>
public sealed class JsonConfigStore : IConfigStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _dir;

    /// <summary>Полный путь к файлу — показываем его на вкладке «О программе».</summary>
    public string FilePath => Path.Combine(_dir, "config.json");

    /// <param name="dir">Папка хранения; null — выбранная <see cref="AppPaths"/>.</param>
    public JsonConfigStore(string? dir = null) => _dir = dir ?? AppPaths.DataDir;

    public AppConfig Load()
    {
        AppConfig cfg;
        try
        {
            cfg = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath)) ?? Fresh()
                : Fresh();
        }
        catch (Exception ex) { Log.Ex("JsonConfigStore.Load", ex); cfg = Fresh(); /* повреждённый конфиг → дефолт */ }
        cfg.MigrateKeyActions();
        cfg.Store = this; // теперь cfg.Save() пишет через этот store
        return cfg;
    }

    public void Save(AppConfig cfg)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(cfg, JsonOpts));
        }
        catch (Exception ex) { Log.Ex("JsonConfigStore.Save", ex); /* не критично */ }
    }

    /// <summary>Конфиг для первого старта (файла нет / повреждён): язык — по языку ОС.</summary>
    private static AppConfig Fresh() => new() { Language = DetectOsLanguage() };

    /// <summary>Язык интерфейса по языку Windows: берём его культуру, если перевод есть,
    /// иначе базовый (Loc.Resolve) — новый язык подхватится автоматически, без правок здесь.</summary>
    private static string DetectOsLanguage() =>
        Loc.Resolve(System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
}
