using XiControl.Config;
using XiControl.Wmi;

namespace XiControl.Input;

/// <summary>
/// Роутинг клавиш прошивки: HID-код → обработчик и настраиваемое действие → команда.
/// Исполнители — колбэки (идиома SettingsActions): сегодня их ставит TrayApp, позже —
/// командный слой (AppController), роутер при этом не меняется. Тестируется на фейках
/// без железа. Вызывать из UI-потока (маршалит подписчик IKeyEventSource).
/// </summary>
public sealed class KeyRouter
{
    private readonly AppConfig _cfg;
    private readonly MiButtonGesture _mi;
    private readonly KeyMap _keys;

    // --- исполнители настраиваемых действий (AppConfig.*Action) ---
    public Action? CycleModes;
    public Action? ToggleCharge;
    public Action? TogglePanel;
    public Action? ToggleOwl;
    public Action? ToggleMonitor;
    public Action? ToggleTravel;
    public Action? ToggleTouchpad;
    public Action? ToggleTouchscreen;
    public Action? ToggleAutoBrightness;
    public Action? CycleRefreshRate;
    public Action? Projection;
    public Action? Screenshot;
    public Action? TaskView;
    public Action? OpenSettings;
    public Action? Copilot;
    public Action? MediaPlayPause;
    public Action? MediaNext;
    public Action? MediaPrev;
    public Action? MediaStop;
    public Action? Calculator;
    public Action<string>? Launch;

    // --- клавиши-уведомления (прошивка уже всё сделала — показать OSD) ---
    public Action<byte>? MicKey;
    public Action<byte>? BacklightKey;
    public Action<byte>? ProjectionWarningKey;
    public Action<byte>? TouchpadStateKey;
    public Action<byte>? LowPowerKey;
    public Action<byte>? NumLockKey;
    public Action<byte>? RefreshRateKey;
    public Action<byte>? WinKeyLockKey;
    public Action<byte>? CameraPrivacyKey;
    public Action<byte>? FnLockKey;
    public Action<byte>? CapsLockKey;
    public Action<byte>? PerformanceKey;

    /// <summary>Открыта ли быстрая панель: клавиша «настройки» при открытой панели — всегда заряд.</summary>
    public Func<bool> PanelVisible = () => false;

    /// <summary>Есть ли датчик освещённости. У сов и тачпада гейт лежит в конфиге, а здесь —
    /// в железе: без датчика фичи нет и в настройках, значит и клавиша молчит. Дефолт false,
    /// чтобы несмонтированный роутер не дёргал то, чего на машине может не быть.</summary>
    public Func<bool> AutoBrightnessAvailable = () => false;

    /// <param name="keys">Карта «код → смысл»; null — из конфига (дефолты TM2424 +
    /// переопределения <see cref="AppConfig.KeyCodes"/> для других моделей, XIC-38).</param>
    public KeyRouter(AppConfig cfg, MiButtonGesture mi, KeyMap? keys = null)
    {
        _cfg = cfg;
        _mi = mi;
        _keys = keys ?? KeyMap.FromConfig(cfg);
    }

    /// <summary>Событие клавиши прошивки (code, value) → обработчик.</summary>
    public void Handle(byte code, byte value)
    {
        switch (_keys.Kind(code))
        {
            case KeyKind.MiDown: _mi.Down(); break;
            case KeyKind.MiUp: _mi.Up(); break;
            case KeyKind.Projection when value == 0:
                Run(_cfg.ProjKeyAction, _cfg.ProjKeyCommand); break;
            case KeyKind.Projection: ProjectionWarningKey?.Invoke(value); break;
            case KeyKind.Screenshot: Screenshot?.Invoke(); break;
            case KeyKind.TaskView: TaskView?.Invoke(); break;
            case KeyKind.Settings: OnSettingsKey(); break; // одиночное событие, удержание не ловится
            case KeyKind.Ai:
                Run(_cfg.AiKeyAction, _cfg.AiKeyCommand); break;
            case KeyKind.Mic: MicKey?.Invoke(value); break;
            case KeyKind.Backlight: BacklightKey?.Invoke(value); break;
            case KeyKind.TouchpadState: TouchpadStateKey?.Invoke(value); break;
            case KeyKind.TouchpadToggle:
                if (_cfg.TouchpadFeature) ToggleTouchpad?.Invoke();
                break;
            case KeyKind.FnLock: FnLockKey?.Invoke(value); break;
            case KeyKind.CapsLock: CapsLockKey?.Invoke(value); break;
            case KeyKind.Performance: PerformanceKey?.Invoke(value); break;
            case KeyKind.Calculator: Calculator?.Invoke(); break;
            case KeyKind.LowPower: LowPowerKey?.Invoke(value); break;
            case KeyKind.NumLock: NumLockKey?.Invoke(value); break;
            case KeyKind.RefreshRate: RefreshRateKey?.Invoke(value); break;
            case KeyKind.WinKeyLock: WinKeyLockKey?.Invoke(value); break;
            case KeyKind.CameraPrivacy: CameraPrivacyKey?.Invoke(value); break;
            case KeyKind.AiUp:
                break; // отпускание AI-клавиши: пара уже отработана на нажатии
            case KeyKind.Reserved:
                // Действия нет, но молчать нельзя. «Резерв» — это пустой слот диспетчера OEM,
                // выясненный статическим разбором ЧУЖОЙ прошивки (docs/14). На модели, которой
                // мы не видели, тот же код вполне может быть живой клавишей, и человек ищет её
                // ровно этой строкой в log.txt — так описано в docs/07-keymap.md и так мы прямо
                // сейчас разбираем отчёты в issue #37. Проглотить её — отобрать единственный
                // способ починиться самому через KeyCodes в config.json.
                Log.Write($"Key: резервный код диспетчера code=0x{code:X2} value=0x{value:X2} — действия нет");
                break;
            default:
                // другие модели шлют другие коды/value — лог помогает разбирать отчёты тестеров,
                // а сам человек может прописать свой код в KeyCodes (config.json)
                Log.Write($"Key: необработанное событие code=0x{code:X2} value=0x{value:X2}");
                break;
        }
    }

    /// <summary>
    /// Выполнить настраиваемое действие клавиши (AppConfig.*Action / *Command).
    /// Неизвестное значение и "none" — молча ничего (совместимость с будущими конфигами).
    /// </summary>
    public void Run(string? action, string? command)
    {
        switch (action)
        {
            case "modes": CycleModes?.Invoke(); break;
            case "charge": ToggleCharge?.Invoke(); break;
            case "panel": TogglePanel?.Invoke(); break;
            case "owl": if (_cfg.OwlMode) ToggleOwl?.Invoke(); break; // фича скрыта — клавиша не включает
            case "monitor": ToggleMonitor?.Invoke(); break;
            case "travel": ToggleTravel?.Invoke(); break;  // без ChargeCare внутри не включится
            case "touchpad": if (_cfg.TouchpadFeature) ToggleTouchpad?.Invoke(); break; // фича скрыта — не трогаем
            case "touchscreen": if (_cfg.TouchscreenFeature) ToggleTouchscreen?.Invoke(); break; // фича скрыта — не трогаем
            case "autobright": if (AutoBrightnessAvailable()) ToggleAutoBrightness?.Invoke(); break; // нет датчика — нечего включать
            case "hz": if (_cfg.RefreshRateFeature) CycleRefreshRate?.Invoke(); break; // фича убрана — экран не трогаем
            case "projection": Projection?.Invoke(); break;
            case "settings": OpenSettings?.Invoke(); break;
            case "copilot": Copilot?.Invoke(); break;
            case "play": MediaPlayPause?.Invoke(); break;
            case "next": MediaNext?.Invoke(); break;
            case "prev": MediaPrev?.Invoke(); break;
            case "stop": MediaStop?.Invoke(); break;
            case "calc": Calculator?.Invoke(); break;
            case "launch":
                if (!string.IsNullOrWhiteSpace(command)) Launch?.Invoke(command);
                break;
        }
    }

    // Клавиша «Настройки»: настраиваемое действие; при открытой панели — всегда заряд
    // (переключается пилюля в ней), независимо от ремапа.
    private void OnSettingsKey()
    {
        if (PanelVisible()) ToggleCharge?.Invoke();
        else Run(_cfg.SettingsKeyAction, _cfg.SettingsKeyCommand);
    }
}
