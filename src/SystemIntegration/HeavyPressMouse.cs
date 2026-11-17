using System.Runtime.InteropServices;

namespace XiControl.SystemIntegration;

/// <summary>
/// Хост <see cref="HeavyClickGate"/> (XIC-108): глобальный хук мыши на своём потоке. Живёт,
/// только пока на сильное нажатие назначен клик мышью — для прочих действий хука нет вовсе.
///
/// Поток свой, а не UI: низкоуровневый хук задерживает ВЕСЬ ввод мыши на время колбэка, и
/// зависни UI-поток — Windows молча сняла бы хук (как с панелью, CLAUDE.md). Колбэк короткий:
/// решение шлюза под замком и, если нужно, SendInput — он только ставит события в очередь.
/// Наш синтез помечен <see cref="Marker"/> в dwExtraInfo: так хук узнаёт отданное нажатие.
/// </summary>
public sealed class HeavyPressMouse : IDisposable
{
    /// <summary>Действия-клики: в списке слота сильного нажатия, у клавиш их нет.</summary>
    public static readonly string[] Actions = ["rclick", "mclick", "dblclick"];

    public static bool IsMouseAction(string? action) => Array.IndexOf(Actions, action) >= 0;

    private static readonly IntPtr Marker = new(0x58694347); // «XiCG»

    private readonly HeavyClickGate _gate = new();
    private readonly Func<string?> _action;
    private readonly object _life = new();
    private volatile int _contacts;
    private Thread? _thread;
    private uint _threadId;
    private HookProc? _proc; // делегат в поле — иначе GC соберёт его и колбэк упадёт

    /// <param name="action">Назначенное действие — читается в момент отпускания.</param>
    public HeavyPressMouse(Func<string?> action) => _action = action;

    /// <summary>Пальцев на тачпаде сейчас (поток касаний). Ноль — левое нажатие не наше: тап или мышь.</summary>
    public void Contacts(int count) => _contacts = count;

    /// <summary>Сильное нажатие (поток касаний). false — левый клик уже ушёл, заменять нечего.</summary>
    public bool Heavy() => _gate.Heavy();

    public void Start()
    {
        lock (_life)
        {
            if (_thread is not null) return;
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "XiControl.HeavyPressMouse" };
            _thread.Start();
            ready.Wait(2000);
        }
    }

    public void Stop()
    {
        lock (_life)
        {
            if (_thread is null) return;
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            if (!_thread.Join(2000)) Log.Write("HeavyPressMouse: поток хука не завершился вовремя");
            _thread = null;
        }
    }

    private void Run(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        _ = PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE); // очередь потока — до сигнала «готов»
        _proc = OnHook;
        IntPtr hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        ready.Set();
        if (hook == IntPtr.Zero)
        {
            Log.Write($"HeavyPressMouse: SetWindowsHookEx(WH_MOUSE_LL) не удалась, ошибка {Marshal.GetLastPInvokeError()}");
            return;
        }
        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message != WM_TIMER) continue;
                _ = KillTimer(IntPtr.Zero, msg.wParam);
                Do(_gate.Tick(Environment.TickCount64));
            }
        }
        finally { UnhookWindowsHookEx(hook); }
    }

    private IntPtr OnHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        try
        {
            var h = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            long msg = (long)wParam; // (int)IntPtr на x64 обрезал бы старшие биты (CA2020)
            if ((h.flags & LLMHF_INJECTED) != 0)
            {
                // чужой синтез не трогаем; наше нажатие — знак, что отданное дошло до очереди
                if (msg == WM_LBUTTONDOWN && h.dwExtraInfo == Marker) _gate.InjectedDownSeen();
            }
            else if (msg == WM_LBUTTONDOWN)
            {
                if (_gate.LeftDown(_contacts > 0, Environment.TickCount64, h.ptX, h.ptY))
                {
                    // +10 мс: WM_TIMER не приходит раньше срока, но и ровно в срок — не всегда
                    _ = SetTimer(IntPtr.Zero, UIntPtr.Zero, HeavyClickGate.DefaultWindowMs + 10, IntPtr.Zero);
                    return 1;
                }
            }
            else if (msg == WM_LBUTTONUP)
            {
                var (swallow, effect) = _gate.LeftUp();
                Do(effect);
                if (swallow) return 1;
            }
            else if (msg == WM_MOUSEMOVE) Do(_gate.Move(h.ptX, h.ptY));
        }
        catch (Exception ex) { Log.Ex("HeavyPressMouse.Hook", ex); }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private void Do(HeavyClickGate.Effect effect)
    {
        switch (effect)
        {
            case HeavyClickGate.Effect.InjectLeftDown: Send(LEFTDOWN); break;
            case HeavyClickGate.Effect.InjectLeftUp: Send(LEFTUP); break;
            case HeavyClickGate.Effect.InjectLeftClick: Send(LEFTDOWN, LEFTUP); break;
            case HeavyClickGate.Effect.RunAction:
                switch (_action())
                {
                    case "rclick": Send(RIGHTDOWN, RIGHTUP); break;
                    case "mclick": Send(MIDDLEDOWN, MIDDLEUP); break;
                    case "dblclick": Send(LEFTDOWN, LEFTUP, LEFTDOWN, LEFTUP); break;
                }
                break;
        }
    }

    // Кнопки без координат: событие встаёт туда, где сейчас указатель
    private static void Send(params uint[] flags)
    {
        var inputs = flags.Select(f => new INPUT { Type = 0, Mi = new MOUSEINPUT { Flags = f, ExtraInfo = Marker } }).ToArray();
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            Log.Write($"HeavyPressMouse: SendInput не прошёл, ошибка {Marshal.GetLastPInvokeError()}");
    }

    public void Dispose() => Stop();

    private const int WH_MOUSE_LL = 14;
    private const uint WM_QUIT = 0x0012, WM_TIMER = 0x0113, PM_NOREMOVE = 0;
    private const long WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const uint LLMHF_INJECTED = 0x01;
    private const uint LEFTDOWN = 0x02, LEFTUP = 0x04, RIGHTDOWN = 0x08, RIGHTUP = 0x10,
                       MIDDLEDOWN = 0x20, MIDDLEUP = 0x40;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public int ptX, ptY; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public int ptX, ptY; }

    // INPUT с мышиной частью — самый большой член union, его размер и задаёт sizeof(INPUT)
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint Type; public MOUSEINPUT Mi; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int Dx, Dy; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hWnd, UIntPtr id);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, [In] INPUT[] inputs, int size);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
