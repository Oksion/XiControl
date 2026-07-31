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
/// Единая трактовка «сеть или батарея». GetSystemPowerStatus законно отдаёт Unknown (255) —
/// чаще всего сразу после resume, то есть ровно тогда, когда просыпаются guard-ы. Батареей
/// считаем ТОЛЬКО явный Offline: принять Unknown за батарею — значит зря включить троттлинг
/// (экран уедет на батарейную частоту при воткнутом зарядном и останется там до следующего
/// события питания, потому что переспрашивать мы не переспрашиваем).
/// </summary>
public static class PowerLine
{
    public static bool IsOnline(PowerLineStatus status) => status != PowerLineStatus.Offline;

    public static bool IsOnline() => IsOnline(SystemInformation.PowerStatus.PowerLineStatus);
}

/// <summary>
/// Прод-реализация поверх статических событий WinForms.
/// SystemEvents доставляет события с фонового MTA-потока без насоса сообщений — WinForms-таймер
/// (дебаунс guard-ов), стартованный оттуда, не тикает никогда (проверено вживую: OSD питания
/// показывался, а частота экрана не менялась). Поэтому Resume/StatusChange маршалятся скрытым
/// окном в поток-создатель (главный: DI собирается в Program.Main) — тот же паттерн, что
/// «все события — в UI-поток» у клавиш прошивки в TrayApp. Suspend и SessionEnding идут
/// синхронно с потока события: после них насос может не успеть, а ре-арм EC (ChargeGuard)
/// должен случиться немедленно.
/// </summary>
public sealed class SystemPowerEvents : IPowerEvents
{
    public event Action<PowerModes>? PowerModeChanged;
    public event Action? SessionEnding;

    private readonly MarshalWindow _window;

    public bool IsOnline => PowerLine.IsOnline();

    public float BatteryLifePercent => SystemInformation.PowerStatus.BatteryLifePercent;

    public SystemPowerEvents()
    {
        _window = new MarshalWindow(m => PowerModeChanged?.Invoke(m));
        SystemEvents.PowerModeChanged += OnPower;
        SystemEvents.SessionEnding += OnSession;
    }

    private void OnPower(object? s, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) PowerModeChanged?.Invoke(e.Mode); // сейчас или никогда
        else _window.Post(e.Mode);
    }

    private void OnSession(object? s, SessionEndingEventArgs e) => SessionEnding?.Invoke();

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPower;
        SystemEvents.SessionEnding -= OnSession;
        _window.DestroyHandle(); // диспоуз на главном потоке (провайдер) — окну это и нужно
    }

    // Скрытое окно-маршалер: Post с любого потока → доставка в WndProc потока-создателя.
    private sealed class MarshalWindow : NativeWindow
    {
        private const int WmDeliver = 0x8000; // WM_APP

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private readonly Action<PowerModes> _deliver;
        private readonly System.Collections.Concurrent.ConcurrentQueue<PowerModes> _queue = new();

        public MarshalWindow(Action<PowerModes> deliver)
        {
            _deliver = deliver;
            CreateHandle(new CreateParams());
        }

        public void Post(PowerModes mode)
        {
            _queue.Enqueue(mode);
            PostMessageW(Handle, WmDeliver, IntPtr.Zero, IntPtr.Zero);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmDeliver)
            {
                while (_queue.TryDequeue(out var mode)) _deliver(mode);
                return;
            }
            base.WndProc(ref m);
        }

        public new void DestroyHandle() => base.DestroyHandle();
    }
}
