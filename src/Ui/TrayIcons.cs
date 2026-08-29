using System.Runtime.InteropServices;
using XiControl.Wmi;

namespace XiControl.Ui;

/// <summary>
/// Монохромные значки трея из SVG-ассетов (tray-*): Авто/база — тумблеры,
/// Тихий — лист, Турбо — молния, Полная — ракета. Цвет под тему панели задач. Кэшируются.
/// </summary>
public static class TrayIcons
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    private const int SM_CXSMICON = 49;

    private static readonly Dictionary<(PerfMode?, bool), Icon> _cache = new();
    private static readonly List<IntPtr> _handles = new();

    public static Icon ForMode(PerfMode? mode, bool lightTaskbar)
    {
        var key = (mode, lightTaskbar);
        if (_cache.TryGetValue(key, out var ic)) return ic;
        ic = Build(mode, lightTaskbar);
        _cache[key] = ic;
        return ic;
    }

    private static Icon Build(PerfMode? mode, bool lightTaskbar)
    {
        string name = mode switch
        {
            PerfMode.Eco => SvgIcons.TrayPerfEco,
            PerfMode.Quiet => SvgIcons.TrayPerfQuiet,
            PerfMode.Balance => SvgIcons.TrayPerfBalance,
            PerfMode.Turbo => SvgIcons.TrayPerfTurbo,
            PerfMode.FullSpeed => SvgIcons.TrayPerfFull,
            _ => SvgIcons.TraySettings, // Авто + база
        };
        Color col = lightTaskbar ? Color.FromArgb(32, 32, 32) : Color.FromArgb(240, 240, 240);

        // Рендер сразу в фактический размер значка трея (с учётом DPI):
        // даунскейл системой размывает тонкие линии в кашу.
        int size = Math.Max(16, GetSystemMetrics(SM_CXSMICON));
        IntPtr h = SvgIcons.Render(name, size, col).GetHicon();
        _handles.Add(h);
        return Icon.FromHandle(h);
    }

    public static void DisposeAll()
    {
        foreach (var ic in _cache.Values) ic.Dispose();
        foreach (var h in _handles) DestroyIcon(h);
        _cache.Clear();
        _handles.Clear();
    }
}
