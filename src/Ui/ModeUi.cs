using XiControl.Wmi;

namespace XiControl.Ui;

/// <summary>UI-маппинг режима производительности: ключ локализации, вид OSD и акцентный цвет.</summary>
internal static class ModeUi
{
    /// <summary>Акцент ячейки режима в панели (палитра docs/10-colors.md).</summary>
    public static Color Accent(PerfMode m) => m switch
    {
        PerfMode.Eco => FlyoutPalette.Green,            // лист
        PerfMode.Quiet => Color.FromArgb(125, 160, 185), // сизый — под луну
        PerfMode.Auto => FlyoutPalette.Blue,
        PerfMode.Turbo => FlyoutPalette.Orange,
        PerfMode.FullSpeed => FlyoutPalette.Red,
        _ => FlyoutPalette.Blue,
    };

    public static string? Key(PerfMode m) => m switch
    {
        PerfMode.Eco => "mode.eco",
        PerfMode.Quiet => "mode.quiet",
        PerfMode.Auto => "mode.auto",
        PerfMode.Turbo => "mode.turbo",
        PerfMode.FullSpeed => "mode.full",
        _ => null,
    };

    public static OsdKind Kind(PerfMode m) => m switch
    {
        PerfMode.Eco => OsdKind.Eco,
        PerfMode.Quiet => OsdKind.Quiet,
        PerfMode.Auto => OsdKind.Auto,
        PerfMode.Turbo => OsdKind.Turbo,
        PerfMode.FullSpeed => OsdKind.Full,
        _ => OsdKind.Auto,
    };
}
