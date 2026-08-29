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
        PerfMode.Balance => FlyoutPalette.Blue,
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
