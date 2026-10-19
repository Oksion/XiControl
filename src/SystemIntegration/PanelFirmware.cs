using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using static XiControl.SystemIntegration.PanelFirmwareProtocol;

namespace XiControl.SystemIntegration;

/// <summary>
/// Протокол чтения версии прошивки TCON-контроллера панели Himax HX8874 (XIC-103/104): чистая
/// логика, без IGCL. Восстановлен из <c>IGCLCSharpDLL.dll</c> (класс <c>IGCLAUXFunc</c>), которой
/// пользуется <c>FirmwareScreenTool.exe</c> службы Xiaomi, — последовательность повторена байт в
/// байт, ничего сверх. Разбор — docs/16-oem-driver-check.md, «Текущие версии прошивок».
/// </summary>
public static class PanelFirmwareProtocol
{
    /// <summary>Одна операция <c>ctlAUXAccess</c>: запись или чтение, канал, адрес, данные.</summary>
    public sealed record AuxOp(int Op, int Flags, uint Address, byte[] Data, int Size);

    public const int OpRead = 1, OpWrite = 2;
    public const int FlagNative = 1, FlagI2c = 2, FlagI2cMot = 4; // native AUX, I2C-over-AUX, I2C с MOT
    public const uint DpcdSinkOui = 0x400;     // DPCD: OUI приёмника, 3 байта
    public const uint HimaxI2c = 0xF2;         // I2C-адрес контроллера за AUX
    public const uint RegRomVersion = 0xE7800210; // ROM Ver — его служба сверяет с каталогом

    // ctl_aux_access_args_t, 0xB0 байт: Size@0, Version@4, OpType@8, Flags@0xC, Address@0x10,
    // RAD@0x18, PortID@0x20, DataSize@0x24, Data[132]@0x28 — сверено с IGCLCSharpDLL.dll
    public const int ArgsSize = 0xB0, OffDataSize = 0x24, OffData = 0x28, DataMax = 132;

    /// <summary>Чтение OUI приёмника — только чтение, им контроллер и опознаётся.</summary>
    public static AuxOp ReadOui() => new(OpRead, FlagNative, DpcdSinkOui, [], 3);

    /// <summary>Himax: OUI <c>94 0B D5</c>. Только на нём делаем записи чтения регистра.</summary>
    public static bool IsHimax(byte[] oui) => oui is [0x94, 0x0B, 0xD5];

    /// <summary>
    /// Чтение регистра (<c>HX8874RegRead</c>): запись <c>0D 00</c>, запись <c>20 01</c>, запись с MOT
    /// 10 байт <c>00 &lt;адрес LE&gt; 00…</c>, чтение 4 байт. Записи идут в контроллер — так делает OEM
    /// при каждом скане; регистры прошивки от этого не меняются.
    /// </summary>
    public static AuxOp[] RegisterRead(uint reg)
    {
        var addr = new byte[10];
        BinaryPrimitives.WriteUInt32LittleEndian(addr.AsSpan(1), reg);
        return
        [
            new(OpWrite, FlagI2c, HimaxI2c, [0x0D, 0x00], 2),
            new(OpWrite, FlagI2c, HimaxI2c, [0x20, 0x01], 2),
            new(OpWrite, FlagI2cMot, HimaxI2c, addr, 10),
            new(OpRead, FlagI2c, HimaxI2c, [], 4),
        ];
    }

    /// <summary>Аргументы <c>ctlAUXAccess</c> под операцию — ровно 0xB0 байт.</summary>
    public static byte[] Args(AuxOp op)
    {
        var a = new byte[ArgsSize];
        BinaryPrimitives.WriteInt32LittleEndian(a.AsSpan(0x00), ArgsSize);
        BinaryPrimitives.WriteInt32LittleEndian(a.AsSpan(0x08), op.Op);
        BinaryPrimitives.WriteInt32LittleEndian(a.AsSpan(0x0C), op.Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(0x10), op.Address);
        BinaryPrimitives.WriteInt32LittleEndian(a.AsSpan(OffDataSize), op.Size);
        op.Data.CopyTo(a, OffData);
        return a;
    }

    /// <summary>Данные ответа: DataSize после вызова, не больше буфера. Негодный размер — пусто.</summary>
    public static byte[] Result(byte[] args)
    {
        int got = BinaryPrimitives.ReadInt32LittleEndian(args.AsSpan(OffDataSize));
        return got is > 0 and <= DataMax ? args[OffData..(OffData + got)] : [];
    }

    /// <summary>ROM Ver в виде каталога: 4 байта LE → hex без префикса (<c>06 0B 57 00</c> →
    /// <c>570B06</c>, в каталоге так же — <c>740B08</c>). Ноль или короткий ответ — null: так
    /// контроллер отвечает, когда ничего не прочиталось.</summary>
    public static string? RomVersion(byte[] r)
    {
        if (r.Length < 4) return null;
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(r);
        return v == 0 ? null : v.ToString("X6", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Текущая версия прошивки панели (ROM Ver TCON Himax) через Intel IGCL <c>ctlAUXAccess</c> — eDP AUX,
/// user-mode API драйвера Intel, тот же <c>ControlLib.dll</c>, что у <see cref="GpuTelemetry"/>.
/// Зовётся только по кнопке проверки драйверов и только если в каталоге есть прошивка экрана: чтение
/// включает записи в контроллер. Видна панель лишь в локальном сеансе при включённом экране — по RDP
/// IGCL отдаёт ноль выходов, и null («версия неизвестна») здесь нормальный ответ, не ошибка.
/// Своя инициализация IGCL на один вызов: держать её открытой между проверками незачем.
/// </summary>
public static class PanelFirmware
{
    private const int InitArgsSize = 36, AppVersion = 1 << 16; // как в GpuTelemetry

    /// <summary>ROM Ver контроллера («570B06») или null. Звать из фонового потока.</summary>
    public static string? ReadRomVersion()
    {
        IntPtr api = IntPtr.Zero;
        try
        {
            if (!Init(out api)) return null;
            foreach (IntPtr output in Outputs(api))
            {
                if (Run(output, ReadOui()) is not { } oui || !IsHimax(oui)) continue;
                byte[]? r = null;
                foreach (var op in RegisterRead(RegRomVersion))
                    if ((r = Run(output, op)) is null) break; // операция не прошла — дальше не пишем
                string? v = r is null ? null : RomVersion(r);
                if (v is null) Log.Write("PanelFirmware: контроллер Himax найден, но ROM Ver не прочитался");
                return v;
            }
            Log.Write("PanelFirmware: контроллер Himax не найден (RDP или экран выключен)");
            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Log.Write($"PanelFirmware: IGCL недоступен — {ex.GetType().Name}"); // не Intel
            return null;
        }
        finally
        {
            if (api != IntPtr.Zero)
                try
                {
                    int r = ctlClose(api);
                    if (r != 0) Log.Write($"PanelFirmware: ctlClose -> 0x{r:X8}");
                }
                catch (Exception ex) { Log.Ex("PanelFirmware.Close", ex); }
        }
    }

    private static bool Init(out IntPtr api)
    {
        var args = new byte[InitArgsSize];
        BinaryPrimitives.WriteInt32LittleEndian(args.AsSpan(0), InitArgsSize);
        BinaryPrimitives.WriteInt32LittleEndian(args.AsSpan(8), AppVersion);
        int r = ctlInit(args, out api);
        if (r == 0) return true;
        Log.Write($"PanelFirmware: ctlInit -> 0x{r:X8}");
        api = IntPtr.Zero;
        return false;
    }

    private static List<IntPtr> Outputs(IntPtr api)
    {
        var list = new List<IntPtr>();
        uint n = 0;
        if (ctlEnumerateDevices(api, ref n, null) != 0 || n == 0) return list;
        var devices = new IntPtr[n];
        if (ctlEnumerateDevices(api, ref n, devices) != 0) return list;
        foreach (IntPtr d in devices.Take((int)n))
        {
            uint m = 0;
            if (ctlEnumerateDisplayOutputs(d, ref m, null) != 0 || m == 0) continue;
            var outs = new IntPtr[m];
            if (ctlEnumerateDisplayOutputs(d, ref m, outs) == 0) list.AddRange(outs.Take((int)m));
        }
        return list;
    }

    /// <summary>Одна операция; null — IGCL вернул ошибку.</summary>
    private static byte[]? Run(IntPtr output, AuxOp op)
    {
        byte[] args = Args(op);
        int r = ctlAUXAccess(output, args);
        if (r == 0) return Result(args);
        Log.Write($"PanelFirmware: ctlAUXAccess op={op.Op} flags={op.Flags} addr=0x{op.Address:X} -> 0x{r:X8}");
        return null;
    }

    private const string Lib = "ControlLib.dll";
    [DllImport(Lib)] private static extern int ctlInit(byte[] args, out IntPtr api);
    [DllImport(Lib)] private static extern int ctlClose(IntPtr api);
    [DllImport(Lib)] private static extern int ctlEnumerateDevices(IntPtr api, ref uint count, IntPtr[]? devices);
    [DllImport(Lib)] private static extern int ctlEnumerateDisplayOutputs(IntPtr device, ref uint count, IntPtr[]? outputs);
    [DllImport(Lib)] private static extern int ctlAUXAccess(IntPtr output, [In, Out] byte[] args);
}
