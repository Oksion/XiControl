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

    // --- исполнители настраиваемых действий (AppConfig.*Action) ---
    public Action? CycleModes;
    public Action? ToggleCharge;
    public Action? TogglePanel;
    public Action? ToggleOwl;
    public Action? ToggleMonitor;
    public Action? ToggleTravel;
    public Action? ToggleTouchpad;
    public Action? ToggleTouchscreen;
    public Action? Projection;
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
    public Action<byte>? FnLockKey;

    /// <summary>Открыта ли быстрая панель: клавиша «настройки» при открытой панели — всегда заряд.</summary>
    public Func<bool> PanelVisible = () => false;

    public KeyRouter(AppConfig cfg, MiButtonGesture mi)
    {
        _cfg = cfg;
        _mi = mi;
    }

    /// <summary>Событие клавиши прошивки (code, value) → обработчик.</summary>
    public void Handle(byte code, byte value)
    {
        switch (code)
        {
            case Mifs.KeyMiDown: _mi.Down(); break;
            case Mifs.KeyMiUp: _mi.Up(); break;
            case Mifs.KeyProjection when value == 0:                                 // value 2 = слабый зарядник — пока пропуск
                Run(_cfg.ProjKeyAction, _cfg.ProjKeyCommand); break;
            case Mifs.KeySettings: OnSettingsKey(); break; // одиночное событие, удержание не ловится
            case Mifs.KeyAiDown:                                                     // 0x24 (отпускание) игнорируем
                Run(_cfg.AiKeyAction, _cfg.AiKeyCommand); break;
            case Mifs.KeyMic: MicKey?.Invoke(value); break;
            case Mifs.KeyKbdBacklight: BacklightKey?.Invoke(value); break;
            case Mifs.KeyFnLock: FnLockKey?.Invoke(value); break;
            default:
                // другие модели шлют другие коды/value — лог помогает разбирать отчёты тестеров
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
