using XiControl.Config;

namespace XiControl.SystemIntegration;

/// <summary>
/// Правила ручной правки кривой авто-яркости: подвинуть точку (XIC-33), добавить и убрать
/// (XIC-66). Чистые функции над снимком точек — никакого UI, конфига и замков: график только
/// показывает результат и отдаёт его на сохранение.
///
/// Две вещи здесь не «ограничения интерфейса», а свойства кривой, которые нельзя потерять:
/// <list type="bullet">
/// <item>Монотонность. Кривая обучения всегда была неубывающей (см. вытеснение в
/// <see cref="BrightnessCurve.Learn"/>), и мышь не должна уметь сломать то, что обучение
/// бережёт: «светлее в комнате — темнее экран» не имеет объяснения.</item>
/// <item>Различимость соседей. Точки ближе гистерезиса друг к другу — противоречивые мнения
/// об одних и тех же условиях; между ними вырастает обрыв, а обучение всё равно съест одну
/// из них при первой же правке.</item>
/// </list>
///
/// Поэтому <see cref="Move"/> зажимает точку между соседями, а <see cref="Add"/> отказывает,
/// если ставить некуда — возвращает <c>null</c>, и вызывающий просто ничего не делает.
/// </summary>
public static class CurveEdit
{
    /// <summary>Меньше двух точек — уже не кривая, а константа: интерполировать нечего.</summary>
    public const int MinPoints = 2;

    /// <summary>Верхний предел числа точек. Движок ограничения не имеет (в config.json можно
    /// вписать сколько угодно), но мышью на карточке в 130 px больше дюжины точек не развести,
    /// да и смысла нет — с этим автор просьбы согласился сам.</summary>
    public const int MaxPoints = 12;

    /// <summary>Правее только прямое солнце — там же кончается шкала графика.</summary>
    public const float MaxLux = 10_000f;

    /// <summary>Подвинуть точку <paramref name="index"/>: люксы и проценты зажимаются так,
    /// чтобы порядок и монотонность сохранились. Отказа нет — двигать можно всегда, просто
    /// не дальше соседей.</summary>
    public static List<BrightnessPoint> Move(
        IReadOnlyList<BrightnessPoint> pts, int index, float lux, int percent, double minGap = 0.1)
    {
        var list = Sorted(pts);
        if (index < 0 || index >= list.Count) return list;

        // потолок и пол по проценту — соседи: между ними точка ходит свободно
        int lo = index > 0 ? list[index - 1].Percent : 0;
        int hi = index < list.Count - 1 ? list[index + 1].Percent : 100;
        list[index].Percent = Math.Clamp(Math.Clamp(percent, 0, 100), Math.Min(lo, hi), Math.Max(lo, hi));
        list[index].Lux = ClampLux(list, index, lux, minGap);
        return list;
    }

    /// <summary>Добавить точку при таком свете и такой яркости. <c>null</c> — места нет:
    /// точек уже предел или рядом (ближе гистерезиса) есть своя.</summary>
    public static List<BrightnessPoint>? Add(
        IReadOnlyList<BrightnessPoint> pts, float lux, int percent, double minGap = 0.1)
    {
        var list = Sorted(pts);
        if (list.Count >= MaxPoints) return null;

        lux = Math.Clamp(lux, 0, MaxLux);
        if (list.Any(p => Math.Abs(LogScale(p.Lux) - LogScale(lux)) < minGap)) return null;

        // новая точка тоже подчиняется монотонности: ткнуть мышью ниже левого соседа можно,
        // а вот получить из этого «светлее — темнее» нельзя
        int at = list.FindIndex(p => p.Lux > lux);
        if (at < 0) at = list.Count;
        int lo = at > 0 ? list[at - 1].Percent : 0;
        int hi = at < list.Count ? list[at].Percent : 100;
        list.Insert(at, new BrightnessPoint
        {
            Lux = lux,
            Percent = Math.Clamp(Math.Clamp(percent, 0, 100), Math.Min(lo, hi), Math.Max(lo, hi)),
        });
        return list;
    }

    /// <summary>Убрать точку. <c>null</c> — последние две не отдаём.</summary>
    public static List<BrightnessPoint>? Remove(IReadOnlyList<BrightnessPoint> pts, int index)
    {
        var list = Sorted(pts);
        if (list.Count <= MinPoints || index < 0 || index >= list.Count) return null;
        list.RemoveAt(index);
        return list;
    }

    // Люксы точки — строго между соседями, с зазором различимости. Если соседи стоят теснее
    // двух зазоров (так бывает у выученных кривых: обучение считает зазор по СВОЕМУ порогу,
    // а он настраивается), становимся ровно посередине — это ближайшее законное место.
    private static float ClampLux(List<BrightnessPoint> list, int index, float lux, double minGap)
    {
        double want = LogScale(Math.Clamp(lux, 0, MaxLux));
        double lo = index > 0 ? LogScale(list[index - 1].Lux) + minGap : 0;
        double hi = index < list.Count - 1 ? LogScale(list[index + 1].Lux) - minGap : LogScale(MaxLux);
        double log = lo > hi ? (lo + hi) / 2 : Math.Clamp(want, lo, hi);
        return (float)Math.Round(Math.Max(0, Math.Pow(10, log) - 1), 1);
    }

    // копия, а не ссылки: правка не должна проступать в конфиге до сохранения
    private static List<BrightnessPoint> Sorted(IEnumerable<BrightnessPoint> pts) =>
        [.. pts.OrderBy(p => p.Lux).Select(p => new BrightnessPoint { Lux = p.Lux, Percent = p.Percent })];

    private static double LogScale(float lux) => Math.Log10(1 + Math.Max(0, lux));
}
