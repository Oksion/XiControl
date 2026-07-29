using System.Management;

namespace XiControl.Wmi;

/// <summary>Результат вызова MiInterface.</summary>
public sealed class MifsResult
{
    public required bool Ok { get; init; }        // OUT[1] == 0x80
    public required byte[] Out { get; init; }
    public byte Val4 => Out.Length > 4 ? Out[4] : (byte)0;  // значение/эхо (perf mode здесь)
    public byte Val6 => Out.Length > 6 ? Out[6] : (byte)0;  // значение (charge статус здесь)
}

/// <summary>
/// Тонкая обёртка над WMI-методом MiCommonInterface.MiInterface.
/// Требует прав администратора (проверено). Бросает при отсутствии интерфейса.
/// </summary>
public sealed class MifsClient : IMifsClient
{
    private readonly ManagementObject _inst;
    private readonly object _lock = new();   // сериализуем вызовы (UI + события питания)

    public MifsClient()
    {
        var scope = new ManagementScope(Mifs.Namespace);
        scope.Connect();
        using var searcher = new ManagementObjectSearcher(
            scope, new ObjectQuery($"SELECT * FROM {Mifs.ClassName}"));
        _inst = searcher.Get().Cast<ManagementObject>().FirstOrDefault()
            ?? throw new InvalidOperationException($"{Mifs.ClassName} не найден.");
    }

    /// <summary>Сырой вызов. op=OpGet/OpSet, cmd/arg/val раскладываются по offset 3/4/6.</summary>
    public MifsResult Invoke(byte op, byte cmd, byte arg = 0, byte val = 0)
    {
        var inData = new byte[Mifs.BufferSize];
        inData[1] = op;
        inData[3] = cmd;
        inData[4] = arg;
        inData[6] = val;

        lock (_lock)
        {
            using var pars = _inst.GetMethodParameters(Mifs.MethodName);
            pars["InData"] = inData;
            using var outParams = _inst.InvokeMethod(Mifs.MethodName, pars, null);
            var outData = outParams?["OutData"] as byte[] ?? Array.Empty<byte>();
            return new MifsResult { Ok = outData.Length > 1 && outData[1] == Mifs.StatusOk, Out = outData };
        }
    }

    public MifsResult Get(byte cmd, byte arg = 0) => Invoke(Mifs.OpGet, cmd, arg);
    public MifsResult Set(byte cmd, byte arg = 0, byte val = 0) => Invoke(Mifs.OpSet, cmd, arg, val);

    // ---- Режим производительности ----

    public PerfMode? GetPerfMode()
    {
        var r = Get(Mifs.CmdPerf);
        return r.Ok ? (PerfMode)r.Val4 : null;
    }

    /// <returns>true, если прошивка приняла режим.</returns>
    public bool SetPerfMode(PerfMode mode)
    {
        if (!Set(Mifs.CmdPerf, (byte)mode).Ok) return false;
        return GetPerfMode() == mode;
    }

    // ---- Порог заряда (уровни 40/50/60/70/80 / полный 100%) ----

    /// <summary>Текущий порог заряда, %; null — прошивка не ответила или код неизвестен.</summary>
    public int? GetChargeLimit()
    {
        var r = Get(Mifs.CmdCharge, Mifs.ChargeSubEnable);
        return r.Ok ? Mifs.ChargePercentForCode(r.Val6) : null;
    }

    /// <summary>
    /// Ставит порог заряда процентом. Пишет код по таблице <see cref="Mifs.ChargeCodeForPercent"/>
    /// с ре-армом off→on (сброс стейт-машины EC, как в референсе). Неподдержанный % — не пишем
    /// вслепую (false); прошивка сама валидирует набор — отвергла код → false (сигнал для фолбэка).
    /// </summary>
    public bool SetChargeLimit(int percent)
    {
        var code = Mifs.ChargeCodeForPercent(percent);
        if (code is null) return false;
        var off = Set(Mifs.CmdCharge, Mifs.ChargeSubEnable, 0);   // «выкл» = 100%
        if (code.Value == 0) return off.Ok;             // сам 100% — второй записи не нужно
        Thread.Sleep(80);
        return Set(Mifs.CmdCharge, Mifs.ChargeSubEnable, code.Value).Ok;
    }

    // ---- Сенсоры (та же группа команд 0x10, driver-free) ----

    /// <summary>Мощность подключённого адаптера в ваттах; 0 — не подключён или не-PD
    /// (обычный USB мощность не сообщает). Значение — согласованная PD-мощность БП.</summary>
    public int GetAdapterWatts()
    {
        var r = Get(Mifs.CmdCharge, Mifs.SensorAdapterWatts);
        return r.Ok ? r.Val6 : 0;
    }

    /// <summary>Здоровье батареи (SOH1), % от исходной ёмкости; null — не прочиталось.</summary>
    public int? GetBatteryHealth()
    {
        var r = Get(Mifs.CmdCharge, Mifs.SensorBatteryHealth);
        return r.Ok ? r.Val6 : null;
    }

    public void Dispose() => _inst.Dispose();
}
