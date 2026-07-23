using System.Runtime.InteropServices;
using System.Text;

namespace XiControl.SystemIntegration;

/// <summary>
/// Общая механика «выключить/включить HID-устройство на шине I2C» штатными user-mode
/// SetupAPI/CfgMgr32, без драйверов (нужны права администратора — они у нас есть).
/// Прошивочный путь (WMI 0x0C, TPLock) на TM2424 к железу не подключён, поэтому только Win32.
///
/// Устройство ищется по своей HID-коллекции (Top-Level Collection, <see cref="CompatId"/>;
/// признак бывает и в hardware, и в compatible ids). Обычно гасим её РОДИТЕЛЯ — узел «HID
/// на шине I2C»: у устройства коллекций может быть несколько (у тачпада — мышь + панель),
/// и погашение одной оставило бы ввод живым через другую, а у родителя гаснет всё разом.
/// НО если родитель коллекции — сам PCI-контроллер шины (на Meteor Lake HID-коллекция
/// тачскрина подцеплена прямо к Intel Serial IO I2C / Touch Host Controller), гасить его
/// нельзя — это утащит контроллер целиком, а не «сенсор»; тогда целимся в саму коллекцию
/// (см. <see cref="IsBusOrController"/>). ID выбранного узла запоминается в конфиге
/// (<see cref="DeviceId"/>): у выключенного устройства HID-коллекции исчезают из системы,
/// и после перезапуска приложения искать больше нечем.
///
/// Отключение двухступенчатое. Сначала «мягкое» CM_Disable_DevNode без PERSIST (после
/// перезагрузки устройство вернётся само) — но это query-remove, который другой софт с
/// открытым хэндлом может заветировать (на части моделей так и происходит). Тогда фолбэк —
/// путь Диспетчера устройств: SetupAPI DIF_PROPERTYCHANGE/DICS_DISABLE (персистентный, вето
/// не подвержен) + флаг <see cref="PersistOff"/> в конфиге, по которому приложение на старте
/// само включает устройство обратно — обещание «не залипает выключенным» сохраняется.
/// Каждый шаг логируется под именем <see cref="LogName"/> — по log.txt с чужой машины видно,
/// где именно не срослось.
/// </summary>
public abstract class HidNodeToggle
{
    /// <summary>HID Top-Level Collection устройства (compatible-id, напр. тачпад = HID_DEVICE_UP:000D_U:0005).</summary>
    protected abstract string CompatId { get; }

    /// <summary>Имя для лога («Touchpad» / «Touchscreen»).</summary>
    protected abstract string LogName { get; }

    /// <summary>ID узла-родителя из конфига — запоминается автоматически при первом обнаружении.</summary>
    protected abstract string? DeviceId { get; set; }

    /// <summary>Флаг «отключено персистентным путём» из конфига — по нему включаем на старте.</summary>
    protected abstract bool PersistOff { get; set; }

    /// <summary>Сохранить конфиг после правки <see cref="DeviceId"/>/<see cref="PersistOff"/>.</summary>
    protected abstract void SaveConfig();

    private const uint ProbDisabled = 22;    // CM_PROB_DISABLED
    private const int CrNoSuchDevnode = 0x0D; // CR_NO_SUCH_DEVNODE
    private const uint LocatePhantom = 0x1;   // CM_LOCATE_DEVNODE_PHANTOM
    private const uint DisableUiNotOk = 0x4;  // CM_DISABLE_UI_NOT_OK

    private uint? _devInst;      // кэш узла-родителя (валиден в сессии, в т.ч. фантомный)
    private bool _loggedMissing; // «не нашли» пишем в лог один раз, не спамим

    /// <summary>Устройство найдено (сейчас или по запомненному ID)?</summary>
    public bool Available => Find() is not null;

    /// <summary>true/false — состояние узла; null — устройство не найдено.
    /// Узел, удалённый мягким отключением, считается выключенным.</summary>
    public bool? IsEnabled()
    {
        if (Find() is not uint inst) return null;
        int cr = CM_Get_DevNode_Status(out _, out uint problem, inst, 0);
        if (cr == CrNoSuchDevnode) return false; // узел убран query-remove'ом — выключено
        if (cr != 0) return null;
        return problem != ProbDisabled;
    }

    /// <summary>Переключить. Возвращает новое состояние, null — не нашли/не вышло.</summary>
    public bool? Toggle()
    {
        if (IsEnabled() is not bool on) return null;
        bool ok = on ? Disable() : Enable();
        return ok ? !on : null;
    }

    /// <summary>
    /// Страховка на старте приложения: если в прошлый раз пришлось отключать персистентно,
    /// после перезагрузки включаем устройство сами (мягкое отключение возвращается без нас).
    /// </summary>
    public void RestoreAfterBoot()
    {
        if (!PersistOff) return;
        Log.Write($"{LogName}: включаю после перезагрузки (осталось персистентное отключение)");
        Enable();
    }

    // ---- Отключение: мягкое (не-persist), затем путь Диспетчера устройств ----

    private bool Disable()
    {
        if (Find() is not uint inst) return false;

        // страховка: никогда не гасим шину/хост-контроллер (напр. PCI Touch Host Controller) —
        // это валит контроллер целиком. Обычно сюда не попадаем (FindViaHid уже целится в
        // безопасный узел), но DeviceId из конфига старой версии мог указывать на контроллер.
        if (DeviceIdOf(inst) is string tid && IsBusOrController(tid))
        {
            Log.Write($"{LogName}: ОТКАЗ отключать — целевой узел это шина/контроллер ({tid})");
            return false;
        }

        int cr = CM_Disable_DevNode(inst, DisableUiNotOk);
        if (cr != 0) Log.Write($"{LogName}: CM_Disable_DevNode → CR 0x{cr:X}");
        if (WaitState(enabled: false)) return true;

        // мягкое отключение заветировано/не сработало — отключаем как Диспетчер устройств
        // (персистентно; на старте приложения RestoreAfterBoot вернёт устройство). Тот же
        // запрет на шину/контроллер, что и выше, — этот путь переживает перезагрузку, погасить
        // им контроллер было бы вдвойне опасно (без валидного/безопасного ID не лезем).
        if (DeviceId is not string id || IsBusOrController(id)) return false;
        Log.Write($"{LogName}: мягкое отключение не сработало — пробую персистентное (SetupAPI)");
        if (!SetupDiChangeState(id, DICS_DISABLE)) return false;
        if (!WaitState(enabled: false)) return false;
        PersistOff = true;
        SaveConfig();
        return true;
    }

    // ---- Включение: CM_Enable → SetupAPI → полный rescan PnP, по нарастающей ----

    private bool Enable()
    {
        bool ok = false;
        if (Find() is uint inst)
        {
            int cr = CM_Enable_DevNode(inst, 0);
            if (cr != 0) Log.Write($"{LogName}: CM_Enable_DevNode → CR 0x{cr:X}");
            ok = WaitState(enabled: true);
        }
        if (!ok && DeviceId is string id)
        {
            Log.Write($"{LogName}: CM_Enable не помог — пробую SetupAPI (DICS_ENABLE)");
            SetupDiChangeState(id, DICS_ENABLE);
            ok = WaitState(enabled: true);
        }
        if (!ok)
        {
            // узел убран query-remove'ом — вернёт только пересканирование шины
            Log.Write($"{LogName}: включаю пересканированием устройств (как «Обновить конфигурацию»)");
            if (CM_Locate_DevNode(out uint root, null, 0) == 0)
            {
                int cr2 = CM_Reenumerate_DevNode(root, 0);
                if (cr2 != 0) Log.Write($"{LogName}: CM_Reenumerate_DevNode → CR 0x{cr2:X}");
            }
            ok = WaitState(enabled: true);
        }
        if (ok && PersistOff) { PersistOff = false; SaveConfig(); }
        return ok;
    }

    // PnP-операции асинхронны — даём устройству до ~1.5 с прийти в целевое состояние.
    // Для «выключен» удалённый узел (IsEnabled == null после потери кэша) тоже успех.
    private bool WaitState(bool enabled)
    {
        for (int i = 0; ; i++)
        {
            var st = IsEnabled();
            if (enabled ? st == true : st != true) return true;
            if (i >= 6) return false;
            Thread.Sleep(250);
        }
    }

    // ---- Поиск узла-родителя ----

    // Через живую HID-коллекцию; иначе — по запомненному ID (обычный или фантомный узел).
    private uint? Find()
    {
        if (_devInst is uint cached) return cached;
        try
        {
            if (FindViaHid() is uint inst) { _devInst = inst; return inst; }
            // кэшированный ID из старой версии мог указывать на PCI-контроллер — игнорируем
            // его (иначе Toggle стал бы гасить шину); FindViaHid перезапишет ID, когда
            // устройство снова включат и его коллекция появится в системе.
            if (!string.IsNullOrEmpty(DeviceId) && !IsBusOrController(DeviceId))
            {
                if (CM_Locate_DevNode(out uint located, DeviceId, 0) == 0 ||
                    CM_Locate_DevNode(out located, DeviceId, LocatePhantom) == 0)
                {
                    _devInst = located;
                    return located;
                }
            }
            if (!_loggedMissing)
            {
                _loggedMissing = true;
                Log.Write($"{LogName}: HID-коллекция ({CompatId}) не найдена — " +
                          "устройства нет или у него другой стек драйвера (не Precision/цифровой)");
            }
        }
        catch (Exception ex) { Log.Ex($"{GetType().Name}.Find", ex); }
        return null;
    }

    private uint? FindViaHid()
    {
        const uint DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4;
        const uint SPDRP_HARDWAREID = 0x1, SPDRP_COMPATIBLEIDS = 0x2;

        IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, "HID", IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == new IntPtr(-1)) return null;
        try
        {
            var info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            var buf = new byte[4096];
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
            {
                // признак TLC встречается и в hardware ids (иные ODM кладут его только туда,
                // compatible ids пусты), и в compatible — смотрим оба
                bool match = false;
                foreach (uint prop in (uint[])[SPDRP_HARDWAREID, SPDRP_COMPATIBLEIDS])
                {
                    if (!SetupDiGetDeviceRegistryProperty(set, ref info, prop,
                            out _, buf, (uint)buf.Length, out uint size)) continue;
                    if (Encoding.Unicode.GetString(buf, 0, (int)size)
                        .Contains(CompatId, StringComparison.OrdinalIgnoreCase)) { match = true; break; }
                }
                if (!match) continue;

                if (CM_Get_Parent(out uint parent, info.DevInst, 0) != 0) continue;

                // по умолчанию гасим РОДИТЕЛЯ коллекции (у тачпада коллекций две — мышь +
                // панель — и гасить надо их общий узел). Но если родитель — сам PCI-контроллер
                // шины (Meteor Lake: коллекция тачскрина висит прямо под Serial IO I2C / THC),
                // гасить его нельзя — утащит контроллер целиком; целимся в саму коллекцию.
                uint target = parent;
                if (DeviceIdOf(parent) is string pid && IsBusOrController(pid))
                {
                    Log.Write($"{LogName}: родитель коллекции — шина/контроллер ({pid}), гашу саму коллекцию");
                    target = info.DevInst;
                }

                // запомнить ID выбранного узла — иначе после перезапуска выключенное устройство не найти
                if (DeviceIdOf(target) is string id &&
                    !string.Equals(DeviceId, id, StringComparison.OrdinalIgnoreCase))
                {
                    DeviceId = id;
                    SaveConfig();
                    Log.Write($"{LogName}: найден узел {id}");
                }
                return target;
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return null;
    }

    // Прочитать instance-id узла в строку; null — если CfgMgr вернул ошибку.
    private static string? DeviceIdOf(uint devInst)
    {
        var buf = new char[256]; // char-буфер без лишнего маршалинга (CA1838), как в FindViaHid
        if (CM_Get_Device_ID(devInst, buf, buf.Length, 0) != 0) return null;
        int len = Array.IndexOf(buf, '\0') is int z && z >= 0 ? z : buf.Length;
        return new string(buf, 0, len);
    }

    // Узел шины/хост-контроллера — гасить его нельзя: утащит весь контроллер, а не «сенсор».
    // Наблюдалось на Meteor Lake (TM2424): HID-коллекция тачскрина подцеплена прямо к PCI —
    // Intel Serial IO I2C / Touch Host Controller (напр. PCI\VEN_8086&DEV_E448), и слепое
    // отключение родителя валило контроллер целиком.
    internal static bool IsBusOrController(string instanceId) =>
        instanceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase);

    // ---- Путь Диспетчера устройств: DIF_PROPERTYCHANGE / DICS_* (персистентный) ----

    private const uint DICS_ENABLE = 1, DICS_DISABLE = 2, DICS_FLAG_GLOBAL = 1;
    private const uint DIF_PROPERTYCHANGE = 0x12;

    private bool SetupDiChangeState(string instanceId, uint stateChange)
    {
        IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == new IntPtr(-1)) return false;
        try
        {
            var info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiOpenDeviceInfo(set, instanceId, IntPtr.Zero, 0, ref info))
            { Log.Write($"{LogName}: SetupDiOpenDeviceInfo({instanceId}) не удалась"); return false; }

            var pcp = new SP_PROPCHANGE_PARAMS
            {
                ClassInstallHeader = new SP_CLASSINSTALL_HEADER
                {
                    cbSize = (uint)Marshal.SizeOf<SP_CLASSINSTALL_HEADER>(),
                    InstallFunction = DIF_PROPERTYCHANGE,
                },
                StateChange = stateChange,
                Scope = DICS_FLAG_GLOBAL,
                HwProfile = 0,
            };
            if (!SetupDiSetClassInstallParams(set, ref info, ref pcp, (uint)Marshal.SizeOf<SP_PROPCHANGE_PARAMS>()) ||
                !SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, set, ref info))
            {
                Log.Write($"{LogName}: DIF_PROPERTYCHANGE({stateChange}) → Win32 0x{Marshal.GetLastWin32Error():X}");
                return false;
            }
            return true;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    // ---- P/Invoke ----

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_CLASSINSTALL_HEADER { public uint cbSize; public uint InstallFunction; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_PROPCHANGE_PARAMS
    {
        public SP_CLASSINSTALL_HEADER ClassInstallHeader;
        public uint StateChange;
        public uint Scope;
        public uint HwProfile;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        uint property, out uint propertyRegDataType, byte[] propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll")]
    private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiOpenDeviceInfo(IntPtr deviceInfoSet, string deviceInstanceId,
        IntPtr hwndParent, uint openFlags, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiSetClassInstallParams(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        ref SP_PROPCHANGE_PARAMS classInstallParams, uint classInstallParamsSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(uint installFunction, IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parentDevInst, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID(uint devInst, [Out] char[] buffer, int bufferLen, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNode(out uint devInst, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Disable_DevNode(uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Enable_DevNode(uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Reenumerate_DevNode(uint devInst, uint flags);
}
