using XiControl.Wmi;

namespace XiControl.Config;

/// <summary>
/// Выученный набор режимов производительности (XIC-44). Прошивка у каждой модели своя: на
/// Book Pro 14 не принимается «Баланс», на Redmi Book Pro 15 2022 работают только «Тихий» и
/// «Баланс». Свипа при старте мы намеренно НЕ делаем — он физически гоняет ноутбук через
/// Турбо и Полную мощность. Вместо этого учимся на отказах: человек выбрал режим, прошивка
/// его отвергла — запомнили.
///
/// Разрез по источнику питания обязателен: «Полная мощность» на TM2424 поддержана, но
/// на батарее прошивка её не примет. Отказ только на батарее — не повод хоронить режим,
/// поэтому прячем лишь то, что отвергнуто НА ОБОИХ источниках.
///
/// Чистая логика без железа — целиком под тестами.
/// </summary>
public static class ModeLearning
{
    /// <summary>Ключи источников питания. Строки, а не bool, чтобы третий вид разъёма
    /// (barrel против USB-C) добавился без ломки формата конфига.</summary>
    public const string Ac = "ac";
    public const string Battery = "battery";

    public static string Source(bool online) => online ? Ac : Battery;

    /// <summary>Отметить явный отказ. true — состояние изменилось и его нужно сохранить.</summary>
    public static bool Record(Dictionary<string, List<PerfMode>> rejected, PerfMode mode, bool online)
    {
        string key = Source(online);
        if (!rejected.TryGetValue(key, out var list)) rejected[key] = list = [];
        if (list.Contains(mode)) return false;
        list.Add(mode);
        return true;
    }

    /// <summary>
    /// Отвергнут и от сети, и от батареи — значит на этой машине режима нет вовсе и держать
    /// его в панели незачем. Одного источника мало: см. «Полную мощность» выше.
    /// </summary>
    public static bool RejectedEverywhere(
        IReadOnlyDictionary<string, List<PerfMode>>? rejected, PerfMode mode) =>
        rejected is not null
        && rejected.TryGetValue(Ac, out var ac) && ac.Contains(mode)
        && rejected.TryGetValue(Battery, out var battery) && battery.Contains(mode);

    /// <summary>
    /// Протухло ли выученное. Обновление BIOS может добавить режимы — вечно помнить старые
    /// отказы значит навсегда лишить человека того, что теперь работает. Пустая текущая
    /// версия (не прочиталась) поводом для сброса не считается.
    /// </summary>
    public static bool Expired(string? learnedFor, string? currentBios) =>
        !string.IsNullOrWhiteSpace(currentBios)
        && !string.IsNullOrWhiteSpace(learnedFor)
        && !string.Equals(learnedFor, currentBios, StringComparison.OrdinalIgnoreCase);
}
