using XiControl.Config;

namespace XiControl.SystemIntegration;

/// <summary>
/// Обучаемая кривая lux → яркость (%): чистая логика авто-яркости (XIC-30), без датчика,
/// таймеров и WMI — целиком под юнит-тестами. Механика по образцу wluma (идеи, не код):
/// кусочно-линейная интерполяция по якорным точкам в лог-шкале люксов (восприятие света
/// логарифмично), обучение — ручная правка становится точкой, а точки, ломающие
/// монотонность относительно неё, вытесняются (умное забывание: кривая всегда монотонна).
/// Нейросети нет намеренно: «почему экран потемнел?» должно иметь ответ.
/// </summary>
public sealed class BrightnessCurve(List<BrightnessPoint> points)
{
    // живём прямо на списке из AppConfig: обучение сразу видно сохранению
    private readonly List<BrightnessPoint> _points = points;

    /// <summary>Дефолтные якоря — разумная лог-кривая «из коробки»: тёмная комната ≈10%,
    /// офис ≈60%, за окном ≈100%. Сеются в конфиг при первом включении фичи.</summary>
    public static List<BrightnessPoint> DefaultPoints() =>
    [
        new() { Lux = 0, Percent = 10 },
        new() { Lux = 10, Percent = 25 },
        new() { Lux = 50, Percent = 40 },
        new() { Lux = 200, Percent = 60 },
        new() { Lux = 700, Percent = 80 },
        new() { Lux = 2000, Percent = 100 },
    ];

    /// <summary>Яркость для освещённости: кусочно-линейно между соседними якорями в
    /// лог-шкале; за краями — крайние значения. Пустая кривая — 50% (не бывает в проде:
    /// включение фичи сеет дефолт).</summary>
    public int Predict(float lux)
    {
        if (_points.Count == 0) return 50;
        var pts = Sorted();
        if (lux <= pts[0].Lux) return pts[0].Percent;
        var last = pts[^1];
        if (lux >= last.Lux) return last.Percent;

        for (int i = 1; i < pts.Count; i++)
        {
            if (lux > pts[i].Lux) continue;
            var (a, b) = (pts[i - 1], pts[i]);
            double t = (LogScale(lux) - LogScale(a.Lux)) / (LogScale(b.Lux) - LogScale(a.Lux));
            return (int)Math.Round(a.Percent + t * (b.Percent - a.Percent));
        }
        return last.Percent; // недостижимо (края отсечены выше) — успокаиваем компилятор
    }

    /// <summary>
    /// Выучить правку пользователя: «при таком свете мне нужно столько». Существующие точки,
    /// ломающие монотонность относительно новой (при том же/меньшем свете просили ярче, при
    /// том же/большем — темнее), вытесняются: свежее слово пользователя весомее старых.
    /// </summary>
    public void Learn(float lux, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        _points.RemoveAll(p =>
            (p.Lux <= lux && p.Percent >= percent) ||
            (p.Lux >= lux && p.Percent <= percent));
        _points.Add(new BrightnessPoint { Lux = lux, Percent = percent });
    }

    /// <summary>Точек в кривой (для тестов и диагностики).</summary>
    public int Count => _points.Count;

    private List<BrightnessPoint> Sorted() => [.. _points.OrderBy(p => p.Lux)];

    // +1 — чтобы 0 лк не улетал в -бесконечность
    private static double LogScale(float lux) => Math.Log10(1 + Math.Max(0, lux));

    /// <summary>
    /// Значимо ли изменение освещённости, чтобы вообще пересчитывать яркость: гистерезис в
    /// лог-шкале (шаг между «сумерками» и «чуть темнее» и между «улицей» и «чуть облачнее»
    /// ощущается одинаково). Порог 0.1 ≈ ±26% люксов.
    /// </summary>
    public static bool Significant(float fromLux, float toLux, double threshold = 0.1) =>
        float.IsNaN(fromLux) || Math.Abs(LogScale(toLux) - LogScale(fromLux)) >= threshold;
}

/// <summary>
/// Медиана люксов по скользящему окну времени — «инерция» авто-яркости (XIC-30): у датчика
/// нет интеграционной сферы, случайный блик даёт честный, но бесполезный всплеск на один-два
/// сэмпла. Медиана, в отличие от среднего, выбросом не сдвигается вообще: реагируем только
/// на изменения, продержавшиеся больше половины окна. Чистая логика — время передаётся явно.
/// </summary>
public sealed class MedianWindow
{
    private readonly List<(long Ms, float Lux)> _samples = [];

    public void Add(long nowMs, float lux, int windowMs)
    {
        Prune(nowMs, windowMs);
        _samples.Add((nowMs, lux));
    }

    /// <summary>Медиана окна; NaN — сэмплов нет. Чётное число — среднее двух средних.</summary>
    public float Median(long nowMs, int windowMs)
    {
        Prune(nowMs, windowMs);
        if (_samples.Count == 0) return float.NaN;
        var sorted = _samples.Select(s => s.Lux).OrderBy(v => v).ToArray();
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2f;
    }

    private void Prune(long nowMs, int windowMs) =>
        _samples.RemoveAll(s => nowMs - s.Ms > Math.Max(0, windowMs));
}
