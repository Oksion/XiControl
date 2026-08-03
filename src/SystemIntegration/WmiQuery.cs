using System.Management;

namespace XiControl.SystemIntegration;

/// <summary>
/// «Первый объект WMI-запроса» — наш типовой приём: яркость, поля BIOS, статус батареи
/// существуют в единственном экземпляре. Раньше это писалось циклом с return на первой
/// итерации — читателю приходилось догадываться, что перебора не будет (и SonarCloud S1751
/// резонно спотыкался о «цикл, который никогда не повторяется»). Здесь намерение названо
/// словом, а disposal коллекции и объекта живёт в одном месте. Исключения намеренно не
/// глотаются: у каждого вызывающего свой контекст лога и свой фолбэк.
/// </summary>
public static class WmiQuery
{
    /// <summary>Первый объект, спроецированный в значимый тип; null — объектов нет.
    /// Именно null, а не default(T): ноль спутался бы с валидной яркостью или мощностью.</summary>
    public static T? First<T>(ManagementObjectSearcher searcher, Func<ManagementObject, T> map) where T : struct
    {
        using var all = searcher.Get();
        using var e = all.GetEnumerator();
        if (!e.MoveNext()) return null;
        using var mo = (ManagementObject)e.Current;
        return map(mo);
    }

    /// <summary>То же для строкового результата (поля BIOS/платы в SystemInfo).
    /// Отдельным именем: перегрузка по одной лишь nullable-аннотации невозможна (CS0111).</summary>
    public static string? FirstString(ManagementObjectSearcher searcher, Func<ManagementObject, string?> map)
    {
        using var all = searcher.Get();
        using var e = all.GetEnumerator();
        if (!e.MoveNext()) return null;
        using var mo = (ManagementObject)e.Current;
        return map(mo);
    }
}
