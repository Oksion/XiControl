using System.Globalization;
using System.Management;

namespace XiControl.SystemIntegration;

/// <summary>
/// Сведения о железе из WMI (только чтение): модель, код платы, версия BIOS, серийный номер.
/// Driver-free — те же классы, что читает любой диагностический софт.
///
/// Кэш на весь сеанс: окно настроек пересобирается на каждый показ (и на смену темы, DPI,
/// языка), поэтому запрос из конструктора вкладки улетал бы по нескольку раз за сессию.
/// Железо за время работы приложения не меняется — читаем один раз, лениво.
/// </summary>
public sealed class SystemInfo
{
    private static readonly Lazy<SystemInfo> Cached = new(Read, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Сведения этой машины (читаются при первом обращении, дальше — из памяти).</summary>
    public static SystemInfo Current => Cached.Value;

    /// <summary>Название модели, напр. «Xiaomi Book Pro 14». null — WMI не отдал.</summary>
    public string? Model { get; private init; }

    /// <summary>Код платы, напр. «TM2424» — именно он нужен в отчётах о совместимости.</summary>
    public string? Board { get; private init; }

    /// <summary>Версия BIOS, напр. «XMAPT4B0P0A0A».</summary>
    public string? Bios { get; private init; }

    /// <summary>Дата выпуска BIOS (yyyy-MM-dd) — нейтральный формат, одинаковый во всех языках.</summary>
    public string? BiosDate { get; private init; }

    /// <summary>Серийный номер целиком. В UI по умолчанию не показываем — см. <see cref="SerialMasked"/>.</summary>
    public string? Serial { get; private init; }

    /// <summary>Модель с кодом платы: «Xiaomi Book Pro 14 (TM2424)».</summary>
    public string? ModelLine => FormatModel(Model, Board);

    /// <summary>BIOS с датой: «XMAPT4B0P0A0A · 2026-06-17».</summary>
    public string? BiosLine => FormatBios(Bios, BiosDate);

    /// <summary>
    /// Серийник с закрытой серединой. Мы сами просим присылать скриншоты этой вкладки в тему
    /// совместимости — незачем раздавать вместе с ними уникальный идентификатор устройства.
    /// Полное значение доступно по клику (см. AboutTab).
    /// </summary>
    public string? SerialMasked => Mask(Serial);

    /// <summary>Модель + код платы. Код не дублируем, если он и так внутри названия модели.</summary>
    internal static string? FormatModel(string? model, string? board)
    {
        model = Clean(model);
        board = Clean(board);
        if (model is null) return board;
        if (board is null || model.Contains(board, StringComparison.OrdinalIgnoreCase)) return model;
        return $"{model} ({board})";
    }

    /// <summary>Версия BIOS и дата выпуска; без даты — просто версия.</summary>
    internal static string? FormatBios(string? bios, string? date)
    {
        bios = Clean(bios);
        date = Clean(date);
        if (bios is null) return null;
        return date is null ? bios : $"{bios} · {date}";
    }

    /// <summary>Закрыть середину: хвост из 4 знаков оставляем — по нему владелец узнаёт свой номер.</summary>
    internal static string? Mask(string? serial)
    {
        serial = Clean(serial);
        if (serial is null) return null;
        if (serial.Length <= 4) return new string('•', serial.Length);
        return new string('•', Math.Min(8, serial.Length - 4)) + serial[^4..];
    }

    /// <summary>WMI охотно отдаёт пробелы и заглушки вроде «To be filled by O.E.M.» — это не значение.</summary>
    internal static string? Clean(string? raw)
    {
        raw = raw?.Trim();
        if (string.IsNullOrEmpty(raw)) return null;
        return raw.Contains("to be filled", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("system manufacturer", StringComparison.OrdinalIgnoreCase)
            || raw is "Default string" or "None" or "N/A" or "0"
            ? null
            : raw;
    }

    /// <summary>Дата BIOS приходит в CIM-формате (20260617050000.000000+000).</summary>
    internal static string? ParseCimDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(raw).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        catch (Exception ex) { Log.Ex("SystemInfo.ParseCimDate", ex); return null; }
    }

    private static SystemInfo Read() => new()
    {
        Model = Query("SELECT Model FROM Win32_ComputerSystem", "Model"),
        Board = Query("SELECT Product FROM Win32_BaseBoard", "Product"),
        Bios = Query("SELECT SMBIOSBIOSVersion FROM Win32_BIOS", "SMBIOSBIOSVersion"),
        BiosDate = ParseCimDate(Query("SELECT ReleaseDate FROM Win32_BIOS", "ReleaseDate")),
        Serial = Query("SELECT SerialNumber FROM Win32_BIOS", "SerialNumber"),
    };

    // Одно поле одного класса; на несовместимой машине WMI просто молчит — деградируем в null
    private static string? Query(string wql, string field)
    {
        try
        {
            using var s = new ManagementObjectSearcher(wql);
            foreach (ManagementObject mo in s.Get())
                using (mo)
                    return mo[field]?.ToString();
        }
        catch (Exception ex) { Log.Ex($"SystemInfo.Query({field})", ex); }
        return null;
    }
}
