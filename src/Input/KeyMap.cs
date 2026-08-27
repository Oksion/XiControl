using System.Globalization;
using XiControl.Config;
using XiControl.Wmi;

namespace XiControl.Input;

/// <summary>Смысл клавиши прошивки — то, что роутер умеет обработать.</summary>
public enum KeyKind
{
    Unknown,
    MiDown, MiUp,
    Projection,
    Screenshot,
    TaskView,
    Settings,
    Ai,
    AiUp,
    Mic,
    Backlight,
    TouchpadState,
    TouchpadToggle,
    FnLock,
    CapsLock,
    Performance,
    Calculator,
    LowPower,
    NumLock,
    RefreshRate,
    WinKeyLock,
    CameraPrivacy,
    Reserved,
}

/// <summary>
/// Код клавиши HID_EVENT20 → смысл (XIC-38). Коды зависят от МОДЕЛИ: расшифрованы они на
/// TM2424, а, например, на TM2113 (Redmi Book 2022 Ryzen) Mi-кнопка шлёт другую пару — при
/// этом сам событийный канал работает. Раньше коды были зашиты в switch роутера, и владелец
/// другой модели не мог ничего сделать, кроме как ждать сборку от нас.
///
/// Теперь поверх дефолтов ложится <see cref="AppConfig.KeyCodes"/> из config.json: человек
/// смотрит свой код в журнале (строка «Key: необработанное событие code=0x18») или в отчёте
/// tools/diagnostics и прописывает его сам. Подтверждённые карты чужих моделей потом
/// переезжают в дефолты — но фича работает у человека сразу, не дожидаясь релиза.
///
/// Чистая логика без железа — целиком под тестами.
/// </summary>
public sealed class KeyMap
{
    /// <summary>Имена слотов в config.json (регистр не важен) → смысл.</summary>
    private static readonly Dictionary<string, KeyKind> SlotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["miDown"] = KeyKind.MiDown,
        ["miUp"] = KeyKind.MiUp,
        ["projection"] = KeyKind.Projection,
        ["screenshot"] = KeyKind.Screenshot,
        ["taskView"] = KeyKind.TaskView,
        ["settings"] = KeyKind.Settings,
        ["ai"] = KeyKind.Ai,
        ["aiUp"] = KeyKind.AiUp,
        ["mic"] = KeyKind.Mic,
        ["backlight"] = KeyKind.Backlight,
        ["touchpadState"] = KeyKind.TouchpadState,
        ["touchpadToggle"] = KeyKind.TouchpadToggle,
        ["fnLock"] = KeyKind.FnLock,
        ["capsLock"] = KeyKind.CapsLock,
        ["performance"] = KeyKind.Performance,
        ["calculator"] = KeyKind.Calculator,
        ["lowPower"] = KeyKind.LowPower,
        ["numLock"] = KeyKind.NumLock,
        ["refreshRate"] = KeyKind.RefreshRate,
        ["winKeyLock"] = KeyKind.WinKeyLock,
        ["cameraPrivacy"] = KeyKind.CameraPrivacy,
    };

    private readonly Dictionary<byte, KeyKind> _byCode;

    private KeyMap(Dictionary<byte, KeyKind> byCode) => _byCode = byCode;

    /// <summary>
    /// Заводская карта: TM2424 (см. docs/07-keymap.md) + подтверждённые коды других моделей.
    /// Один и тот же смысл может иметь несколько кодов — модели не пересекаются по значениям,
    /// поэтому лишние записи безвредны, а владельцу подтверждённой модели не нужен config.
    /// </summary>
    public static KeyMap Default() => new(new Dictionary<byte, KeyKind>
    {
        [Mifs.KeyMiDown] = KeyKind.MiDown,
        [Mifs.KeyMiUp] = KeyKind.MiUp,
        // TM2113 (Redmi Book Pro 15 2022, Ryzen): Mi-кнопка шлёт свою пару — подтверждено
        // отчётом пользователя, GitHub issue #37. Клавиша «настройки» там 0x1B, как у нас.
        [0x18] = KeyKind.MiDown,
        [0x19] = KeyKind.MiUp,
        [Mifs.KeyProjection] = KeyKind.Projection,
        [Mifs.KeyScreenshot] = KeyKind.Screenshot,
        [Mifs.KeyTaskView] = KeyKind.TaskView,
        [Mifs.KeyOemDevice] = KeyKind.Reserved,
        [Mifs.KeySettings] = KeyKind.Settings,
        [Mifs.KeyAiDown] = KeyKind.Ai,
        [Mifs.KeyAiUp] = KeyKind.AiUp,
        [Mifs.KeyMic] = KeyKind.Mic,
        [Mifs.KeyKbdBacklight] = KeyKind.Backlight,
        [Mifs.KeyTouchpadState] = KeyKind.TouchpadState,
        [Mifs.KeyTouchpadToggle] = KeyKind.TouchpadToggle,
        [Mifs.KeyFnLock] = KeyKind.FnLock,
        [Mifs.KeyCapsLock] = KeyKind.CapsLock,
        [Mifs.KeyPerformance] = KeyKind.Performance,
        [Mifs.KeyCalculator] = KeyKind.Calculator,
        [Mifs.KeyLowPower] = KeyKind.LowPower,
        [Mifs.KeyNumLock] = KeyKind.NumLock,
        [Mifs.KeyGameCenter] = KeyKind.Reserved,
        [Mifs.KeyOemToggle] = KeyKind.Reserved,
        [Mifs.KeySupportAssistant] = KeyKind.Reserved,
        [Mifs.KeyRefreshRateCompat] = KeyKind.RefreshRate,
        [Mifs.KeyWinLock] = KeyKind.WinKeyLock,
        [Mifs.KeyRefreshRate] = KeyKind.RefreshRate,
        [Mifs.KeyCameraPrivacy] = KeyKind.CameraPrivacy,
        [0x08] = KeyKind.Reserved,
        [0x0F] = KeyKind.Reserved,
        [0x11] = KeyKind.Reserved,
        [0x14] = KeyKind.Reserved,
        [0x15] = KeyKind.Reserved,
        [0x1C] = KeyKind.Reserved,
        [0x1D] = KeyKind.Reserved,
        [0x1E] = KeyKind.Reserved,
        [0x1F] = KeyKind.Reserved,
        [0x20] = KeyKind.Reserved,
        [0x22] = KeyKind.Reserved,
    });

    /// <summary>
    /// Дефолты + переопределения из конфига. Переопределённый слот ЗАБИРАЕТ код себе: старый
    /// код этого слота перестаёт что-либо значить (иначе на модели, где 0x25 — совсем другая
    /// клавиша, Mi-жесты срабатывали бы от неё). Мусор в конфиге игнорируется молча — кривая
    /// строка не должна отбирать у человека рабочие клавиши.
    /// </summary>
    public static KeyMap FromConfig(AppConfig cfg)
    {
        var map = Default()._byCode;
        if (cfg.KeyCodes is not { Count: > 0 } overrides) return new KeyMap(map);

        foreach (var (slot, raw) in overrides)
        {
            if (!SlotNames.TryGetValue(slot.Trim(), out var kind)) continue; // неизвестный слот
            if (ParseCode(raw) is not byte code) continue;                   // не разобрали код
            foreach (var old in map.Where(p => p.Value == kind).Select(p => p.Key).ToArray())
                map.Remove(old);
            map[code] = kind; // код мог принадлежать другому слоту — новый хозяин важнее
        }
        return new KeyMap(map);
    }

    /// <summary>Смысл кода; Unknown — код не наш (роутер запишет его в журнал).</summary>
    public KeyKind Kind(byte code) => _byCode.TryGetValue(code, out var k) ? k : KeyKind.Unknown;

    /// <summary>
    /// Код из конфига: «0x18» (как печатает журнал и диагностика) либо десятичное «24».
    /// Всё прочее — null (молча игнорируем).
    /// </summary>
    public static byte? ParseCode(string? raw)
    {
        string s = raw?.Trim() ?? "";
        if (s.Length == 0) return null;
        bool hex = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (hex) s = s[2..];
        return byte.TryParse(s, hex ? NumberStyles.HexNumber : NumberStyles.Integer,
            CultureInfo.InvariantCulture, out byte code) ? code : null;
    }
}
