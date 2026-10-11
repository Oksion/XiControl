using System.ComponentModel;
using System.Runtime.InteropServices;

namespace XiControl.SystemIntegration;

/// <summary>Что видно о службе Xiaomi MiDeviceService.</summary>
public enum OemServiceState { NotInstalled, Running, Stopped, Disabled, NoAccess }

/// <summary>Тип запуска службы (то, что запоминаем перед отключением и возвращаем потом).</summary>
public enum OemStartType { Unknown, Auto, DelayedAuto, Manual, Disabled }

/// <summary>Чем кончилось нажатие «Отключить» / «Включить».</summary>
public enum OemServiceResult { Ok, AccessDenied, NotInstalled, Failed }

public sealed record OemServiceInfo(OemServiceState State, OemStartType StartType)
{
    public static readonly OemServiceInfo Missing = new(OemServiceState.NotInstalled, OemStartType.Unknown);
}

/// <summary>Что показать на вкладке «Драйверы»: состояние; что сейчас делается (null — ничего,
/// false — отключаем, true — включаем); ошибка последнего нажатия.</summary>
public sealed record OemServiceView(OemServiceInfo Info, bool? Pending, OemServiceResult? Error);

/// <summary>
/// Служба Xiaomi MiDeviceService (XIC-87, docs/17): XiControl она не нужна, а на выходе из Eco
/// откатывает яркость и сама меняет режим питания Windows. Здесь — чтение состояния и
/// отключение/включение по кнопке; на старте и в фоне ничего не делаем.
/// Через SCM напрямую, а не sc.exe/WMI: чтение мгновенное (вкладка строится в UI-потоке),
/// и коды ошибок честные — ERROR_ACCESS_DENIED отличаем от остального. Deny-ACE службы стоит
/// только на SYSTEM, администратор (а мы — он) останавливает и перенастраивает её штатно.
/// </summary>
public static class OemService
{
    public const string Name = "MiDeviceService";

    /// <summary>Состояние и тип запуска. Быстро (одно обращение к SCM), можно из UI-потока.</summary>
    public static OemServiceInfo Read()
    {
        try
        {
            using var scm = OpenScm();
            using var svc = OpenService(scm, SERVICE_QUERY_STATUS | SERVICE_QUERY_CONFIG);
            if (svc.IsInvalid) return Marshal.GetLastWin32Error() switch
            {
                ERROR_SERVICE_DOES_NOT_EXIST => OemServiceInfo.Missing,
                ERROR_ACCESS_DENIED => new(OemServiceState.NoAccess, OemStartType.Unknown),
                var e => throw new Win32Exception(e),
            };
            if (!QueryServiceStatus(svc, out var st)) throw new Win32Exception();
            var start = QueryStartType(svc);
            return new(StateOf(st.CurrentState, start), start);
        }
        catch (Exception ex)
        {
            Log.Ex("OemService.Read", ex);
            return new(OemServiceState.NoAccess, OemStartType.Unknown);
        }
    }

    /// <summary>Тип запуска Disabled, затем остановка (ждём до ~15 с). Звать из фонового потока.</summary>
    public static OemServiceResult Disable() => Run("Disable", svc =>
    {
        // сначала тип запуска: если остановка и не удастся, после перезагрузки служба не поднимется
        if (!ChangeServiceConfig(svc, SERVICE_NO_CHANGE, SERVICE_DISABLED, SERVICE_NO_CHANGE,
                null, null, IntPtr.Zero, null, null, null, null)) return false;
        if (!QueryServiceStatus(svc, out var st)) return false;
        if (st.CurrentState != SERVICE_STOPPED
            && !ControlService(svc, SERVICE_CONTROL_STOP, out _)
            && Marshal.GetLastWin32Error() != ERROR_SERVICE_NOT_ACTIVE) return false;
        return WaitFor(svc, SERVICE_STOPPED);
    });

    /// <summary>Вернуть тип запуска <paramref name="start"/> и запустить. Звать из фонового потока.</summary>
    public static OemServiceResult Enable(OemStartType start) => Run("Enable " + start, svc =>
    {
        uint type = start == OemStartType.Manual ? SERVICE_DEMAND_START : SERVICE_AUTO_START;
        if (!ChangeServiceConfig(svc, SERVICE_NO_CHANGE, type, SERVICE_NO_CHANGE,
                null, null, IntPtr.Zero, null, null, null, null)) return false;
        if (type == SERVICE_AUTO_START)
        {
            // «отложенный» — отдельный флаг поверх Auto; выставляем явно в обе стороны
            var info = new SERVICE_DELAYED_AUTO_START_INFO { fDelayedAutostart = start == OemStartType.DelayedAuto };
            if (!ChangeServiceConfig2(svc, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, ref info)) return false;
        }
        if (!StartService(svc, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != ERROR_SERVICE_ALREADY_RUNNING) return false;
        return WaitFor(svc, SERVICE_RUNNING);
    });

    /// <summary>
    /// Какой тип запуска вернуть по «Включить»: запомненный перед отключением, а если его нет
    /// (отключали руками, sc.exe, другой программой) — Auto, как у предустановки Xiaomi.
    /// Disabled и мусор не возвращаем никогда — иначе «Включить» ничего бы не включало.
    /// </summary>
    internal static OemStartType RestoreTarget(string? saved) =>
        Enum.TryParse<OemStartType>(saved, ignoreCase: true, out var t)
        && t is OemStartType.Auto or OemStartType.DelayedAuto or OemStartType.Manual
            ? t : OemStartType.Auto;

    /// <summary>Запоминать ли тип запуска перед отключением: Disabled/неизвестный не пишем
    /// поверх уже запомненного — повторное «Отключить» не должно стереть исходный.</summary>
    internal static bool ShouldRemember(OemStartType current) =>
        current is OemStartType.Auto or OemStartType.DelayedAuto or OemStartType.Manual;

    /// <summary>Состояние для показа: работающая — «запущена» при любом типе запуска (отключение
    /// могло не остановить её), стоящая с Disabled — «отключена».</summary>
    internal static OemServiceState StateOf(uint scmState, OemStartType start) => scmState switch
    {
        SERVICE_STOPPED => start == OemStartType.Disabled ? OemServiceState.Disabled : OemServiceState.Stopped,
        _ => OemServiceState.Running, // START/STOP_PENDING, RUNNING, PAUSED — процесс жив
    };

    private static OemServiceResult Run(string what, Func<SafeServiceHandle, bool> op)
    {
        try
        {
            using var scm = OpenScm();
            using var svc = OpenService(scm, SERVICE_QUERY_STATUS | SERVICE_QUERY_CONFIG | SERVICE_CHANGE_CONFIG | SERVICE_STOP | SERVICE_START);
            if (svc.IsInvalid) return Fail(what, Marshal.GetLastWin32Error());
            if (op(svc)) { Log.Write($"OemService: {what} — ок"); return OemServiceResult.Ok; }
            return Fail(what, Marshal.GetLastWin32Error());
        }
        catch (Exception ex)
        {
            Log.Ex("OemService." + what, ex);
            return OemServiceResult.Failed;
        }
    }

    private static OemServiceResult Fail(string what, int err)
    {
        Log.Write($"OemService: {what} — ошибка {err} ({new Win32Exception(err).Message})");
        return err switch
        {
            ERROR_ACCESS_DENIED => OemServiceResult.AccessDenied,
            ERROR_SERVICE_DOES_NOT_EXIST => OemServiceResult.NotInstalled,
            _ => OemServiceResult.Failed,
        };
    }

    private static bool WaitFor(SafeServiceHandle svc, uint state)
    {
        for (int i = 0; i < 60; i++)
        {
            if (!QueryServiceStatus(svc, out var st)) return false;
            if (st.CurrentState == state) return true;
            Thread.Sleep(250);
        }
        Marshal.SetLastPInvokeError(ERROR_SERVICE_REQUEST_TIMEOUT);
        return false;
    }

    private static OemStartType QueryStartType(SafeServiceHandle svc)
    {
        _ = QueryServiceConfig(svc, IntPtr.Zero, 0, out int need);
        if (need <= 0) return OemStartType.Unknown;
        IntPtr buf = Marshal.AllocHGlobal(need);
        try
        {
            if (!QueryServiceConfig(svc, buf, need, out _)) return OemStartType.Unknown;
            uint type = (uint)Marshal.ReadInt32(buf, 4); // QUERY_SERVICE_CONFIG: dwServiceType, dwStartType…
            return type switch
            {
                SERVICE_AUTO_START => IsDelayed(svc) ? OemStartType.DelayedAuto : OemStartType.Auto,
                SERVICE_DEMAND_START => OemStartType.Manual,
                SERVICE_DISABLED => OemStartType.Disabled,
                _ => OemStartType.Unknown,
            };
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static bool IsDelayed(SafeServiceHandle svc)
    {
        var info = new SERVICE_DELAYED_AUTO_START_INFO();
        return QueryServiceConfig2(svc, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, ref info, Marshal.SizeOf<SERVICE_DELAYED_AUTO_START_INFO>(), out _)
            && info.fDelayedAutostart;
    }

    private static SafeServiceHandle OpenScm()
    {
        var h = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        return h.IsInvalid ? throw new Win32Exception() : h;
    }

    private static SafeServiceHandle OpenService(SafeServiceHandle scm, uint access) => OpenServiceW(scm, Name, access);

    // ---- Win32 ----

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_QUERY_CONFIG = 0x0001, SERVICE_CHANGE_CONFIG = 0x0002, SERVICE_QUERY_STATUS = 0x0004,
                       SERVICE_START = 0x0010, SERVICE_STOP = 0x0020;
    private const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;
    private const uint SERVICE_AUTO_START = 2, SERVICE_DEMAND_START = 3, SERVICE_DISABLED = 4;
    private const uint SERVICE_STOPPED = 1, SERVICE_RUNNING = 4;
    private const uint SERVICE_CONTROL_STOP = 1;
    private const uint SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;
    private const int ERROR_ACCESS_DENIED = 5, ERROR_SERVICE_ALREADY_RUNNING = 1056, ERROR_SERVICE_REQUEST_TIMEOUT = 1053,
                      ERROR_SERVICE_DOES_NOT_EXIST = 1060, ERROR_SERVICE_NOT_ACTIVE = 1062;

    private sealed class SafeServiceHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_DELAYED_AUTO_START_INFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fDelayedAutostart;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenServiceW(SafeServiceHandle scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr h);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(SafeServiceHandle svc, out SERVICE_STATUS status);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryServiceConfig(SafeServiceHandle svc, IntPtr buf, int size, out int needed);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryServiceConfig2W")]
    private static extern bool QueryServiceConfig2(SafeServiceHandle svc, uint level, ref SERVICE_DELAYED_AUTO_START_INFO buf, int size, out int needed);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ChangeServiceConfigW")]
    private static extern bool ChangeServiceConfig(SafeServiceHandle svc, uint type, uint start, uint error, string? path,
        string? group, IntPtr tag, string? deps, string? user, string? password, string? display);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ChangeServiceConfig2W")]
    private static extern bool ChangeServiceConfig2(SafeServiceHandle svc, uint level, ref SERVICE_DELAYED_AUTO_START_INFO info);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(SafeServiceHandle svc, uint control, out SERVICE_STATUS status);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "StartServiceW")]
    private static extern bool StartService(SafeServiceHandle svc, int argc, IntPtr argv);
}
