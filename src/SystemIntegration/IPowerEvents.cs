using Microsoft.Win32;

namespace XiControl.SystemIntegration;

/// <summary>
/// События питания и текущее состояние — развязка guard-ов от статики
/// SystemEvents/SystemInformation (WinForms) ради тестов на фейках.
/// </summary>
public interface IPowerEvents : IDisposable
{
    /// <summary>Resume / Suspend / StatusChange (семантика SystemEvents.PowerModeChanged).</summary>
    event Action<PowerModes>? PowerModeChanged;

    /// <summary>Завершение сеанса (shutdown/restart/logoff) — последний шанс тронуть EC.</summary>
    event Action? SessionEnding;

    /// <summary>true — питание от сети (AC), false — батарея.</summary>
    bool IsOnline { get; }

    /// <summary>Заряд батареи 0..1; вне диапазона (напр. 2.55) — «неизвестно» (семантика WinForms).</summary>
    float BatteryLifePercent { get; }
}

/// <summary>
/// Смена режима экрана: разрешение, частота, подключение/отключение монитора, пробуждение панели
/// (семантика SystemEvents.DisplaySettingsChanged). Отдельный шов, а не поле в
/// <see cref="IPowerEvents"/>: событие не про питание и потребитель у него свой. Реализация общая —
/// <see cref="SystemEventsSource"/>, одно скрытое окно на оба источника.
/// </summary>
public interface IDisplayEvents
{
    event Action? DisplaySettingsChanged;
}

/// <summary>
/// Единая трактовка «сеть или батарея». GetSystemPowerStatus законно отдаёт Unknown (255) —
/// чаще всего сразу после resume, то есть ровно тогда, когда просыпаются guard-ы. Батареей
/// считаем ТОЛЬКО явный Offline: принять Unknown за батарею — значит зря включить троттлинг
/// (экран уедет на батарейную частоту при воткнутом зарядном и останется там до следующего
/// события питания, потому что переспрашивать мы не переспрашиваем).
/// </summary>
public static class PowerLine
{
    public static bool IsOnline(PowerLineStatus status) => status != PowerLineStatus.Offline;

    public static bool IsOnline() => IsOnline(PowerStatus.Read().LineStatus);
}

/// <summary>
/// Прод-реализация поверх статических событий WinForms — один источник и для питания, и для экрана.
/// SystemEvents доставляет события с фонового MTA-потока без насоса сообщений, а WinForms-таймер
/// (дебаунс guard-ов), стартованный оттуда, не тикает никогда (проверено вживую: OSD питания
/// показывался, а частота экрана не менялась). Поэтому Resume/StatusChange и DisplaySettingsChanged
/// маршалятся скрытым окном в поток-создатель (главный: DI собирается в Program.Main) — тот же
/// паттерн, что «все события — в UI-поток» у клавиш прошивки в TrayApp. Suspend и SessionEnding
/// идут синхронно с потока события: после них насос может не успеть, а ре-арм EC (ChargeGuard)
/// должен случиться немедленно.
///
/// Оба шва (<see cref="IPowerEvents"/>, <see cref="IDisplayEvents"/>) — на одном экземпляре:
/// окно-маршалер нужно ровно одно, а интерфейсы остаются узкими.
/// </summary>
public sealed class SystemEventsSource : IPowerEvents, IDisplayEvents
{
    public event Action<PowerModes>? PowerModeChanged;
    public event Action? SessionEnding;
    public event Action? DisplaySettingsChanged;

    private readonly MarshalWindow _window;
    private bool _disposed;   // в DI экземпляр отдаётся под двумя интерфейсами — Dispose может прийти не раз

    public bool IsOnline => PowerLine.IsOnline();

    public float BatteryLifePercent => PowerStatus.Read().BatteryLifePercent;

    public SystemEventsSource()
    {
        _window = new MarshalWindow(m => PowerModeChanged?.Invoke(m), () => DisplaySettingsChanged?.Invoke());
        SystemEvents.PowerModeChanged += OnPower;
        SystemEvents.SessionEnding += OnSession;
        SystemEvents.DisplaySettingsChanged += OnDisplay;
    }

    private void OnPower(object? s, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) PowerModeChanged?.Invoke(e.Mode); // сейчас или никогда
        else _window.PostPower(e.Mode);
    }

    private void OnSession(object? s, SessionEndingEventArgs e) => SessionEnding?.Invoke();

    private void OnDisplay(object? s, EventArgs e) => _window.PostDisplay();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPower;
        SystemEvents.SessionEnding -= OnSession;
        SystemEvents.DisplaySettingsChanged -= OnDisplay;
        _window.DestroyHandle(); // диспоуз на главном потоке (провайдер) — окну это и нужно
    }

    // Скрытое окно-маршалер: Post с любого потока → доставка в WndProc потока-создателя.
    private sealed class MarshalWindow : NativeWindow
    {
        private const int WmPower = 0x8000;    // WM_APP
        private const int WmDisplay = 0x8001;  // WM_APP + 1

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private readonly Action<PowerModes> _power;
        private readonly Action _display;
        private readonly System.Collections.Concurrent.ConcurrentQueue<PowerModes> _queue = new();

        public MarshalWindow(Action<PowerModes> power, Action display)
        {
            _power = power;
            _display = display;
            CreateHandle(new CreateParams());
        }

        public void PostPower(PowerModes mode)
        {
            _queue.Enqueue(mode);
            PostMessageW(Handle, WmPower, IntPtr.Zero, IntPtr.Zero);
        }

        // payload нет — само событие и есть сигнал «режим экрана изменился»
        public void PostDisplay() => PostMessageW(Handle, WmDisplay, IntPtr.Zero, IntPtr.Zero);

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WmPower:
                    while (_queue.TryDequeue(out var mode)) _power(mode);
                    return;
                case WmDisplay:
                    _display();
                    return;
                default:
                    base.WndProc(ref m);
                    return;
            }
        }

        public new void DestroyHandle() => base.DestroyHandle();
    }
}
