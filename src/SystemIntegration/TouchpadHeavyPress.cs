using XiControl.Config;

namespace XiControl.SystemIntegration;

/// <summary>
/// Сильное нажатие на тачпад как назначаемое действие (XIC-78): продавили панель сильнее
/// обычного клика — выполняется выбранное действие, тот же список, что у клавиш. Как в
/// Xiaomi PC Manager, где это скриншот, только действие своё.
///
/// Две половины, обе driver-free:
/// <list type="number">
/// <item>распознавание — давление из обычного ввода тачпада (<see cref="HeavyPressDetector"/>),
///   в тачпад для этого не пишется ни байта;</item>
/// <item>ощущение — второй щелчок прошивки в момент продавливания (<c>0x59</c>): без него
///   человек не чувствует, что «дожал». Пишется ТОЛЬКО по явному переключению пользователем,
///   на старте тачпад не трогаем (живёт в самом тачпаде, как и прочие настройки).</item>
/// </list>
/// Обычный клик при сильном нажатии никуда не девается — продавить можно только нажав. Кроме
/// кликов мышью (ПКМ/СКМ/двойной, XIC-108): там левый клик придерживает <see cref="HeavyPressMouse"/>
/// и заменяет назначенным.
/// </summary>
public sealed class TouchpadHeavyPress : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly TouchpadInput _input;
    private readonly TouchpadHaptics? _haptics;
    private readonly HeavyPressDetector _detector = new();
    private readonly HeavyPressMouse _mouse;
    private readonly object _lock = new();
    private bool _attached;

    /// <summary>Сильное нажатие случилось (поток читателя касаний!).</summary>
    public event Action? Pressed;

    public TouchpadHeavyPress(AppConfig cfg, TouchpadInput input, TouchpadHaptics? haptics = null)
    {
        _cfg = cfg;
        _input = input;
        _haptics = haptics;
        _mouse = new HeavyPressMouse(() => _cfg.TouchpadHeavyPressAction);
    }

    /// <summary>Назначено ли действие — «нет действия» и есть выключенная фича.</summary>
    public bool Enabled => !string.IsNullOrEmpty(_cfg.TouchpadHeavyPressAction) && _cfg.TouchpadHeavyPressAction != "none";

    /// <summary>Назначен клик мышью — вместо левого клика, а не вдобавок к нему (XIC-108).</summary>
    private bool MouseAction => HeavyPressMouse.IsMouseAction(_cfg.TouchpadHeavyPressAction);

    /// <summary>Старт приложения: подписаться на касания, если фича включена. В тачпад не пишем.</summary>
    public void Start()
    {
        if (Enabled) Attach();
        SyncMouse();
    }

    /// <summary>
    /// Хук мыши — только под клик мышью: для прочих действий левое нажатие не придерживаем
    /// вовсе. Зовётся и при смене одного действия на другое, когда подписка уже есть.
    /// </summary>
    public void SyncMouse()
    {
        if (Enabled && MouseAction) _mouse.Start(); else _mouse.Stop();
    }

    /// <summary>
    /// Явное включение/выключение пользователем: второй щелчок прошивки — вслед за фичей
    /// (выключили — возвращаем заводское «не щёлкать»), подписка — тоже. Зовётся с фона:
    /// запись в тачпад занимает ~0,5 с.
    /// </summary>
    public void Apply()
    {
        bool on = Enabled;
        if (_haptics is not null && !_haptics.SetHeavyPress(on))
            Log.Write($"TouchpadHeavyPress: второй щелчок прошивки не {(on ? "включился" : "выключился")}");
        if (on) Attach(); else Detach();
        SyncMouse();
    }

    private void OnFrame(IReadOnlyList<TouchContact> contacts)
    {
        _mouse.Contacts(contacts.Count);
        bool fired;
        lock (_lock) fired = _detector.Update(contacts);
        if (!fired) return;
        if (!MouseAction) Pressed?.Invoke();
        // продавили позже окна ожидания — левый клик уже ушёл, клик сверху был бы хуже
        else if (!_mouse.Heavy()) Log.Write("TouchpadHeavyPress: сильное нажатие позже окна — левый клик уже отдан");
    }

    // Подписка и Acquire/Release — СНАРУЖИ замка: Release останавливает читатель и ждёт его
    // поток, а тот может как раз стоять в OnFrame на этом же замке — вышел бы тупик до
    // таймаута, после которого читатель уже не поднимается. Замок сторожит только флаг.
    private void Attach()
    {
        lock (_lock)
        {
            if (_attached) return;
            _attached = true;
        }
        _input.Frame += OnFrame;
        _input.Acquire(pressure: true);
    }

    private void Detach()
    {
        lock (_lock)
        {
            if (!_attached) return;
            _attached = false;
        }
        _input.Frame -= OnFrame;
        _input.Release(pressure: true);
    }

    public void Dispose()
    {
        _mouse.Dispose();
        Detach();
    }
}
