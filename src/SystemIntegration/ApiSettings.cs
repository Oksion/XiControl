using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace XiControl.SystemIntegration;

/// <summary>
/// Настройки HTTP API (XIC-13). Хранятся отдельно от config.json — в
/// %ProgramData%\XiControl\api.json с ACL «запись только Administrators/SYSTEM»:
/// config.json переписывается любым процессом пользователя, и держать там флаг включения
/// сетевого входа в admin-процесс нельзя (сторонний софт включил бы API и вписал свой токен).
/// Правка файла требует elevation — на эту границу и опираемся; DPAPI(CurrentUser) не защита:
/// процесс того же пользователя расшифрует и перепишет так же легко.
/// </summary>
public sealed class ApiSettings
{
    /// <summary>API включён. По умолчанию выключен — хост даже не создаётся (0 CPU).</summary>
    public bool Enabled { get; set; }

    /// <summary>TCP-порт слушателя (1024–65535).</summary>
    public int Port { get; set; } = 58125;

    /// <summary>Доступ из локальной сети: bind на все интерфейсы + firewall-правило LocalSubnet.
    /// false (по умолчанию) — только 127.0.0.1: снаружи не достучаться даже с токеном.</summary>
    public bool LanAccess { get; set; }

    /// <summary>SHA-256 токена (hex). Плейнтекст нигде не хранится — показывается один раз
    /// при генерации во вкладке «HTTP API». Пока токен не сгенерирован, все запросы — 401.</summary>
    public string? TokenSha256 { get; set; }

    // Пер-командные разрешения (белый список поверх белого списка): по умолчанию всё
    // выключено — доступен только GET /status. Write-команды включаются поштучно.
    public bool AllowMode { get; set; }
    public bool AllowCare { get; set; }
    public bool AllowTravel { get; set; }
    public bool AllowOwl { get; set; }
}

/// <summary>Загрузка/сохранение api.json. Пишем всегда с ужесточённым ACL — пишет наш
/// elevated-процесс, обычные процессы пользователя файл только читают.</summary>
public static class ApiSettingsStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "XiControl");
    private static readonly string FilePath = Path.Combine(Dir, "api.json");
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static ApiSettings Load()
    {
        try
        {
            // Startup elevated-процесса: сразу ужесточить ACL папки — в ProgramData обычный
            // пользователь МОЖЕТ создавать файлы, и без этого малварь подложила бы свой api.json
            // до первого сохранения нашего (TOCTOU на первом создании).
            HardenDir();
            if (!File.Exists(FilePath)) return new ApiSettings();
            // Вторая линия: доверяем только файлу, созданному elevated-процессом (владелец —
            // Administrators/SYSTEM). Чужой владелец = файл подложен без elevation → игнорируем.
            if (!OwnerIsAdmin(FilePath))
            {
                Log.Write("ApiSettings: api.json создан не администратором — игнорируем");
                return new ApiSettings();
            }
            return JsonSerializer.Deserialize<ApiSettings>(File.ReadAllText(FilePath)) ?? new ApiSettings();
        }
        catch (Exception ex) { Log.Ex("ApiSettings.Load", ex); }
        return new ApiSettings();
    }

    public static void Save(ApiSettings s)
    {
        try
        {
            HardenDir();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s, WriteOptions));
            HardenFile(FilePath);
        }
        catch (Exception ex) { Log.Ex("ApiSettings.Save", ex); }
    }

    // SID-ы по WellKnownSidType — не по именам групп (имена локализованы).
    private static SecurityIdentifier System_ => new(WellKnownSidType.LocalSystemSid, null);
    private static SecurityIdentifier Admins => new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static SecurityIdentifier Users => new(WellKnownSidType.BuiltinUsersSid, null);

    private static bool OwnerIsAdmin(string path)
    {
        var owner = new FileInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        return Admins.Equals(owner) || System_.Equals(owner);
    }

    // ACL папки: запись — только SYSTEM/Administrators, Users — чтение. Наследование отрезано,
    // иначе Users унаследовали бы от ProgramData право создавать файлы внутри.
    private static void HardenDir()
    {
        var di = Directory.CreateDirectory(Dir);
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(System_, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.SetOwner(Admins); // детерминированный владелец: проверка OwnerIsAdmin не зависит от того, кто создал папку
        di.SetAccessControl(sec);
    }

    // ACL файла — то же самое (файл мог быть создан до ужесточения папки).
    private static void HardenFile(string path)
    {
        var sec = new FileSecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sec.AddAccessRule(new FileSystemAccessRule(System_, FileSystemRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Read, AccessControlType.Allow));
        sec.SetOwner(Admins);
        new FileInfo(path).SetAccessControl(sec);
    }
}
