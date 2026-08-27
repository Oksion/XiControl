namespace XiControl.Ui;

/// <summary>
/// Explorer can emit both version-4 and legacy notifications for one physical tray click while
/// its overflow surface changes state. Collapse that pair without delaying the real click.
/// </summary>
internal sealed class TrayCallbackGate
{
    internal const long DuplicateWindowMs = 250;

    private readonly object _sync = new();
    private long _lastAcceptedAt = long.MinValue;

    internal bool TryEnter() => TryEnter(Environment.TickCount64);

    internal bool TryEnter(long timestamp)
    {
        lock (_sync)
        {
            if (_lastAcceptedAt != long.MinValue)
            {
                long elapsed = timestamp - _lastAcceptedAt;
                if (elapsed >= 0 && elapsed < DuplicateWindowMs) return false;
            }
            _lastAcceptedAt = timestamp;
            return true;
        }
    }
}
