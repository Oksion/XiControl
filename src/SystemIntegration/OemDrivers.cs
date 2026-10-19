using System.Globalization;
using System.Management;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace XiControl.SystemIntegration;

/// <summary>Пакет OEM: zip на их CDN. Ссылку только показываем — ничего не скачиваем и не ставим.</summary>
public sealed record OemPackage(string FileName, long Size, string Md5, string? Url);

/// <summary>Ответ по одному модулю: пакет и hardware-id, которые он закрывает, каждый со своей версией.</summary>
public sealed record OemModule(string Name, OemPackage? Package, IReadOnlyList<OemHardwareId> HardwareIds);

public sealed record OemHardwareId(string Id, string Version);

/// <summary>Установленный драйвер: один из hardware-/compatible-id устройства и версия его драйвера.</summary>
public sealed record InstalledDriver(string HardwareId, string Version, string Device);

/// <summary>Сверка одного устройства с каталогом. <see cref="Firmware"/> — прошивка, которую сравнить
/// нельзя: текущая версия не прочиталась или (у экрана) номера не сопоставимы — «в каталоге есть такая»,
/// и, если прочиталась, текущая рядом. Прошивка тачпада с прочитанной версией получает обычные
/// Same/Update/Newer (XIC-104).</summary>
public enum DriverState { Same, Update, Newer, Firmware }

public sealed record DriverRow(string Module, string Device, string? Installed, string Catalog, DriverState State, OemPackage? Package);

/// <summary>Итог проверки целиком.</summary>
public enum DriverCheckStatus { NotChecked, Running, Ok, NoCatalog, NoMatch, Failed }

public sealed record DriverCheckResult(
    DriverCheckStatus Status,
    string? Product,
    IReadOnlyList<DriverRow> Rows,
    string? BiosCurrent,
    OemPackage? BiosOffer,
    bool BiosChecked,
    int Failed,
    DateTime CheckedAt)
{
    public static readonly DriverCheckResult None =
        new(DriverCheckStatus.NotChecked, null, [], null, null, false, 0, default);
}

/// <summary>
/// Протокол проверки обновлений драйверов и BIOS у OEM (XIC-95, разбор — docs/16-oem-driver-check.md).
/// Чистые функции: тело, подпись, разбор ответа — под тестами; сеть в <see cref="OemDriverCheck"/>.
/// </summary>
public static class OemProtocol
{
    internal const string Url = "https://whippet.bsp.xiaomi.com/api/v2/nb/checkUpdate";
    internal const string AppId = "nb-DBRUc9Fdui";
    private const string Key = "OH3QAmMrso1DhOm4"; // оба зашиты в MiDeviceServiceEntry.dll

    /// <summary>Модули, которые служба спрашивает при полном скане (BIOS — отдельной веткой).</summary>
    internal static readonly string[] Modules =
    [
        "WLC", "WC", "IGC", "IDC", "SC", "BT", "FR", "TBT", "ISH", "GNA", "HID", "CG",
        "ME", "SIO", "HR", "RST", "NPU", "MEP", "PMT", "HDR", "NFC", "OTS", "FWTP", "FWSC",
    ];

    /// <summary>Прошивки: версии в каталоге — прошивочные, сравнивать их с версией драйвера нельзя.</summary>
    internal static bool IsFirmware(string module) => module is "FWTP" or "FWSC";

    internal const string TouchpadFirmware = "FWTP", PanelFirmware = "FWSC";

    /// <summary>
    /// Тело запроса. Ключи строго по алфавиту и компактно: служба собирает JSON на std::map, и
    /// подпись считается по ровно такой строке. Без <c>sn</c>: сервер отвечает так же, а хеш
    /// серийника — стабильный отпечаток машины, отдавать его незачем.
    /// </summary>
    internal static string BuildBody(string category, string modulesJson, string product, string lan, string region, string build) =>
        $"{{\"category\":{Str(category)},\"lan\":{Str(lan)},\"modules\":{modulesJson},\"product\":{Str(product)}," +
        $"\"region\":{Str(region)},\"winBuildNumber\":{Str(build)}}}";

    internal static string DriverModules(string module) => $"[{{\"moduleName\":{Str(module)}}}]";

    internal static string BiosModules(string version) => $"[{{\"moduleName\":\"BIOS\",\"version\":{Str(version)}}}]";

    /// <summary>MD5(appid + body + key), шестнадцатеричный в ВЕРХНЕМ регистре.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351",
        Justification = "MD5 задан протоколом сервера OEM — это подпись их API, а не наша защита")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S4790",
        Justification = "То же для Sonar: алгоритм подписи выбрал не XiControl, а сервер Xiaomi")]
    internal static string Sign(string body) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(AppId + body + Key)));

    /// <summary>Тело POST: <c>data="&lt;base64(root)&gt;"</c> — с кавычками, как шлёт служба.</summary>
    internal static string BuildPost(string body)
    {
        string root = $"{{\"body\":{body},\"header\":{{\"appid\":\"{AppId}\",\"sign\":\"{Sign(body)}\"}}}}";
        return "data=\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes(root)) + "\"";
    }

    /// <summary>
    /// Версия BIOS для запроса: «1.0.0.» + десятичное от последних четырёх hex-символов SMBIOS
    /// (<c>XMAPT4B0P0A0A</c> → <c>0A0A</c> → <c>1.0.0.2570</c>). Правило снято с TM2424; не подошло — null.
    /// </summary>
    internal static string? BiosRequestVersion(string? smbios)
    {
        if (smbios is null || smbios.Length < 4) return null;
        string tail = smbios[^4..];
        return int.TryParse(tail, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int v)
            ? "1.0.0." + v.ToString("D4", CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>Разобрать ответ. null — не JSON или <c>code</c> ≠ 0 (неверная подпись и т.п.);
    /// пустой список — «для этого модуля на эту модель пакета нет», это не ошибка.
    /// <paramref name="product"/> — модель, о которой спрашивали: модуль, у которого сервер явно
    /// назвал другую <c>product</c>, отбрасывается. Для драйверов это страховка поверх сверки по
    /// hardware-id, для прошивок FWSC/FWTP — единственная проверка, что пакет про эту модель:
    /// их id синтетические и с устройствами не сверяются (XIC-102, XIC-103).</summary>
    internal static List<OemModule>? ParseResponse(string json, string? product = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || code.GetInt32() != 0)
                return null;
            var list = new List<OemModule>();
            if (!root.TryGetProperty("modules", out var modules) || modules.ValueKind != JsonValueKind.Array) return list;
            foreach (var m in modules.EnumerateArray())
            {
                string name = Text(m, "moduleName") ?? "?";
                if (product is not null && Text(m, "product") is { } answered
                    && !answered.Equals(product, StringComparison.OrdinalIgnoreCase))
                    continue; // каталог чужой модели — не про этот ноутбук
                if (!m.TryGetProperty("latestVer", out var latest) || latest.ValueKind != JsonValueKind.Object) continue;

                // пакет у модуля один — FULL_PACKAGE
                OemPackage? package = latest.TryGetProperty("packages", out var pkgs)
                    && pkgs.ValueKind == JsonValueKind.Array && pkgs.GetArrayLength() > 0
                    ? ParsePackage(pkgs[0]) : null;

                // настоящая версия — в strategy.hardwareIds[].version; latestVer.version — заглушка "NS"
                var ids = new List<OemHardwareId>();
                if (latest.TryGetProperty("strategy", out var strategy)
                    && strategy.TryGetProperty("hardwareIds", out var hw) && hw.ValueKind == JsonValueKind.Array)
                    foreach (var h in hw.EnumerateArray())
                        if (Text(h, "id") is { Length: > 0 } id)
                            ids.Add(new OemHardwareId(id, Text(h, "version") ?? ""));
                list.Add(new OemModule(name, package, ids));
            }
            return list;
        }
        catch (JsonException) { return null; }
    }

    private static OemPackage ParsePackage(JsonElement p)
    {
        string? url = null;
        if (p.TryGetProperty("mirrors", out var mirrors) && mirrors.ValueKind == JsonValueKind.Array)
            foreach (var u in mirrors.EnumerateArray())
                if (u.ValueKind == JsonValueKind.String && u.GetString() is { } s
                    && s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) { url = s; break; }
        long size = p.TryGetProperty("fileSize", out var fs) && fs.TryGetInt64(out long l) ? l : 0;
        return new OemPackage(Text(p, "fileName") ?? "", size, Text(p, "md5") ?? "", url);
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Str(string s) => JsonSerializer.Serialize(s);
}

/// <summary>Сверка каталога с установленным — строго попарно по hardware-id (чистая логика);
/// прошивки — исключение, см. <see cref="Compare"/>.</summary>
public static class DriverMatch
{
    /// <summary>
    /// Установленный драйвер под id каталога: каталог даёт префикс, у устройства id длиннее
    /// (<c>ACPI\INTC105F</c> ⊂ <c>ACPI\INTC105F&amp;…</c>). Префикс — только по границе сегмента,
    /// иначе <c>DEV_E31</c> зацепил бы <c>DEV_E31B</c>.
    /// </summary>
    internal static InstalledDriver? Find(IReadOnlyList<InstalledDriver> installed, string catalogId)
    {
        foreach (var d in installed)
        {
            string id = d.HardwareId;
            if (id.Equals(catalogId, StringComparison.OrdinalIgnoreCase)) return d;
            if (id.Length > catalogId.Length && id.StartsWith(catalogId, StringComparison.OrdinalIgnoreCase)
                && id[catalogId.Length] is '&' or '\\')
                return d;
        }
        return null;
    }

    /// <summary>Сравнить версии: числовые — по числам, иначе строкой.</summary>
    internal static int CompareVersions(string a, string b) =>
        Version.TryParse(a, out var va) && Version.TryParse(b, out var vb)
            ? va.CompareTo(vb)
            : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Версия прошивки тачпада из каталожной: последние четыре числа
    /// (<c>3.5.13.01.44.25.13.0</c> → <c>44.25.13.0</c>; префикс <c>3.5.13.01</c> — номер пакета,
    /// он же в имени zip). Именно эти четыре числа отдаёт сам тачпад командой 0x10 (XIC-103).
    /// null — строка другого вида, сравнивать не с чем.
    /// </summary>
    internal static string? TouchpadCatalogVersion(string catalog)
    {
        var parts = catalog.Split('.');
        if (parts.Length < 4) return null;
        var tail = parts[^4..];
        return tail.All(p => p.Length > 0 && p.All(char.IsAsciiDigit)) ? string.Join('.', tail) : null;
    }

    /// <summary>
    /// Строки сверки. Версия — из записи ЭТОГО hardware-id, не «пакета»: один zip везёт драйверы
    /// разных вендоров (у аудио 10 разных версий на 125 id). Несколько id каталога могут указывать
    /// на одно устройство — строку даём одну.
    /// Прошивки (XIC-102) — без сверки по hardware-id: их id в каталоге синтетические
    /// (<c>USB\VID_6789&amp;PID_1234&amp;REV_1236</c> у экрана), такого устройства в системе нет.
    /// Строка — одна на модуль с пакетом; в <c>Device</c> — код модуля, имя ему даёт UI по локализации.
    /// Текущая версия прошивки — из <paramref name="firmware"/> (код модуля → прочитанное, XIC-104):
    /// у тачпада она сравнивается с хвостом каталожной, у экрана только встаёт рядом — ROM Ver панели
    /// отличается от каталожного старшим байтом, и «старее» это или другой вариант панели, по номеру
    /// не сказать (docs/16).
    /// </summary>
    internal static List<DriverRow> Compare(IEnumerable<OemModule> modules, IReadOnlyList<InstalledDriver> installed,
        IReadOnlyDictionary<string, string>? firmware = null)
    {
        var rows = new List<DriverRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in modules)
        {
            if (OemProtocol.IsFirmware(m.Name))
            {
                if (m.Package is not null && seen.Add(m.Name))
                    rows.Add(FirmwareRow(m, firmware?.GetValueOrDefault(m.Name)));
                continue;
            }
            foreach (var h in m.HardwareIds)
            {
                if (Find(installed, h.Id) is not { } inst) continue;
                if (!seen.Add($"{m.Name}|{inst.Device}|{h.Version}")) continue;
                int c = CompareVersions(h.Version, inst.Version);
                var state = c > 0 ? DriverState.Update : c < 0 ? DriverState.Newer : DriverState.Same;
                rows.Add(new DriverRow(m.Name, inst.Device, inst.Version, h.Version, state, m.Package));
            }
        }
        return rows;
    }

    private static DriverRow FirmwareRow(OemModule m, string? current)
    {
        string catalog = m.HardwareIds.Count > 0 ? m.HardwareIds[0].Version : "";
        var state = DriverState.Firmware;
        if (m.Name == OemProtocol.TouchpadFirmware && TouchpadCatalogVersion(catalog) is { } tail)
        {
            catalog = tail;
            if (current is not null)
            {
                int c = CompareVersions(tail, current);
                state = c > 0 ? DriverState.Update : c < 0 ? DriverState.Newer : DriverState.Same;
            }
        }
        return new DriverRow(m.Name, m.Name, current, catalog, state, m.Package);
    }
}

/// <summary>
/// Проверка у OEM: модель и BIOS — из WMI, установленное — Win32_PnPSignedDriver, каталог — по
/// одному запросу на модуль к серверу Xiaomi. Только по явному нажатию; ничего не скачивает и не
/// ставит. Любая сетевая ошибка — мягкая: модуль считается неотвеченным, проверка идёт дальше.
/// </summary>
public static class OemDriverCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private const int Parallel = 4; // вежливо к их серверу: полный скан — 25 запросов

    /// <summary>Звать только из фонового потока. <paramref name="currentFirmware"/> — чтение текущей
    /// версии прошивки по коду модуля (XIC-104); зовётся только для прошивок, на которые в каталоге
    /// есть пакет: у экрана чтение включает записи в контроллер, без повода их не делаем.</summary>
    public static async Task<DriverCheckResult> RunAsync(Func<string, string?>? currentFirmware = null, CancellationToken ct = default)
    {
        var sys = SystemInfo.Current;
        string? product = sys.Board;
        if (string.IsNullOrWhiteSpace(product))
            return DriverCheckResult.None with { Status = DriverCheckStatus.NoCatalog, CheckedAt = DateTime.Now };

        string build = WindowsBuild();
        var (lan, region) = Locale();
        var installed = ReadInstalled();

        using var http = new HttpClient { Timeout = Timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("XiControl-driver-check");
        using var gate = new SemaphoreSlim(Parallel);

        async Task<List<OemModule>?> Ask(string category, string modulesJson)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string body = OemProtocol.BuildBody(category, modulesJson, product, lan, region, build);
                using var content = new StringContent(OemProtocol.BuildPost(body), Encoding.ASCII, "application/x-www-form-urlencoded");
                using var resp = await http.PostAsync(OemProtocol.Url, content, ct).ConfigureAwait(false);
                string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var parsed = OemProtocol.ParseResponse(json, product);
                if (parsed is null) Log.Write($"OemDriverCheck: {category}/{modulesJson} — сервер ответил ошибкой ({Trim(json)})");
                return parsed;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                Log.Write($"OemDriverCheck: {category}/{modulesJson} — {ex.GetType().Name}: {ex.Message}");
                return null;
            }
            finally { gate.Release(); }
        }

        var driverTasks = OemProtocol.Modules.Select(m => Ask("driver", OemProtocol.DriverModules(m))).ToList();
        string? biosVer = OemProtocol.BiosRequestVersion(sys.Bios);
        var biosTask = biosVer is null ? Task.FromResult<List<OemModule>?>(null) : Ask("bios", OemProtocol.BiosModules(biosVer));
        var answers = await Task.WhenAll(driverTasks).ConfigureAwait(false);
        var bios = await biosTask.ConfigureAwait(false);

        int failed = answers.Count(a => a is null);
        var modules = answers.Where(a => a is not null).SelectMany(a => a!).ToList();
        var rows = DriverMatch.Compare(modules, installed, ReadFirmware(modules, currentFirmware));
        var status = failed == answers.Length ? DriverCheckStatus.Failed
            : modules.Count == 0 ? DriverCheckStatus.NoCatalog
            : rows.Count == 0 ? DriverCheckStatus.NoMatch
            : DriverCheckStatus.Ok;
        Log.Write($"OemDriverCheck: {product}, модулей с пакетом {modules.Count}, устройств сверено {rows.Count}, " +
                  $"обновлений {rows.Count(r => r.State == DriverState.Update)}, прошивок {rows.Count(r => r.State == DriverState.Firmware)}, без ответа {failed}");
        return new DriverCheckResult(status, product, rows, sys.Bios, bios?.FirstOrDefault()?.Package,
            bios is not null, failed, DateTime.Now);
    }

    // По очереди, а не параллельно: тачпад и контроллер панели — разные устройства, но спешить
    // некуда, а чтение версии у каждого — секунды пауз между кадрами
    private static Dictionary<string, string>? ReadFirmware(List<OemModule> modules, Func<string, string?>? read)
    {
        if (read is null) return null;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in modules.Where(m => OemProtocol.IsFirmware(m.Name) && m.Package is not null)
                     .Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string? v = null;
            try { v = read(name); }
            catch (Exception ex) { Log.Ex($"OemDriverCheck.ReadFirmware({name})", ex); }
            Log.Write($"OemDriverCheck: текущая прошивка {name} — {v ?? "не прочиталась"}");
            if (v is not null) result[name] = v;
        }
        return result;
    }

    private static string Trim(string s) => s.Length <= 120 ? s : s[..120] + "…";

    private static string WindowsBuild()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return k?.GetValue("CurrentBuildNumber") as string ?? Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture);
        }
        catch { return Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture); }
    }

    // как у службы: язык интерфейса и регион системы (ru_RU / RU); региональной блокировки нет
    private static (string Lan, string Region) Locale()
    {
        try
        {
            var ui = CultureInfo.InstalledUICulture;
            var region = RegionInfo.CurrentRegion;
            return (ui.Name.Replace('-', '_'), region.TwoLetterISORegionName);
        }
        catch { return ("en_US", "US"); }
    }

    private static List<InstalledDriver> ReadInstalled()
    {
        var list = new List<InstalledDriver>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT DeviceName, DriverVersion, HardwareID, CompatID FROM Win32_PnPSignedDriver");
            foreach (ManagementObject mo in s.Get())
                using (mo)
                {
                    if (mo["DriverVersion"] is not string ver || string.IsNullOrEmpty(ver)) continue;
                    string device = mo["DeviceName"] as string ?? "?";
                    foreach (string key in new[] { "HardwareID", "CompatID" })
                        if (mo[key] is string id && id.Length > 0)
                            list.Add(new InstalledDriver(id, ver, device));
                }
        }
        catch (Exception ex) { Log.Ex("OemDriverCheck.ReadInstalled", ex); }
        return list;
    }
}
