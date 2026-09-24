using XiControl.SystemIntegration;
using XiControl.Wmi;

namespace XiControl.Ui;

/// <summary>
/// Политика обновления значка трея: редкий опрос реального режима (извне его меняют
/// только сон/EC — свои изменения зовут Refresh сразу), кэш «без изменений — не трогаем»,
/// принудительная перерисовка при смене темы. Сам рендер и NotifyIcon — за колбэком
/// Apply (ставит TrayApp), поэтому логика тестируется без GDI и железа.
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private readonly IMifsClient _mifs;
    private readonly IAppTimer _poll;
    private PerfMode? _mode;
    private bool _init;
    private bool _light;

    /// <summary>Отрисовать и применить значок (режим, светлая панель задач).</summary>
    public Action<PerfMode?, bool>? Apply;

    /// <summary>Каждый опрос, даже без смены режима — для живого тултипа трея
    /// (заряд батареи меняется независимо от режима).</summary>
    public Action<PerfMode?>? Polled;

    /// <summary>Светлая ли панель задач — сидка системного запроса (Theme.TaskbarIsLight).</summary>
    public Func<bool> LightTaskbar = Theme.TaskbarIsLight;

    public TrayIconController(IMifsClient mifs, IAppTimer? poll = null)
    {
        _mifs = mifs;
        _poll = poll ?? new UiTimer();
        _poll.Interval = 30000;
        _poll.Tick += () => Refresh();
    }

    /// <summary>Применить значок по текущему состоянию и начать лёгкий опрос.</summary>
    public void Start()
    {
        _light = LightTaskbar();
        Refresh();
        _poll.Start();
    }

    /// <summary>Обновить значок по реальному режиму; force — перерисовать даже без изменений.</summary>
    public void Refresh(bool force = false)
    {
        PerfMode? mode;
        try { mode = _mifs.GetPerfMode(); }
        catch (Exception ex) { Log.Ex("TrayIconController.Refresh", ex); mode = null; }
        Polled?.Invoke(mode); // тултип живёт на каждом опросе, значок — только на изменениях
        if (!force && _init && mode == _mode) return; // без изменений — не трогаем
        _init = true;
        _mode = mode;
        Apply?.Invoke(mode, _light);
    }

    /// <summary>Windows сменила тему — перечитать цвет панели задач и перерисовать принудительно.</summary>
    public void ThemeChanged()
    {
        _light = LightTaskbar();
        Refresh(force: true);
    }

    public void Dispose() => _poll.Dispose();
}
