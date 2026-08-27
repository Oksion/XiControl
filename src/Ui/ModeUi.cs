using XiControl.Wmi;

namespace XiControl.Ui;

/// <summary>UI-маппинг режима производительности: ключ локализации, вид OSD и акцентный цвет.</summary>
internal static class ModeUi
{
    /// <summary>Преобразует value события HID_EVENT20/0x16 в режим XiControl.</summary>
    public static PerfMode? FromHotkeyValue(byte value) => value switch
    {
        0 => PerfMode.Turbo, // legacy OEM branch: WorkloadTurbo
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
        PerfMode.Eco => Color.FromArgb(72, 189, 112),
        PerfMode.Quiet => Color.FromArgb(125, 160, 185), // сизый — под луну
        PerfMode.Auto => Color.FromArgb(74, 163, 255),
        PerfMode.Turbo => Color.FromArgb(255, 158, 74),
        PerfMode.FullSpeed => Color.FromArgb(255, 92, 104),
        PerfMode.Balance => Color.FromArgb(125, 110, 255),
        _ => Color.FromArgb(74, 163, 255),
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

    public static string Glyph(PerfMode mode) => mode switch
    {
        PerfMode.Eco => "\uE8BE",
        PerfMode.Quiet => "\uE708",
        PerfMode.Auto => "\uE9D9",
        PerfMode.Turbo => "\uE945",
        PerfMode.FullSpeed => "\uEC4A",
        PerfMode.Balance => "\uE9D9",
        _ => "\uE9D9",
    };

    public static string SvgIcon(PerfMode mode) => mode switch
    {
        PerfMode.Eco => SvgIcons.PerfEco,
        PerfMode.Quiet => SvgIcons.PerfQuiet,
        PerfMode.Auto => SvgIcons.PerfAuto,
        PerfMode.Turbo => SvgIcons.PerfTurbo,
        PerfMode.FullSpeed => SvgIcons.PerfFull,
        PerfMode.Balance => SvgIcons.PerfAuto,
        _ => SvgIcons.PerfAuto,
    };

    public static string MenuSvgIcon(PerfMode mode) => mode switch
    {
        PerfMode.Eco => SvgIcons.MenuPerfEco,
        PerfMode.Quiet => SvgIcons.MenuPerfQuiet,
        PerfMode.Auto => SvgIcons.MenuPerfAuto,
        PerfMode.Turbo => SvgIcons.MenuPerfTurbo,
        PerfMode.FullSpeed => SvgIcons.MenuPerfFull,
        PerfMode.Balance => SvgIcons.MenuPerfBalance,
        _ => SvgIcons.MenuPerfAuto,
    };

    public static OsdKind Kind(PerfMode m) => m switch
    {
        PerfMode.Eco => OsdKind.Eco,
        PerfMode.Quiet => OsdKind.Quiet,
        PerfMode.Auto => OsdKind.Auto,
        PerfMode.Turbo => OsdKind.Turbo,
        PerfMode.FullSpeed => OsdKind.Full,
        PerfMode.Balance => OsdKind.Auto,
        _ => OsdKind.Auto,
    };
}
