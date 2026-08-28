using System.Text.Json.Serialization;
using XiControl.Wmi;

namespace XiControl.Config;

/// <summary>Якорная точка кривой авто-яркости: «при таком свете — такая яркость» (XIC-30).</summary>
public sealed class BrightnessPoint
{
    public float Lux { get; set; }
    public int Percent { get; set; }
}

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

    /// <summary>
    /// Порог «беречь батарею», % — значение X, когда ChargeCare=true. Пресеты 40/50/60/70/80
    /// (см. <see cref="Mifs.ChargeCarePresets"/>); панель/меню переключают ChargeCare между этим X
    /// и 100%. Дефолт 80 = прежнее поведение, поэтому старые config.json мигрируются автоматически
    /// (поля нет → дефолт 80). Невалидное (руками правленное) значение гасится фолбэком к 80 на месте.
    /// </summary>
    public int CareLimitPercent { get; set; } = Mifs.ChargeThresholdPercent;

    public bool AutoStart { get; set; } = false;

    /// <summary>Логировать ошибки и проблемы в %APPDATA%\XiControl\log.txt.
    /// false — не пишется вообще ничего (диагностика по отчётам станет невозможна).</summary>
    public bool LogEnabled { get; set; } = true;

    /// <summary>
    /// Проверять выход новых версий (один HTTPS-запрос к GitHub, не чаще раза в сутки).
    /// false — приложение не стучится в сеть вообще: тумблер и есть выключатель трафика,
    /// поэтому отдельного «offline»-флага в конфиге нет.
    /// </summary>
    public bool CheckUpdates { get; set; } = true;

    /// <summary>Версия, про которую уже показывали тост, — повторно не напоминаем.
    /// Про следующую напомним. Отметка на «О программе» остаётся в любом случае.</summary>
    public string? SkippedVersion { get; set; }

    /// <summary>Когда последний раз ходили за релизом (UTC). Бережём чужой сервис и свой старт:
    /// приложение перезапускается часто, «раз за запуск» лупило бы по GitHub без нужды.</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

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
    /// Самостоятельная опция (вкладка «Экран»): работает и без «Профилей питания» — держит её
    /// PowerProfileGuard независимо. По умолчанию выкл: утилита перебивает яркость Windows
    /// только по явному выбору пользователя.</summary>
    public bool RememberBrightness { get; set; } = false;

    /// <summary>
    /// Ограничивать яркость экрана (XIC-29, бережёт OLED от выгорания): выше лимита яркость
    /// плавно сводится обратно. Заблокировать сам ползунок Windows невозможно — только вернуть
    /// после факта, о чём честно сказано в описании настройки. Держит BrightnessCapGuard.
    /// </summary>
    public bool BrightnessCapEnabled { get; set; } = false;

    /// <summary>Лимит яркости (%) при питании от сети.</summary>
    public int BrightnessCapAc { get; set; } = 80;

    /// <summary>Лимит яркости (%) при питании от батареи.</summary>
    public int BrightnessCapBattery { get; set; } = 60;

    // Тайминги лимита яркости — только правкой config.json (в UI не выносим, дефолты согласованы
    // в XIC-29). Кривые значения клэмпит BrightnessCapGuard при чтении.

    /// <summary>Длительность плавного хода яркости на весь путь, мс: интервал шага = длительность /
    /// дельта, поэтому спуск 100→70 и 75→70 занимает одинаковое время. Дефолт 10 с.</summary>
    public int BrightnessRampMs { get; set; } = 10_000;

    /// <summary>Интервал между шагами схождения к лимиту. Дефолт 1 мин.</summary>
    public int BrightnessConvergeMs { get; set; } = 60_000;

    /// <summary>Пауза после повторного подъёма яркости пользователем («мне правда нужно ярче»), минут.
    /// Сбрасывается блокировкой, сном, сменой питания и перезапуском. Дефолт 2 часа.</summary>
    public int BrightnessBackoffMin { get; set; } = 120;

    /// <summary>Делитель разрыва при схождении: за шаг сокращаем (яркость − лимит) во столько раз.</summary>
    public int BrightnessGapDivisor { get; set; } = 2;

    /// <summary>Порог схождения, %: если до лимита осталось не больше — доводим сразу
    /// (иначе гонялись бы за половинками бесконечно).</summary>
    public int BrightnessSnapPercent { get; set; } = 2;

    /// <summary>
    /// Авто-яркость по датчику освещённости (XIC-30): экран следует за светом по обучаемой
    /// кривой AutoBrightnessPoints. Взаимоисключается с RememberBrightness (кривая заменяет
    /// слоты). Требует датчика и выключенной адаптивной яркости Windows. Держит
    /// AutoBrightnessGuard.
    /// </summary>
    public bool AutoBrightness { get; set; }

    /// <summary>Якорные точки кривой lux → % при питании от сети (обучаются правками
    /// пользователя; при первом включении сеются дефолтом из BrightnessCurve.DefaultPoints).
    /// Кривых две — от сети и от батареи: комфортная яркость в одних и тех же люксах
    /// у розетки и в дороге разная. Не редактировать руками без нужды.</summary>
    public List<BrightnessPoint> AutoBrightnessPointsAc { get; set; } = [];

    /// <summary>Якорные точки кривой lux → % при питании от батареи.</summary>
    public List<BrightnessPoint> AutoBrightnessPointsBattery { get; set; } = [];

    // Тонкие настройки авто-яркости — только правкой config.json.

    /// <summary>Мёртвая зона, %: не трогаем яркость, если предсказание отличается меньше.</summary>
    public int AutoBrightnessDeadband { get; set; } = 5;

    /// <summary>Стабилизация света, мс: реагируем, когда освещённость устоялась. Дефолт 2 с.</summary>
    public int AutoBrightnessSettleMs { get; set; } = 2000;

    /// <summary>«Период раздумья» ручной правки, мс: пользователь докрутил и остановился —
    /// значение становится обучающей точкой. Дефолт 5 с.</summary>
    public int AutoBrightnessLearnMs { get; set; } = 5000;

    /// <summary>Гистерезис значимости света в лог-шкале (0.1 ≈ ±26% люксов).</summary>
    public double AutoBrightnessHysteresis { get; set; } = 0.1;

    /// <summary>«Инерция» датчика, сек (есть в UI): решения принимаются по медиане люксов за это
    /// окно — случайный блик её не сдвигает. 0 — фильтр выключен (мгновенные значения).</summary>
    public int AutoBrightnessMedianSec { get; set; } = 10;

    /// <summary>Доля новой правки при УТОЧНЯЮЩЕМ обучении (XIC-32): 0.5 — кривая встаёт посередине
    /// между прежним мнением и новым, 1 — как раньше, буквально по последней правке.</summary>
    public double AutoBrightnessLearnBlend { get; set; } = 0.5;

    /// <summary>До какой разницы (%) правка считается уточняющей и сглаживается; крупнее —
    /// осознанная смена, запоминается точно. Дефолт 10 = шаг клавиш яркости Windows.</summary>
    public int AutoBrightnessFineStep { get; set; } = 10;

    /// <summary>Обучение кривой по правкам (XIC-37, тумблер на вкладке «Экран»). false —
    /// кривая заморожена и становится авторитетом: правка яркости — временное отклонение,
    /// дальше действует «возврат к выученному» (см. AutoBrightnessRevert).</summary>
    public bool AutoBrightnessLearning { get; set; } = true;

    /// <summary>Возврат к выученному при выключенном обучении (комбо на вкладке «Экран»):
    /// null — всегда (мягкое схождение к кривой, механика лимита XIC-29), "battery" — только
    /// на батарее (от сети правка живёт до смены света), "off" — не возвращать вовсе.</summary>
    public string? AutoBrightnessRevert { get; set; }

    /// <summary>Интервал шагов схождения к выученному, мс (только config.json). За шаг разрыв
    /// сокращается в BrightnessGapDivisor раз (делитель и порог доводки — общие с лимитом).</summary>
    public int AutoBrightnessRevertMs { get; set; } = 60_000;

    /// <summary>Пауза после «пользователь настоял» (повторная правка после нашего шага), минут
    /// (только config.json). Сбрасывается блокировкой, сном и сменой питания.</summary>
    public int AutoBrightnessRevertBackoffMin { get; set; } = 120;

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

    /// <summary>
    /// Удерживать частоту: возвращать заданную, если режим экрана сменил кто-то извне
    /// (параметры Windows, чужая утилита, драйвер после сброса). Работает поверх
    /// <see cref="AutoRefreshRate"/> — без него возвращать нечего. По умолчанию выключено:
    /// это реакция на чужие действия, включать её за пользователя не станем.
    /// </summary>
    public bool HoldRefreshRate { get; set; } = false;

    /// <summary>Частота экрана (Гц) от сети. Настраивается только правкой config.json.</summary>
    public int AcRefreshRate { get; set; } = 120;

    /// <summary>Частота экрана (Гц) от батареи. Настраивается только правкой config.json.</summary>
    public int BatteryRefreshRate { get; set; } = 60;

    /// <summary>Режим «Не спать» активен: сна нет; крышка на AC — «ничего не делать»;
    /// экран не гаснет, если не задан <see cref="OwlIgnoreDisplay"/>.</summary>
    public bool Awake { get; set; } = false;

    /// <summary>
    /// «Сова не трогает монитор»: с true режим держит бодрой только систему, а экран гаснет по
    /// обычному таймауту схемы питания (не выставляем `ES_DISPLAY_REQUIRED`). Для сценария
    /// «комп не должен уснуть ради удалённого доступа» — матрица там горит впустую.
    /// Дефолт false = прежнее поведение, экран удерживается: поведение существующих установок
    /// не меняется, пока поле не добавят в config.json руками. Крышку опция не затрагивает —
    /// закрытая крышка на сети всё так же не усыпляет. Медиаплееры просят `ES_DISPLAY_REQUIRED`
    /// сами, поэтому фильмы не прерываются и с true. Применяется при следующем включении совы.
    /// </summary>
    public bool OwlIgnoreDisplay { get; set; } = false;

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

    // ---- Индикатор в трее (XIC-35) ----

    /// <summary>Второй значок в трее с числом выбранной метрики (как TrafficMonitor).
    /// По умолчанию выключено; выключено = не создаётся ни значок, ни таймер, ни
    /// источники данных — ноль дополнительной нагрузки на систему.</summary>
    public bool TrayMetricEnabled { get; set; }

    /// <summary>Метрика на значке: "power" (Вт с датчика батареи), "cpu", "gpu", "ram",
    /// "temp". null/неизвестное → power. Одновременно показывается одна.</summary>
    public string? TrayMetricKind { get; set; }

    /// <summary>Период обновления значка, сек (в UI пресеты 1/2/5/10; правкой config.json —
    /// любое, приводится к 1..60).</summary>
    public int TrayMetricPeriodSec { get; set; } = 2;

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
    // (заряд 80/100), "panel" (быстрая панель), "owl", "monitor", "travel",
    // "touchpad", "touchscreen", "autobright" (авто-яркость, только при датчике освещённости),
    // "projection" (Win+P), "settings" (Параметры Windows), "copilot" (Win+C), "play"/"next"/"prev"/"stop"
    // (мультимедиа), "calc" (калькулятор), "launch" (команда из соответствующего *Command),
    // "none". null → дефолт слота (см. MigrateKeyActions).

    /// <summary>Одиночный клик Mi-кнопки (дефолт "modes").</summary>
    public string? MiClickAction { get; set; }

    /// <summary>Двойной клик Mi-кнопки (дефолт "charge"). "none" — жест отключён,
    /// одиночный клик срабатывает мгновенно (без окна ожидания ~300 мс).</summary>
    public string? MiDoubleAction { get; set; }

    /// <summary>Удержание Mi-кнопки (дефолт "panel" — прежнее зашитое поведение). "none" —
    /// жест отключён, долгое нажатие отрабатывает обычным кликом.</summary>
    public string? MiHoldAction { get; set; }

    /// <summary>Клавиша «настройки» (шестерёнка), дефолт "charge". При открытой панели — всегда заряд.</summary>
    public string? SettingsKeyAction { get; set; }

    /// <summary>AI-клавиша (дефолт "copilot").</summary>
    public string? AiKeyAction { get; set; }

    /// <summary>Клавиша «проекция» (дефолт "projection").</summary>
    public string? ProjKeyAction { get; set; }

    /// <summary>
    /// Переопределение кодов клавиш прошивки (XIC-38) — для моделей, где они отличаются от
    /// TM2424. Слоты: <c>miDown</c>, <c>miUp</c>, <c>projection</c>, <c>settings</c>, <c>ai</c>,
    /// <c>mic</c>, <c>backlight</c>, <c>fnLock</c>; значение — код в виде <c>"0x18"</c> (как в
    /// журнале: «Key: необработанное событие code=0x18») или десятичное число. Пример:
    /// <code>"KeyCodes": { "miDown": "0x18", "miUp": "0x19" }</code>
    /// Применяется при следующем запуске. Незнакомый слот и неразборчивый код игнорируются
    /// молча — кривая правка не должна отбирать рабочие клавиши. Подробности — docs/07-keymap.md.
    /// </summary>
    [JsonConverter(typeof(LenientStringMapConverter))]
    public Dictionary<string, string>? KeyCodes { get; set; }

    /// <summary>Команды для действия "launch": путь к exe/файлу/URL + аргументы
    /// (поддерживаются %ПЕРЕМЕННЫЕ%; путь с пробелами — в кавычках).</summary>
    public string? MiClickCommand { get; set; }
    public string? MiDoubleCommand { get; set; }
    public string? MiHoldCommand { get; set; }
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
    /// Мёртвая зона у нижнего края тачпада: гасит НАЧАЛО касания в полосе (защита от лежащей
    /// ладони). Жест, начатый выше и вошедший в зону, продолжает работать; нажатие в зоне
    /// тоже срабатывает — проверено вживую, см. XIC-24. По умолчанию выключено: опция пишет
    /// машинную настройку Windows (HKLM), включать её за пользователя не станем.
    /// </summary>
    public bool TouchpadDeadZone { get; set; }

    /// <summary>Высота мёртвой зоны в миллиметрах. В UI — пресеты 8/10/12/15/20; правкой
    /// config.json можно задать своё (значение приводится к 1..40 мм).</summary>
    public int TouchpadDeadZoneMm { get; set; } = 12;

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
        // до XIC-28 удержание было зашито на панель — старые конфиги ведут себя как прежде
        MiHoldAction ??= "panel";
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

    /// <summary>Валидный порог «беречь», % — клэмп руками-правленного <see cref="CareLimitPercent"/>
    /// к поддержанному пресету (неизвестное значение → дефолт 80).</summary>
    public int CarePercent() =>
        Mifs.ChargeCodeForPercent(CareLimitPercent) is null ? Mifs.ChargeThresholdPercent : CareLimitPercent;

    /// <summary>Запомнить режим для восстановления — только если опция включена и значение изменилось (бережём SSD).</summary>
    public void RememberMode(PerfMode mode)
    {
        if (!RestoreMode || StartPerfMode == mode) return;
        StartPerfMode = mode;
        Save();
    }
}
