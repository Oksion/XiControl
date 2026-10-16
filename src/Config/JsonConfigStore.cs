using System.Text.Json;
using System.Text.Json.Serialization;
using XiControl.Localization;

namespace XiControl.Config;

/// <summary>JSON-хранилище конфига: config.json в каталоге данных (<see cref="AppPaths"/> —
/// %APPDATA%\XiControl либо папка программы в портативном режиме; явная папка — для тестов).</summary>
public sealed class JsonConfigStore : IConfigStore
{
    // Перечисления пишем именами, а не числами: config.json правят руками, и "Balance" в нём
    // понятнее, чем 1. Отдельные свойства уже помечены [JsonStringEnumConverter] и от этого не
    // меняются — глобальный конвертер нужен коллекциям (HiddenModes), где атрибут на свойстве
    // относился бы к списку, а не к его элементам. Чтение остаётся терпимым: имена и числа.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

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
            // JsonOpts обязателен и на чтении: без него имена перечислений («Balance»),
            // которыми мы теперь пишем, не разбираются — и весь конфиг молча уехал бы в дефолты.
            cfg = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), JsonOpts) ?? Fresh()
                : Fresh();
        }
        catch (Exception ex) { Log.Ex("JsonConfigStore.Load", ex); cfg = Fresh(); /* повреждённый конфиг → дефолт */ }
        cfg.MigrateKeyActions();
        cfg.Store = this; // теперь cfg.Save() пишет через этот store
        return cfg;
    }

    // Сохраняют и UI-поток, и пул (PowerModeSync, самолечение порога заряда, RestoreAfterBoot):
    // две одновременные записи одного файла давали IOException, и одна правка терялась. Под
    // замком каждая запись сериализует весь текущий конфиг — последняя несёт обе правки.
    private readonly object _saveLock = new();

    public void Save(AppConfig cfg)
    {
        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(cfg, JsonOpts));
            }
            catch (Exception ex) { Log.Ex("JsonConfigStore.Save", ex); /* не критично */ }
        }
    }

    /// <summary>Конфиг для первого старта (файла нет / повреждён): язык — по языку ОС.</summary>
    private static AppConfig Fresh() => new() { Language = DetectOsLanguage() };

    /// <summary>Язык интерфейса по языку Windows: берём его культуру, если перевод есть,
    /// иначе базовый (Loc.Resolve) — новый язык подхватится автоматически, без правок здесь.</summary>
    private static string DetectOsLanguage() =>
        Loc.Resolve(System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
}
