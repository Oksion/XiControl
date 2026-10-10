namespace XiControl.SystemIntegration;

/// <summary>
/// Слот плавного хода яркости: новый ход отменяет предыдущий. Источник отмены освобождается
/// не в момент отмены, а когда задача хода РЕАЛЬНО завершилась. Ход живёт на пуле и между
/// проверками токена делает медленную WMI-запись: «Cancel + сразу Dispose» из guard-а успевал
/// проскочить в этот промежуток, и следующий же <c>ct.WaitHandle</c> внутри хода падал
/// ObjectDisposedException («Brightness.Ramp: … has been disposed» в логе, чаще на выходе
/// из сна, когда правки и пересчёты идут пачкой).
/// </summary>
internal sealed class RampSlot
{
    // свой замок, а не замок guard-а: освобождение идёт продолжением на пуле, и оно должно
    // забрать источник из слота атомарно с Dispose — иначе отмена после конца хода звала бы
    // Cancel у уже освобождённого источника
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;

    public void Start(Func<CancellationToken, Task> ramp)
    {
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            CancelLocked();
            _cts = cts;
        }
        Task task;
        try { task = ramp(cts.Token); }
        catch { Release(cts); throw; } // ход не стартовал — токен никто не держит
        // без ExecuteSynchronously: на уже завершённой задаче продолжение выполнилось бы прямо
        // здесь, в потоке guard-а под его замком, — незачем
        task.ContinueWith(_ => Release(cts),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>Отменить текущий ход. Источник освободит продолжение задачи хода.</summary>
    public void Cancel()
    {
        lock (_gate) CancelLocked();
    }

    private void CancelLocked()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private void Release(CancellationTokenSource cts)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_cts, cts)) _cts = null; // ход кончился сам — отменять больше нечего
            cts.Dispose();
        }
    }
}
