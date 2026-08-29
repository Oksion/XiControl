using System.Management;

namespace XiControl.Wmi;

/// <summary>
/// Результат вызова MiInterface. Разбор зависит от диалекта прошивки (XIC-43): «успех» и место
/// значения у моделей разные, поэтому сырые байты интерпретирует <see cref="MifsReply"/>.
/// </summary>
public sealed class MifsResult
{
    public required byte[] Out { get; init; }
    public required MifsDialect Dialect { get; init; }
    public required byte Cmd { get; init; }   // нужна эхо-диалекту: там ответ — повтор команды

    public bool Ok => MifsReply.Ok(Dialect, Out, Cmd);

    /// <param name="classicOffset">Смещение значения в классическом диалекте:
    /// <see cref="MifsReply.PerfOffset"/> или <see cref="MifsReply.ChargeOffset"/>.</param>
    public byte Value(int classicOffset) => MifsReply.Value(Dialect, Out, classicOffset);
}

/// <summary>
/// Тонкая обёртка над WMI-методом MiCommonInterface.MiInterface.
/// Требует прав администратора (проверено). Бросает при отсутствии интерфейса.
/// </summary>
public sealed class MifsClient : IMifsClient
{
    private readonly ManagementObject _inst;
    private readonly object _lock = new();   // сериализуем вызовы (UI + события питания)
    private MifsDialect _dialect = MifsDialect.Unknown;   // определяется один раз, см. Dialect()

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
    public MifsResult Invoke(byte op, byte cmd, byte arg = 0, byte val = 0) =>
        new() { Out = Raw(op, cmd, arg, val), Dialect = Dialect(), Cmd = cmd };

    /// <summary>Транспорт без интерпретации — им же снимается проба для определения диалекта,
    /// когда интерпретировать ещё нечем.</summary>
    private byte[] Raw(byte op, byte cmd, byte arg, byte val)
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
            return outParams?["OutData"] as byte[] ?? [];
        }
    }

    /// <summary>
    /// Диалект ответа этой прошивки. Определяется одним GET при первом обращении и кэшируется:
    /// железо в пределах сеанса не меняется. Под тем же замком, что и вызовы, — Monitor
    /// реентрантен, а два потока не должны снимать пробу одновременно.
    /// </summary>
    private MifsDialect Dialect()
    {
        if (_dialect != MifsDialect.Unknown) return _dialect;
        lock (_lock)
        {
            if (_dialect != MifsDialect.Unknown) return _dialect;
            var probe = Raw(Mifs.OpGet, Mifs.CmdPerf, 0, 0);
            _dialect = MifsReply.Detect(probe, Mifs.CmdPerf);
            Log.Write($"MIFS: диалект ответа — {_dialect} (GET 0x{Mifs.CmdPerf:X2} → {Dump(probe)})");
            if (_dialect == MifsDialect.Unsupported)
                Log.Write("MIFS: раскладка ответа не опознана — функции прошивки выключены");
            return _dialect;
        }
    }

    private static string Dump(byte[] b) =>
        string.Join(' ', b.Take(8).Select(x => x.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));

    public MifsResult Get(byte cmd, byte arg = 0) => Invoke(Mifs.OpGet, cmd, arg);
    public MifsResult Set(byte cmd, byte arg = 0, byte val = 0) => Invoke(Mifs.OpSet, cmd, arg, val);

    // ---- Режим производительности ----

    public PerfMode? GetPerfMode()
    {
        var r = Get(Mifs.CmdPerf);
        return r.Ok ? (PerfMode)r.Value(MifsReply.PerfOffset) : null;
    }

    /// <returns>true, если прошивка приняла режим.</returns>
    public bool SetPerfMode(PerfMode mode)
    {
        if (!Set(Mifs.CmdPerf, (byte)mode).Ok) return false;
        return GetPerfMode() == mode;
    }

    // ---- Порог заряда (уровни 40/50/60/70/80 / полный 100%) ----

    /// <summary>Текущий порог заряда, %; null — прошивка не ответила, код неизвестен или
    /// её диалект не несёт данных этой группы (тогда честнее прочерк, чем выдумка).</summary>
    public int? GetChargeLimit()
    {
        if (!MifsReply.CarriesChargeData(Dialect())) return null;
        var r = Get(Mifs.CmdCharge, Mifs.ChargeSubEnable);
        return r.Ok ? Mifs.ChargePercentForCode(r.Value(MifsReply.ChargeOffset)) : null;
    }

    /// <summary>
    /// Ставит порог заряда процентом. Пишет код по таблице <see cref="Mifs.ChargeCodeForPercent"/>
    /// с ре-армом off→on (сброс стейт-машины EC, как в референсе). Неподдержанный % — не пишем
    /// вслепую (false); прошивка сама валидирует набор — отвергла код → false (сигнал для фолбэка).
    /// </summary>
    public bool SetChargeLimit(int percent)
    {
        // Диалект без данных этой группы — писать некуда: на TM2113 измерено, что запись кодов
        // не меняет ответ вовсе. Честное false (интерфейс покажет «не сработало») лучше, чем
        // тихий «успех» по эху и лимит, которого на самом деле нет.
        if (!MifsReply.CarriesChargeData(Dialect())) return false;

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
        if (!MifsReply.CarriesChargeData(Dialect())) return 0;
        var r = Get(Mifs.CmdCharge, Mifs.SensorAdapterWatts);
        return r.Ok ? r.Value(MifsReply.ChargeOffset) : 0;
    }

    /// <summary>Здоровье батареи (SOH1), % от исходной ёмкости; null — не прочиталось.</summary>
    public int? GetBatteryHealth()
    {
        if (!MifsReply.CarriesChargeData(Dialect())) return null;
        var r = Get(Mifs.CmdCharge, Mifs.SensorBatteryHealth);
        return r.Ok ? r.Value(MifsReply.ChargeOffset) : null;
    }

    public void Dispose() => _inst.Dispose();
}
