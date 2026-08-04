using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace XiControl.SystemIntegration;

/// <summary>
/// Автозапуск через Планировщик заданий — единственный корректный способ
/// стартовать elevated-приложение при входе БЕЗ UAC-запроса.
/// Задача создаётся с RunLevel=HighestAvailable и разрешением работы на батарее.
/// </summary>
public static class AutoStart
{
    /// <summary>Имя задачи до XIC-23 — одно на всю машину. Второй пользователь, включив автозапуск,
    /// перезаписывал задачу первого, и у того автозапуск молча ломался.</summary>
    private const string LegacyTaskName = "XiControl";

    /// <summary>Своя задача на пользователя: SID в имени (как GHelper_&lt;SID&gt;).</summary>
    private static string TaskName => $"XiControl_{CurrentSid}";

    private static string CurrentSid => WindowsIdentity.GetCurrent().User?.Value ?? "unknown";

    private static string SchTasks =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");

    /// <summary>Автозапуск включён: своя задача или доставшаяся от старой версии (только наша).</summary>
    public static bool IsEnabled() => Run("/query", "/tn", TaskName) == 0 || LegacyIsOurs();

    /// <summary>
    /// Задача старого образца существует И принадлежит текущему пользователю. Чужую (соседней
    /// учётки) не трогаем никогда — иначе сломаем ей автозапуск, то есть ровно тот баг, что чиним.
    /// </summary>
    private static bool LegacyIsOurs()
    {
        string? xml = RunRead("/query", "/tn", LegacyTaskName, "/xml");
        return xml is not null && OwnedByCurrentUser(xml, WindowsIdentity.GetCurrent().Name, CurrentSid);
    }

    /// <summary>
    /// Владелец задачи — этот пользователь? Планировщик хранит `UserId` в двух видах: мы пишем
    /// везде DOMAIN\User, но триггерный он нормализует в SID (проверено на живой задаче), поэтому
    /// засчитываем любое совпадение.
    /// </summary>
    internal static bool OwnedByCurrentUser(string taskXml, string userName, string sid)
    {
        foreach (var m in System.Text.RegularExpressions.Regex.Matches(taskXml,
                     @"<UserId>\s*(.*?)\s*</UserId>",
                     System.Text.RegularExpressions.RegexOptions.Singleline, TimeSpan.FromSeconds(1))
                 .Cast<System.Text.RegularExpressions.Match>())
        {
            string id = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
            if (id.Equals(userName, StringComparison.OrdinalIgnoreCase) ||
                id.Equals(sid, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Пора ли пересоздать задачу из-за версии exe: путь живой, но ведёт на сборку СТАРЕЕ текущей
    /// (типовой случай portable — распаковали в новую папку, а задача поднимает прежнюю).
    /// Нерелизная сборка (`0.0.*` — локальная `0.0.0` или тестовая из main `0.0.<прогон CI>`) не
    /// участвует ни с одной стороны: иначе локальный/тестовый запуск переписывал бы пользовательскую
    /// задачу на себя, а равные версии гоняли бы пересоздание по кругу.
    /// </summary>
    internal static bool IsOutdated(string? taskFileVersion, Version? current)
    {
        if (current is null || !Version.TryParse(taskFileVersion, out var inTask)) return false;
        if (IsDev(inTask) || IsDev(current)) return false;
        return inTask < current;

        static bool IsDev(Version v) => v is { Major: 0, Minor: 0 };
    }

    /// <summary>
    /// Самопочинка: задача указывает на exe, которого больше нет, ИЛИ на сборку старее текущей
    /// (обновились, а задача поднимает прежнюю папку — выглядит как «обновление не применилось»).
    /// Пересоздаём на текущий exe. Вызывается один раз на старте; периодических проверок нет.
    /// </summary>
    public static void RepairIfBroken()
    {
        if (Environment.ProcessPath is null) return;
        var cmd = TaskCommand();
        if (cmd is null) return; // задачи нет — чинить нечего

        if (!File.Exists(cmd))
        {
            Log.Write($"AutoStart: задача указывает на пропавший exe ({cmd}) — пересоздаю на текущий");
            Enable();
            return;
        }

        string? taskVersion;
        try { taskVersion = FileVersionInfo.GetVersionInfo(cmd).FileVersion; }
        catch (Exception ex) { Log.Ex("AutoStart.FileVersion", ex); return; } // не прочитали — не трогаем чужое

        if (IsOutdated(taskVersion, System.Reflection.Assembly.GetExecutingAssembly().GetName().Version))
        {
            Log.Write($"AutoStart: задача поднимает старую сборку ({taskVersion}, {cmd}) — пересоздаю на текущий");
            Enable();
        }
    }

    /// <summary>Путь exe в существующей задаче (своей или доставшейся от старой версии);
    /// null — задачи нет / не разобрали XML.</summary>
    private static string? TaskCommand()
    {
        var xml = RunRead("/query", "/tn", TaskName, "/xml");
        if (xml is null && LegacyIsOurs()) xml = RunRead("/query", "/tn", LegacyTaskName, "/xml");
        if (xml is null) return null;
        var m = System.Text.RegularExpressions.Regex.Match(xml, @"<Command>\s*(.*?)\s*</Command>",
            System.Text.RegularExpressions.RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value) : null;
    }

    public static void Set(bool enabled)
    {
        if (enabled) Enable(); else Disable();
    }

    private static bool Enable()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "XiControl_task.xml");
        try
        {
            // сначала убрать свою задачу старого образца: оставить обе — значит поднимать два
            // экземпляра при входе (мьютекс второй погасит, но в трее мелькнёт и лог засорится)
            if (LegacyIsOurs())
            {
                Log.Write($"AutoStart: переношу задачу «{LegacyTaskName}» на имя с SID");
                Run("/delete", "/tn", LegacyTaskName, "/f");
            }

            // schtasks /xml требует файл в кодировке UTF-16
            File.WriteAllText(tmp, BuildXml(), Encoding.Unicode);
            return Run("/create", "/tn", TaskName, "/xml", tmp, "/f") == 0;
        }
        catch (Exception ex) { Log.Ex("AutoStart.Enable", ex); return false; }
        finally { try { File.Delete(tmp); } catch { /* tmp занят/уже удалён — не критично */ } }
    }

    // Гасим обе: свою и доставшуюся от старой версии — иначе выключенный автозапуск оживёт.
    // Успех — если убрали хоть одну: у мигрирующей установки своей задачи ещё нет.
    private static bool Disable()
    {
        bool legacy = LegacyIsOurs() && Run("/delete", "/tn", LegacyTaskName, "/f") == 0;
        bool own = Run("/delete", "/tn", TaskName, "/f") == 0;
        return own || legacy;
    }

    /// <summary>stdout команды при успехе, иначе null.</summary>
    private static string? RunRead(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = SchTasks,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return null;
            string stdout = p.StandardOutput.ReadToEnd();
            _ = p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            return p.HasExited && p.ExitCode == 0 ? stdout : null;
        }
        catch (Exception ex) { Log.Ex("AutoStart.RunRead", ex); return null; }
    }

    private static int Run(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = SchTasks,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return -1;
            _ = p.StandardOutput.ReadToEnd();
            _ = p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch (Exception ex) { Log.Ex("AutoStart.Run", ex); return -1; }
    }

    private static string BuildXml()
    {
        string exe = Environment.ProcessPath!; // у обычного exe-процесса путь есть всегда
        string user = WindowsIdentity.GetCurrent().Name; // DOMAIN\User
        string exeX = SecurityElement.Escape(exe)!;
        string userX = SecurityElement.Escape(user)!;

        return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>Xi Control — автозапуск при входе</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{userX}</UserId>
      <Delay>PT5S</Delay>
    </LogonTrigger>
    <!-- возврат в сеанс при быстром переключении пользователей — это не logon, без этого
         триггера приложение не поднимается. SessionUnlock НЕ добавлять: он срабатывает на
         каждую разблокировку экрана -->
    <SessionStateChangeTrigger>
      <Enabled>true</Enabled>
      <UserId>{userX}</UserId>
      <StateChange>ConsoleConnect</StateChange>
      <Delay>PT5S</Delay>
    </SessionStateChangeTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{userX}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>false</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{exeX}</Command>
      <Arguments>--autostart</Arguments>
    </Exec>
  </Actions>
</Task>";
    }
}
