using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;

namespace XiControl.SystemIntegration;

public interface IPowerEvents : IDisposable
{
    event Action<PowerModes>? PowerModeChanged;
    event Action? SessionEnding;
    bool IsOnline { get; }
    float BatteryLifePercent { get; }
}

public interface IDisplayEvents
{
    event Action? DisplaySettingsChanged;
}

/// <summary>Значения совпадают с Win32 SYSTEM_POWER_STATUS.ACLineStatus.</summary>
public enum PowerLineStatus : byte
{
    Offline = 0,
    Online = 1,
    Unknown = 255,
}

/// <summary>Снимок состояния питания из Win32 GetSystemPowerStatus.</summary>
public readonly record struct PowerSnapshot(PowerLineStatus LineStatus, float BatteryLifePercent)
{
    public int? BatteryPercent => BatteryLifePercent is >= 0f and <= 1f
        ? (int)Math.Round(BatteryLifePercent * 100)
        : null;
}

public static class PowerLine
{
    public static bool IsOnline(PowerLineStatus status) => status != PowerLineStatus.Offline;
    public static bool IsOnline() => IsOnline(PowerStatus.Read().LineStatus);
}

public static class PowerStatus
{
    public static PowerSnapshot Read()
    {
        if (!GetSystemPowerStatus(out var status))
            return new PowerSnapshot(PowerLineStatus.Unknown, 2.55f);

        float battery = status.BatteryLifePercent == byte.MaxValue
            ? 2.55f
            : status.BatteryLifePercent / 100f;
        return new PowerSnapshot((PowerLineStatus)status.AcLineStatus, battery);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);
}

/// <summary>
/// Единый адаптер SystemEvents. События, кроме suspend, отправляются в WinUI DispatcherQueue,
/// чтобы guard-ы и представления с UI-таймерами получали их в главном потоке; suspend/session ending синхронны.
/// </summary>
public sealed class SystemEventsSource : IPowerEvents, IDisplayEvents
{
    private readonly DispatcherQueue _queue;
    private bool _disposed;

    public SystemEventsSource()
    {
        _queue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("SystemEventsSource должен создаваться в WinUI-потоке.");
        SystemEvents.PowerModeChanged += OnPower;
        SystemEvents.SessionEnding += OnSessionEnding;
        SystemEvents.DisplaySettingsChanged += OnDisplay;
    }

    public event Action<PowerModes>? PowerModeChanged;
    public event Action? SessionEnding;
    public event Action? DisplaySettingsChanged;

    public bool IsOnline => PowerLine.IsOnline();
    public float BatteryLifePercent => PowerStatus.Read().BatteryLifePercent;

    private void OnPower(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) PowerModeChanged?.Invoke(e.Mode);
        else _queue.TryEnqueue(() => PowerModeChanged?.Invoke(e.Mode));
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) => SessionEnding?.Invoke();
    private void OnDisplay(object? sender, EventArgs e) => _queue.TryEnqueue(() => DisplaySettingsChanged?.Invoke());

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPower;
        SystemEvents.SessionEnding -= OnSessionEnding;
        SystemEvents.DisplaySettingsChanged -= OnDisplay;
    }
}
