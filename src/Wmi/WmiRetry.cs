namespace XiControl.Wmi;

/// <summary>
/// Когда переподключать WMI-подписку на события (XIC-83). Обрыв уже работавшего потока
/// (рестарт winmgmt) — бессрочно раз в 30 с. Первый старт — ограниченно, с нарастающей паузой:
/// сразу после входа в сеанс WMI бывает не готов — первый повтор через 2 с, а не через 30.
/// Если класса событий на машине нет — через ~минуту сдаёмся, а не пишем в лог раз в 30 с вечно.
/// </summary>
internal static class WmiRetry
{
    internal static readonly TimeSpan Reconnect = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan[] FirstStart =
    [
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30),
    ];

    /// <summary>Сколько всего попыток первого старта, считая исходную.</summary>
    internal static int FirstStartAttempts => FirstStart.Length + 1;

    /// <summary>
    /// Пауза перед следующей попыткой или null — больше не пробуем.
    /// <paramref name="firstFailures"/> — сколько раз подряд не удался первый старт (≥ 1).
    /// </summary>
    internal static TimeSpan? NextDelay(bool everStarted, int firstFailures) =>
        everStarted ? Reconnect
        : firstFailures >= 1 && firstFailures <= FirstStart.Length ? FirstStart[firstFailures - 1]
        : null;
}
