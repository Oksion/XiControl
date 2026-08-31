namespace XiControl.SystemIntegration;

/// <summary>Край тачпада, отданный под ползунок.</summary>
public enum TouchpadEdge { None, Left, Right }

/// <summary>
/// Одно касание в нормализованных координатах: 0 — левый/верхний край панели, 1 — правый/нижний.
/// Доля, а не миллиметры, намеренно: физические единицы PTP объявляются в дюймах с показателем
/// (на TM2424 <c>Units=0x13</c>, <c>UnitExponent=-2</c>), и пересчёт «на глазок» уже однажды дал
/// 54,7 мм вместо 139 — доля от логического диапазона от этой трактовки не зависит вовсе.
/// </summary>
public readonly record struct TouchContact(int Id, double X, double Y);

/// <summary>
/// Распознавание жеста «ползунок у края тачпада» (XIC-61): палец, опустившийся в краевую полосу,
/// вертикальным движением набирает шаги — вверх положительные, вниз отрицательные. Чистая логика
/// без Win32 и таймеров, целиком под тестами; сырые касания приносит <see cref="RawTouchpadReader"/>.
///
/// <b>Принимаются только контакты, НАЧАВШИЕСЯ в полосе.</b> Это не придирка, а стыковка с
/// curtain-зоной, которая гасит именно инициацию касания (XIC-24): палец, начатый в середине
/// панели и заехавший в полосу, курсор двигать продолжает — и крутить им ещё и ползунок значило бы
/// делать два действия одним движением.
///
/// <b>Два пальца отменяют жест.</b> Двухпальцевое движение у Windows — прокрутка и масштаб;
/// перехватывать его нельзя, поэтому при втором касании начатый жест сбрасывается.
/// </summary>
public sealed class TouchpadEdgeGesture(double stripFraction = 0.1, double stepFraction = 0.06)
{
    /// <summary>Ширина краевой полосы как доля ширины панели. Клэмп — защита от кривого
    /// config.json: 0 отключил бы жест молча, а половина панели сделала бы тачпад неюзабельным.</summary>
    private readonly double _strip = Math.Clamp(stripFraction, 0.02, 0.35);

    /// <summary>Сколько высоты панели нужно пройти на один шаг. Мелкий шаг превращает ползунок
    /// в дёрганый, крупный — в тугой; доля, а не пиксели, чтобы не зависеть от размера панели.</summary>
    private readonly double _step = Math.Clamp(stepFraction, 0.01, 0.5);

    private int _id = -1;              // контакт, ведущий жест; -1 — жеста нет
    private TouchpadEdge _edge;        // край, в котором он начался
    private double _anchor;            // Y, от которого отсчитывается следующий шаг
    private bool _cancelled;           // жест отменён (второй палец) — до отрыва не возобновляем

    /// <summary>Идёт ли сейчас жест — для диагностики и тестов.</summary>
    public TouchpadEdge Active => _id >= 0 && !_cancelled ? _edge : TouchpadEdge.None;

    /// <summary>
    /// Очередной кадр касаний. Возвращает край и число шагов: положительное — вверх (ярче/громче),
    /// отрицательное — вниз. Ноль шагов означает «жест идёт, но порог ещё не набран».
    /// Пустой кадр (палец отпущен) сбрасывает состояние.
    /// </summary>
    public (TouchpadEdge Edge, int Steps) Update(IReadOnlyList<TouchContact> contacts)
    {
        if (contacts.Count == 0) { Reset(); return (TouchpadEdge.None, 0); }

        // Второй палец — это прокрутка Windows, а не наш ползунок. Отменяем до полного отрыва,
        // иначе подняв один палец человек неожиданно продолжил бы крутить яркость.
        if (contacts.Count > 1) { _cancelled = true; _id = -1; return (TouchpadEdge.None, 0); }
        if (_cancelled) return (TouchpadEdge.None, 0);

        var c = contacts[0];
        if (_id != c.Id)
        {
            // Новое касание: жест начинается, только если палец опустился внутри полосы.
            var edge = EdgeOf(c.X);
            if (edge == TouchpadEdge.None) { _cancelled = true; _id = -1; return (TouchpadEdge.None, 0); }
            _id = c.Id;
            _edge = edge;
            _anchor = c.Y;
            return (edge, 0);
        }

        // Y растёт вниз, а «вверх» человек ожидает как «больше» — отсюда знак минус.
        double moved = _anchor - c.Y;
        int steps = (int)(moved / _step);
        if (steps == 0) return (_edge, 0);
        _anchor -= steps * _step;   // не обнуляем: остаток переносится в следующий шаг
        return (_edge, steps);
    }

    /// <summary>Сбросить состояние: смена настроек, потеря фокуса, выключение фичи.</summary>
    public void Reset()
    {
        _id = -1;
        _edge = TouchpadEdge.None;
        _cancelled = false;
    }

    /// <summary>В какой полосе лежит X (доля ширины). Середина панели — None.</summary>
    public TouchpadEdge EdgeOf(double x) =>
        x <= _strip ? TouchpadEdge.Left : x >= 1 - _strip ? TouchpadEdge.Right : TouchpadEdge.None;
}
