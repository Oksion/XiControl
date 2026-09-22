using XiControl.Wmi;

namespace XiControl.Config;

/// <summary>
/// Какие режимы производительности показывать. Чистая логика — вынесена из контроллера ради
/// тестов: правило «минимум два» легко сломать незаметно, а сломанное оно оставляет человека
/// с одной кнопкой, которая ничего не переключает.
///
/// Пока нет автоопределения набора режимов (XIC-44), это единственный способ убрать с глаз
/// режимы, которые прошивка конкретной модели не принимает: на TM2113 из пяти работают два,
/// на TM2424 не работает Balance.
/// </summary>
public static class ModeVisibility
{
    /// <summary>Меньше двух видимых режимов не бывает: переключать было бы не на что.</summary>
    public const int Minimum = 2;

    /// <summary>
    /// Скрытые режимы для одного источника питания (XIC-65). Нет записи — ничего не скрыто:
    /// новый источник начинает с полного набора, а не с пустого.
    /// </summary>
    public static IReadOnlyList<PerfMode> For(
        IReadOnlyDictionary<string, List<PerfMode>>? bySource, bool online) =>
        bySource is not null && bySource.TryGetValue(ModeLearning.Source(online), out var list)
            ? list : [];

    /// <summary>
    /// Разложить общий список скрытых по обоим источникам — миграция со времён, когда
    /// видимость была одна на всё. Прятал человек режим вообще, значит он остаётся скрытым
    /// и от сети, и от батареи: внезапно вернувшаяся ячейка читалась бы как поломка.
    /// </summary>
    public static Dictionary<string, List<PerfMode>> Split(IEnumerable<PerfMode>? hidden)
    {
        var all = hidden?.Distinct().ToList() ?? [];
        return new Dictionary<string, List<PerfMode>>
        {
            [ModeLearning.Ac] = [.. all],
            [ModeLearning.Battery] = [.. all],
        };
    }

    /// <summary>
    /// Видимые режимы в порядке <paramref name="all"/>. Скрытые из конфига применяются, только
    /// если после них останется хотя бы <see cref="Minimum"/>: кривая ручная правка (скрыли всё)
    /// не должна отбирать выбор — в этом случае показываем всё, как при первом запуске.
    /// </summary>
    public static PerfMode[] Visible(IReadOnlyList<PerfMode> all, IEnumerable<PerfMode>? hidden)
    {
        if (hidden is null) return [.. all];

        var off = new HashSet<PerfMode>(hidden);
        var visible = all.Where(m => !off.Contains(m)).ToArray();
        return visible.Length >= Minimum ? visible : [.. all];
    }

    /// <summary>
    /// Можно ли скрыть ещё один режим при таком количестве видимых. Тумблер последних двух
    /// в настройках гасится этим же правилом — чтобы человек не жал по кнопке, которая молча
    /// ничего не делает.
    /// </summary>
    public static bool CanHide(int visibleCount) => visibleCount > Minimum;

    /// <summary>
    /// Новый набор скрытых после переключения одного режима. Запрет возвращает набор без
    /// изменений — вызывающему не нужно знать правило, достаточно сравнить результат.
    /// </summary>
    public static PerfMode[] Toggle(
        IReadOnlyList<PerfMode> all, IEnumerable<PerfMode>? hidden, PerfMode mode, bool visible)
    {
        var off = new HashSet<PerfMode>(hidden ?? []);
        if (visible) { off.Remove(mode); return [.. off]; }

        // прячем — только если после этого останется из чего выбирать
        if (!CanHide(Visible(all, off).Length)) return [.. off];
        off.Add(mode);
        return [.. off];
    }
}
