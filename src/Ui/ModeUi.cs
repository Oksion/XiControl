using XiControl.Wmi;

namespace XiControl.Ui;

/// <summary>UI-маппинг режима производительности: ключ локализации, вид OSD и акцентный цвет.</summary>
internal static class ModeUi
{
    /// <summary>Режим из value события OEM-клавиши производительности (HID_EVENT20/0x16).</summary>
    public static PerfMode? FromHotkeyValue(byte value) => value switch
    {
        0 => PerfMode.Turbo, // legacy OEM-ветка WorkloadTurbo
        (byte)PerfMode.Balance => PerfMode.Balance,
        (byte)PerfMode.Quiet => PerfMode.Quiet,
        (byte)PerfMode.Turbo => PerfMode.Turbo,
        (byte)PerfMode.FullSpeed => PerfMode.FullSpeed,
        (byte)PerfMode.Auto => PerfMode.Auto,
        (byte)PerfMode.Eco => PerfMode.Eco,
        _ => null,
    };

    /// <summary>Акцент ячейки режима в панели (палитра docs/10-colors.md).</summary>
    public static Color Accent(PerfMode m) => m switch
    {
        PerfMode.Eco => FlyoutPalette.Green,            // лист
        PerfMode.Quiet => Color.FromArgb(125, 160, 185), // сизый — под луну
        PerfMode.Auto => FlyoutPalette.Blue,
        PerfMode.Turbo => FlyoutPalette.Orange,
        PerfMode.FullSpeed => FlyoutPalette.Red,
        // Бирюза под весы: у «Баланса» был цвет «Авто», и в панели две ячейки читались бы
        // как одна и та же. Своё лицо, но по яркости в одном ряду с остальными акцентами.
        PerfMode.Balance => Color.FromArgb(38, 198, 218),
        _ => FlyoutPalette.Blue,
    };

    public static string? Key(PerfMode m) => m switch
    {
        PerfMode.Eco => "mode.eco",
        PerfMode.Quiet => "mode.quiet",
        PerfMode.Auto => "mode.auto",
        PerfMode.Turbo => "mode.turbo",
        PerfMode.FullSpeed => "mode.full",
        PerfMode.Balance => "mode.balance",
        _ => null,
    };

    /// <summary>
    /// Строка журнала на каждое событие смены режима (0x16, XIC-110): объявленный код, что
    /// ответила прошивка и от чего питаемся. Раньше писались только расхождения — и отчёт «режим
    /// сам сбрасывается при зарядке» (issue #109) было не разобрать: обычных смен в журнале нет.
    /// </summary>
    public static string EventLine(byte value, PerfMode? actual, bool online)
    {
        PerfMode? announced = FromHotkeyValue(value);
        string said = announced is PerfMode a ? a.ToString() : "неизвестно";
        string fw = actual is PerfMode m ? m.ToString() : "не ответила";
        string trust = actual is not null && announced is not null && actual != announced ? " — верим прошивке" : "";
        return $"Режим: событие 0x{value:X2} ({said}), прошивка {fw}, {(online ? "сеть" : "батарея")}{trust}";
    }

    /// <summary>Строка журнала на смену питания: режим сразу и через паузу — сброс прошивкой виден сам.</summary>
    public static string PowerLine(bool online, PerfMode? right, PerfMode? later, int laterMs) =>
        $"Режим: питание → {(online ? "сеть" : "батарея")}, сразу {Name(right)}, через {laterMs / 1000} с {Name(later)}";

    private static string Name(PerfMode? m) => m?.ToString() ?? "—";

    public static OsdKind Kind(PerfMode m) => m switch
    {
        PerfMode.Eco => OsdKind.Eco,
        PerfMode.Quiet => OsdKind.Quiet,
        PerfMode.Auto => OsdKind.Auto,
        PerfMode.Turbo => OsdKind.Turbo,
        PerfMode.FullSpeed => OsdKind.Full,
        PerfMode.Balance => OsdKind.Balance,
        _ => OsdKind.Auto,
    };
}
