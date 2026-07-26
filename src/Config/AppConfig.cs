using System.Text.Json.Serialization;
using XiControl.Wmi;

namespace XiControl.Config;

/// <summary>Настройки приложения. Хранятся в %APPDATA%\XiControl\config.json.</summary>
public sealed class AppConfig
{
    /// <summary>Язык интерфейса — культурный код (напр. «ru»); null = определить по языку ОС.
    /// Старые конфиги хранили индекс (0/1/2) — мигрируется конвертером.</summary>
    [JsonConverter(typeof(LegacyLanguageConverter))]
    public string? Language { get; set; }

    /// <summary>Одноразовая подсказка при первом запуске уже показана (balloon-tip трея).</summary>
    public bool FirstRunShown { get; set; } = false;

    /// <summary>Тема панелей/OSD/«Монитора»: null — тёмная (исторический вид),
    /// "light" — светлая, "system" — следовать теме приложений Windows.</summary>
    public string? FlyoutTheme { get; set; }

    public bool ChargeCare { get; set; } = false;
    public bool AutoStart { get; set; } = false;

    /// <summary>Логировать ошибки и проблемы в %APPDATA%\XiControl\log.txt.
    /// false — не пишется вообще ничего (диагностика по отчётам станет невозможна).</summary>
    public bool LogEnabled { get; set; } = true;

    /// <summary>
    /// «В дорогу»: временный оверрайд «зарядить до 100%» поверх «беречь ~80%» (ChargeCare).
    /// Держит TrayApp: при включении заряжаем до 100%, по достижении 100% — OSD (+звук),
    /// при отключении зарядника режим сам сбрасывается (следующее подключение — снова 80%).
    /// Имеет смысл только при ChargeCare=true; при постоянном 100% кнопка неактивна.
    /// </summary>
    public bool TravelMode { get; set; } = false;

    /// <summary>Проигрывать джингл при достижении 100% в режиме «В дорогу».</summary>
    public bool TravelSound { get; set; } = true;

    /// <summary>Джингл при переключении «В дорогу» с клавиши на заблокированном экране
    /// (OSD под локскрином не виден — «слепая» обратная связь, XIC-11).</summary>
    public bool TravelLockSound { get; set; } = true;

    /// <summary>Toast-уведомление при переключении «В дорогу» на заблокированном экране.
    /// Показ содержимого на локскрине управляется настройками уведомлений Windows.</summary>
    public bool TravelLockToast { get; set; } = true;

    /// <summary>Свой WAV для звука готовности «В дорогу» (поддерживаются `%ПЕРЕМЕННЫЕ%`).
    /// Пусто или файл не найден → встроенный джингл. Только WAV/PCM. Правится в config.json.</summary>
    public string? TravelSoundFile { get; set; }

    /// <summary>Показывать мощность подключённого адаптера (Вт) в OSD при подключении зарядки.
    /// Мощность сообщают только PD-адаптеры; обычный USB/не-PD → 0 (в OSD не дописывается).
    /// Читается driver-free через MIFS (та же группа, что защита заряда).</summary>
    public bool ChargerWattsOsd { get; set; } = true;

    /// <summary>Порог «слабого зарядника», Вт: подключён PD-БП мощностью в диапазоне (0; порог) →
    /// OSD с иконкой-предупреждением, что заряд будет медленным. 0 — предупреждение выключено.</summary>
    public int WeakChargerWatts { get; set; } = 60;

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
    /// «Профили питания»: при подключении сети применяется AcPerfMode, при переходе на батарею —
    /// BatteryPerfMode; яркость экрана запоминается и восстанавливается отдельно для каждого
    /// состояния (RememberBrightness). Взаимоисключающе с RestoreMode/ForceStartMode — на старте
    /// и при каждой смене питания режим задаёт именно этот профиль. Держит PowerProfileGuard.
    /// </summary>
    public bool PowerProfiles { get; set; } = false;

    /// <summary>Режим при питании от сети; null — «не менять». Выбирается в меню.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PerfMode? AcPerfMode { get; set; }

    /// <summary>Режим при питании от батареи; null — «не менять». Выбирается в меню.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PerfMode? BatteryPerfMode { get; set; }

    /// <summary>Запоминать и восстанавливать яркость экрана отдельно для сети и батареи.
    /// Самостоятельная опция (в «Общих»): работает и без «Профилей питания» — держит её
    /// PowerProfileGuard независимо. По умолчанию выкл: утилита перебивает яркость Windows
    /// только по явному выбору пользователя.</summary>
    public bool RememberBrightness { get; set; } = false;

    /// <summary>Запомненная яркость экрана (0–100) от сети; null — ещё не запомнена.</summary>
    public int? AcBrightness { get; set; }

    /// <summary>Запомненная яркость экрана (0–100) от батареи; null — ещё не запомнена.</summary>
    public int? BatteryBrightness { get; set; }

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
    /// «Управление частотой экрана» как фича: пункт меню, ячейка панели и вкладка «Экран»
    /// в настройках. false — прошивку/экран не трогаем совсем (авто-герцовка не применяется),
    /// вся ветка скрыта из UI (как OwlMode у совы) — для тех, кому герцовка не нужна.
    /// </summary>
    public bool RefreshRateFeature { get; set; } = true;

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

    // ---- Тайминги (мс) ----
    // Правятся только в config.json (UI нет); применяются при следующем запуске.
    // Дефолты = историческим константам; при чтении клэмпятся снизу, чтобы кривое
    // значение не сломало жест / не сделало OSD мгновенным.

    /// <summary>Порог «долгого» нажатия Mi-кнопки (открывает панель). Дефолт 400 мс.</summary>
    public int MiHoldMs { get; set; } = 400;

    /// <summary>Окно ожидания второго клика Mi-кнопки. Дефолт 300 мс.</summary>
    public int MiDoubleClickMs { get; set; } = 300;

    /// <summary>Сколько OSD висит до затухания. Дефолт 2800 мс («Авто»-режим — на 600 мс дольше).</summary>
    public int OsdDurationMs { get; set; } = 2800;

    // ---- Действия клавиш ----
    // На каждый слот — своё действие из общего списка: "modes" (цикл режимов), "charge"
    // (заряд 80/100), "panel" (быстрая панель), "owl", "monitor", "travel", "projection"
    // (Win+P), "settings" (Параметры Windows), "copilot" (Win+C), "launch" (команда из
    // соответствующего *Command), "none". null → дефолт слота (см. MigrateKeyActions).

    /// <summary>Одиночный клик Mi-кнопки (дефолт "modes"). Удержание всегда открывает панель.</summary>
    public string? MiClickAction { get; set; }

    /// <summary>Двойной клик Mi-кнопки (дефолт "charge"). "none" — жест отключён,
    /// одиночный клик срабатывает мгновенно (без окна ожидания ~300 мс).</summary>
    public string? MiDoubleAction { get; set; }

    /// <summary>Клавиша «настройки» (шестерёнка), дефолт "charge". При открытой панели — всегда заряд.</summary>
    public string? SettingsKeyAction { get; set; }

    /// <summary>AI-клавиша (дефолт "copilot").</summary>
    public string? AiKeyAction { get; set; }

    /// <summary>Клавиша «проекция» (дефолт "projection").</summary>
    public string? ProjKeyAction { get; set; }

    /// <summary>Команды для действия "launch": путь к exe/файлу/URL + аргументы
    /// (поддерживаются %ПЕРЕМЕННЫЕ%; путь с пробелами — в кавычках).</summary>
    public string? MiClickCommand { get; set; }
    public string? MiDoubleCommand { get; set; }
    public string? SettingsKeyCommand { get; set; }
    public string? AiKeyCommand { get; set; }
    public string? ProjKeyCommand { get; set; }

    /// <summary>
    /// «Управление тачпадом» как фича: ячейка в панели и действие для клавиш.
    /// false — ячейка скрыта, действие «touchpad» не срабатывает (как OwlMode у совы).
    /// </summary>
    public bool TouchpadFeature { get; set; } = true;

    /// <summary>ID узла тачпада — запоминается автоматически при первом обнаружении.
    /// Нужен, чтобы включить тачпад обратно после перезапуска приложения: у выключенного
    /// тачпада HID-коллекции исчезают из системы и найти его иначе нечем. Не редактировать.</summary>
    public string? TouchpadDeviceId { get; set; }

    /// <summary>Тачпад отключён персистентным путём (мягкое отключение не сработало —
    /// фолбэк через SetupAPI, переживает перезагрузку). По этому флагу приложение на
    /// старте включает тачпад само. Ставится/снимается автоматически.</summary>
    public bool TouchpadPersistOff { get; set; }

    /// <summary>
    /// «Управление сенсорным экраном» как фича: ячейка в панели и действие для клавиш.
    /// false — ячейка скрыта, действие «touchscreen» не срабатывает (как OwlMode у совы).
    /// </summary>
    public bool TouchscreenFeature { get; set; } = true;

    /// <summary>ID узла сенсорного экрана — запоминается автоматически при первом обнаружении.
    /// Нужен, чтобы включить экран обратно после перезапуска приложения: у выключенного
    /// экрана HID-коллекции исчезают из системы и найти его иначе нечем. Не редактировать.</summary>
    public string? TouchscreenDeviceId { get; set; }

    /// <summary>Сенсорный экран отключён персистентным путём (мягкое отключение не сработало —
    /// фолбэк через SetupAPI, переживает перезагрузку). По этому флагу приложение на
    /// старте включает экран само. Ставится/снимается автоматически.</summary>
    public bool TouchscreenPersistOff { get; set; }

    // ---- Устаревшие поля клавиш (до v0.8) — читаются только для миграции ----

    /// <summary>Устаревшее: см. SettingsKeyAction. "charge"/"settings".</summary>
    public string? SettingsKey { get; set; }

    /// <summary>Устаревшее: см. MiClickAction/MiDoubleAction. "modes"/"charge".</summary>
    public string? MiShortPress { get; set; }

    /// <summary>Устаревшее: см. MiDoubleAction ("none" = выключен).</summary>
    public bool MiDoubleClick { get; set; } = true;

    /// <summary>Устаревшее: см. AiKeyAction="launch" + AiKeyCommand.</summary>
    public string? AiKeyProgram { get; set; }

    /// <summary>Устаревшее: аргументы для AiKeyProgram.</summary>
    public string? AiKeyArgs { get; set; }

    // Persistence живёт за IConfigStore (JsonConfigStore); ссылку ставит store при Load.
    // Поле (не свойство) — System.Text.Json игнорирует поля при (де)сериализации.
    internal IConfigStore? Store;

    /// <summary>
    /// Заполнить пустые действия клавиш дефолтами, перенеся старые опции (MiShortPress,
    /// MiDoubleClick, SettingsKey, AiKeyProgram/Args). На диск не пишем — сохранится при
    /// следующем изменении настроек. Публично для инструментов предпросмотра.
    /// </summary>
    public void MigrateKeyActions()
    {
        const string Charge = "charge";
        if (MiClickAction is null)
        {
            bool chargeFirst = string.Equals(MiShortPress, Charge, StringComparison.OrdinalIgnoreCase);
            MiClickAction = chargeFirst ? Charge : "modes";
            MiDoubleAction ??= !MiDoubleClick ? "none" : (chargeFirst ? "modes" : Charge);
        }
        MiDoubleAction ??= Charge;
        SettingsKeyAction ??= string.Equals(SettingsKey, "settings", StringComparison.OrdinalIgnoreCase)
            ? "settings" : Charge;
        if (AiKeyAction is null)
        {
            if (!string.IsNullOrWhiteSpace(AiKeyProgram))
            {
                // путь с пробелами берём в кавычки — AiKeyCommand хранит команду одной строкой
                var p = AiKeyProgram.Trim();
                var cmd = p.Contains(' ') ? $"\"{p}\"" : p;
                if (!string.IsNullOrWhiteSpace(AiKeyArgs)) cmd += " " + AiKeyArgs.Trim();
                AiKeyAction = "launch";
                AiKeyCommand ??= cmd;
            }
            else AiKeyAction = "copilot";
        }
        ProjKeyAction ??= "projection";
    }

    /// <summary>Сохранить через привязанный store. Без store (голый POCO в тестах) — no-op.</summary>
    public void Save() => Store?.Save(this);

    /// <summary>Запомнить режим для восстановления — только если опция включена и значение изменилось (бережём SSD).</summary>
    public void RememberMode(PerfMode mode)
    {
        if (!RestoreMode || StartPerfMode == mode) return;
        StartPerfMode = mode;
        Save();
    }
}
