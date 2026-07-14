using System.Text.Json;
using System.Text.Json.Serialization;
using XiControl.Localization;
using XiControl.Wmi;

namespace XiControl.Config;

/// <summary>Настройки приложения. Хранятся в %APPDATA%\XiControl\config.json.</summary>
public sealed class AppConfig
{
    public Lang Language { get; set; } = Lang.Ru;
    public bool ChargeCare { get; set; } = false;
    public bool AutoStart { get; set; } = false;

    /// <summary>
    /// Восстанавливать выбранный режим производительности после перезагрузки.
    /// Пока выключено — режим в конфиг не пишется (не тратим ресурс SSD на каждое переключение).
    /// </summary>
    public bool RestoreMode { get; set; } = false;

    /// <summary>
    /// Режим, применяемый при старте (когда RestoreMode = true). Обновляется при каждой смене
    /// режима, пока опция включена. При выключении опции не удаляется и не обновляется — при
    /// повторном включении всё вернётся как было до отключения.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PerfMode? StartPerfMode { get; set; }

    /// <summary>
    /// Принудительный режим при каждой загрузке — задаётся только правкой config.json. Работает
    /// лишь когда RestoreMode = false: каждый старт включается этот режим, с какого бы ни выключились.
    /// Значения: "Quiet", "Turbo", "FullSpeed", "Auto", "Eco". Убрать — null или удалить строку.
    /// Если режим сейчас недоступен (напр. Full-speed на батарее) — включится Auto.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PerfMode? ForceStartMode { get; set; }

    /// <summary>
    /// Показывать скрытый режим Эко (0x0A) в меню, панели и цикле Mi-кнопки.
    /// Настраивается только правкой config.json (перезапуск).
    /// </summary>
    public bool EcoMode { get; set; } = true;

    /// <summary>
    /// Показывать режим «Полная мощность» (0x04). false — режим убирается из UI
    /// и включить его из приложения нельзя. Только правкой config.json (перезапуск).
    /// </summary>
    public bool FullSpeedMode { get; set; } = true;

    /// <summary>
    /// «Режим совы» как фича: показывать ячейку в панели и пункт меню.
    /// false — скрыть полностью (и выключить активный режим при старте).
    /// </summary>
    public bool OwlMode { get; set; } = true;

    /// <summary>
    /// Авто-герцовка: при подключении зарядки экран переводится на AcRefreshRate Гц,
    /// при отключении — на BatteryRefreshRate. Переключается в меню трея и в панели.
    /// </summary>
    public bool AutoRefreshRate { get; set; } = false;

    /// <summary>Частота экрана (Гц) от сети. Настраивается только правкой config.json.</summary>
    public int AcRefreshRate { get; set; } = 120;

    /// <summary>Частота экрана (Гц) от батареи. Настраивается только правкой config.json.</summary>
    public int BatteryRefreshRate { get; set; } = 60;

    /// <summary>Режим «Не спать» активен: экран не гаснет, сна нет; крышка на AC — «ничего не делать».</summary>
    public bool Awake { get; set; } = false;

    /// <summary>Исходное действие крышки (AC) до включения «Не спать» — для восстановления, в т.ч. после сбоя.</summary>
    public int? AwakeSavedLidAc { get; set; }

    /// <summary>Позиция окна «Монитор» (виджет перетаскивается мышью); null — по центру.</summary>
    public int? MonitorX { get; set; }
    public int? MonitorY { get; set; }

    /// <summary>
    /// Вид виджета «Монитор»: null/"full" — полный с графиками, "mini" — три индикатора
    /// Power/CPU/RAM в строку без графиков, "power" — только ватты. Переключается кнопкой
    /// в самом виджете или двойным кликом по нему; выбор сохраняется автоматически.
    /// </summary>
    public string? MonitorView { get; set; }

    /// <summary>
    /// Действие клавиши «настройки»: "charge" (по умолчанию) — переключение заряда
    /// 80/100, "settings" — открыть Параметры Windows (как в ранних версиях).
    /// </summary>
    public string? SettingsKey { get; set; }

    /// <summary>
    /// Раскладка кликов Mi-кнопки. "modes" (по умолчанию): одинарный — цикл режимов,
    /// двойной — заряд 80/100. "charge" — инверсия: одинарный — заряд, двойной — режимы.
    /// Удержание всегда открывает панель.
    /// </summary>
    public string? MiShortPress { get; set; }

    /// <summary>
    /// Двойной клик Mi-кнопки. false — жест отключён, одинарный клик срабатывает
    /// мгновенно (без окна ожидания ~300 мс).
    /// </summary>
    public bool MiDoubleClick { get; set; } = true;

    /// <summary>
    /// Что запускать AI-клавишей: путь к exe/файлу/URL (поддерживаются %ПЕРЕМЕННЫЕ%).
    /// Пусто → Copilot (Win+C). Настраивается только правкой config.json.
    /// </summary>
    public string? AiKeyProgram { get; set; }

    /// <summary>Аргументы командной строки для AiKeyProgram (опционально).</summary>
    public string? AiKeyArgs { get; set; }

    [JsonIgnore]
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XiControl");

    [JsonIgnore]
    private static string FilePath => Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath)) ?? Fresh();
        }
        catch (Exception ex) { Log.Ex("AppConfig.Load", ex); /* повреждённый конфиг → дефолт */ }
        return Fresh();
    }

    /// <summary>Конфиг для первого старта (файла нет / повреждён): язык — по языку ОС.</summary>
    private static AppConfig Fresh() => new() { Language = DetectOsLanguage() };

    /// <summary>Язык интерфейса по языку Windows: ru→Ru, zh→Zh, всё остальное (вкл. en)→En.</summary>
    private static Lang DetectOsLanguage() =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
        {
            "ru" => Lang.Ru,
            "zh" => Lang.Zh,
            _ => Lang.En,
        };

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex) { Log.Ex("AppConfig.Save", ex); /* не критично */ }
    }

    /// <summary>Запомнить режим для восстановления — только если опция включена и значение изменилось (бережём SSD).</summary>
    public void RememberMode(PerfMode mode)
    {
        if (!RestoreMode || StartPerfMode == mode) return;
        StartPerfMode = mode;
        Save();
    }
}
