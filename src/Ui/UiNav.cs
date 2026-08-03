namespace XiControl.Ui;

/// <summary>
/// Чистая арифметика навигации по UI: порядок обхода ячеек панели, перемещение фокуса
/// стрелками, выбор вкладки настроек. Вынесено из форм намеренно — тут нет ни рендера,
/// ни окон, поэтому логика проверяется юнит-тестами, а не только глазами (XIC-9).
/// </summary>
internal static class UiNav
{
    /// <summary>
    /// Порядок клавиатурного обхода ячеек быстрой панели: сначала режимы, затем нижний ряд
    /// слева направо, в конце кнопки шапки (шестерёнка, монитор, крестик).
    ///
    /// Скрытые ячейки в обход не попадают — иначе стрелка уводила бы фокус в пустоту:
    /// подсветить нечего, а нажатие ничего не делает.
    ///
    /// Идентификаторы совпадают с <c>_hover</c>/<c>HitTest</c>: 0..N-1 — режимы, 10 — «беречь»,
    /// 11 — 100%, 12 — закрыть, 13 — сова, 14 — монитор, 15 — герцовка, 16 — «в дорогу»,
    /// 17 — тачпад, 18 — тачскрин, 19 — настройки.
    /// </summary>
    public static List<int> PanelOrder(int modes, bool touchscreen, bool touchpad, bool hz, bool awake)
    {
        var order = new List<int>();
        for (int i = 0; i < modes; i++) order.Add(i);
        order.Add(16); order.Add(10); order.Add(11);
        if (touchscreen) order.Add(18);
        if (touchpad) order.Add(17);
        if (hz) order.Add(15);
        if (awake) order.Add(13);
        order.Add(19); order.Add(14); order.Add(12);
        return order;
    }

    /// <summary>
    /// Сдвиг фокуса стрелкой. Обход циклический; из состояния «фокуса ещё нет» (-1) вперёд
    /// попадаем на первую ячейку, назад — на последнюю. Пустой обход фокуса не имеет.
    /// </summary>
    public static int NextFocus(int focus, int count, bool forward)
    {
        if (count <= 0) return -1;
        return forward
            ? (focus + 1 + count) % count
            : (focus <= 0 ? count : focus) - 1;
    }

    /// <summary>
    /// Индекс фокуса после пересборки раскладки: если ячеек стало меньше, прежний индекс
    /// указывает в никуда — сбрасываем. Иначе фокус пережил бы скрытие фичи и рисовался
    /// вокруг несуществующей ячейки.
    /// </summary>
    public static int KeepFocus(int focus, int count) => focus >= count ? -1 : focus;

    /// <summary>
    /// Активная вкладка настроек после пересборки окна. Вкладки могут исчезать («Экран»
    /// скрыт, когда авто-герцовка выключена), поэтому индекс вне диапазона уводим на первую.
    /// </summary>
    public static int ClampTab(int tab, int count) => tab >= 0 && tab < count ? tab : 0;
}
