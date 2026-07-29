using XiControl.Config;
using XiControl.Localization;
using XiControl.Wmi;

namespace XiControl.Ui;

/// <summary>
/// Меню трея: ленивое построение пунктов (на Opening), тёмная тема, показ по клику
/// (приватный NotifyIcon.ShowContextMenu через рефлексию — даёт правильную позицию
/// и авто-закрытие) и «прогрев» первого показа. Команды идут в AppController;
/// окна (монитор/настройки) и выход — колбэки TrayApp.
/// </summary>
public sealed class TrayMenuBuilder : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly IMifsClient _mifs;
    private readonly AppController _controller;
    private bool _dark = Theme.IsDark();

    /// <summary>Само меню — TrayApp вешает его на NotifyIcon.ContextMenuStrip.</summary>
    public ContextMenuStrip Menu { get; }

    // окна и завершение — вотчина TrayApp
    public Action? ShowMonitor;
    public Action? OpenSettings;
    public Action? ExitRequested;
    public Func<bool> MonitorVisible = () => false;

    public TrayMenuBuilder(AppConfig cfg, IMifsClient mifs, AppController controller)
    {
        _cfg = cfg;
        _mifs = mifs;
        _controller = controller;

        Menu = new ContextMenuStrip { Font = new Font("Segoe UI", 9F) };
        Menu.Opening += (_, _) => Build();
        ApplyTheme();
    }

    /// <summary>Показ меню по клику на значке (Build случится сам через Opening).</summary>
    public void Show(NotifyIcon tray)
    {
        try
        {
            var mi = tray.GetType().GetMethod("ShowContextMenu",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (mi != null) { mi.Invoke(tray, null); return; }
        }
        catch (Exception ex) { Log.Ex(nameof(TrayMenuBuilder) + ".Show", ex); }
        Menu.Show(Cursor.Position); // запасной путь, если приватный API исчезнет из WinForms
    }

    /// <summary>
    /// «Прогрев»: без него самый первый клик по значку проглатывается — первый показ
    /// ContextMenuStrip инициализирует ленивые ресурсы (хэндл меню, первый WMI-вызов
    /// в Build, передний план приложения). Показ за экраном с мгновенным закрытием.
    /// </summary>
    public void Prime()
    {
        try { Menu.Show(new Point(-32000, -32000)); Menu.Close(); }
        catch (Exception ex) { Log.Ex(nameof(TrayMenuBuilder) + ".Prime", ex); }
    }

    /// <summary>Windows сменила тему — перечитать и перекрасить меню.</summary>
    public void ThemeChanged()
    {
        bool dark = Theme.IsDark();
        if (dark != _dark) { _dark = dark; ApplyTheme(); }
    }

    private void Build()
    {
        // Clear не диспозит старые пункты — освобождаем сами
        var stale = Menu.Items.Cast<ToolStripItem>().ToArray();
        Menu.Items.Clear();
        foreach (var it in stale) it.Dispose();

        // размер иконок меню под текущий DPI (сейчас иконка только у пункта «Язык»)
        int imgSz = (int)Math.Round(16 * Menu.DeviceDpi / 96.0);
        Menu.ImageScalingSize = new Size(imgSz, imgSz);

        // --- Заряд ---
        // Порог приводим к прошивке (её мог сменить кто-то снаружи) и дальше рисуем из конфига: раньше
        // галочка считалась от живого значения, а процент в подписи брался из конфига — меню могло
        // показать «беречь 60%» с галочкой, когда в железе 50% (XIC-17).
        _controller.SyncCareFromFirmware();
        var charge = new ToolStripMenuItem(Loc.T("menu.charge", _cfg.CarePercent())) { Checked = _cfg.ChargeCare };
        // состояние читаем в момент клика: пока меню висело, его мог сменить ChargeGuard
        charge.Click += (_, _) => _controller.ToggleCharge();
        Menu.Items.Add(charge);

        // «В дорогу»: временный заряд до 100% поверх «беречь 80%» (неактивно при постоянном 100%)
        var travel = new ToolStripMenuItem(Loc.T("menu.travel")) { Checked = _cfg.TravelMode, Enabled = _cfg.ChargeCare };
        travel.Click += (_, _) => _controller.SetTravel(!_cfg.TravelMode);
        Menu.Items.Add(travel);

        // --- Режим совы (не спать) — если фича не скрыта конфигом ---
        if (_cfg.OwlMode)
        {
            var owl = new ToolStripMenuItem(Loc.T("menu.owl")) { Checked = _cfg.Awake };
            owl.Click += (_, _) => _controller.ToggleAwake();
            Menu.Items.Add(owl);
        }

        // --- Авто-герцовка (частота экрана по питанию) — если фича не скрыта конфигом ---
        if (_cfg.RefreshRateFeature)
        {
            var hz = new ToolStripMenuItem(Loc.T("menu.hz", _cfg.AcRefreshRate, _cfg.BatteryRefreshRate))
            { Checked = _cfg.AutoRefreshRate };
            hz.Click += (_, _) => _controller.ToggleAutoHz(!_cfg.AutoRefreshRate);
            Menu.Items.Add(hz);
        }

        // --- Монитор (Вт / CPU / RAM) ---
        var monitor = new ToolStripMenuItem(Loc.T("menu.monitor")) { Checked = MonitorVisible() };
        monitor.Click += (_, _) => ShowMonitor?.Invoke();
        Menu.Items.Add(monitor);

        // --- Режим (подменю) ---
        PerfMode? current = Safe<PerfMode?>(() => _mifs.GetPerfMode(), null);
        string currentName = current is PerfMode cm && ModeUi.Key(cm) is string mk ? Loc.T(mk) : "—";
        var perf = new ToolStripMenuItem($"{Loc.T("menu.perf")}:  {currentName}");
        foreach (var mode in _controller.VisibleModes)
        {
            var m = mode;
            var item = new ToolStripMenuItem(Loc.T(ModeUi.Key(m) ?? "mode.auto")) { Checked = current == m };
            item.Click += (_, _) => _controller.SetMode(m);
            perf.DropDownItems.Add(item);
        }
        TintDropDown(perf);
        Menu.Items.Add(perf);

        Menu.Items.Add(new ToolStripSeparator());

        // --- Настройки (отдельное окно в стиле Win11: все опции по группам) ---
        var settings = new ToolStripMenuItem(Loc.T("menu.settings") + "…");
        settings.Click += (_, _) => OpenSettings?.Invoke();
        Menu.Items.Add(settings);

        Menu.Items.Add(new ToolStripSeparator());

        // --- Выход ---
        var exit = new ToolStripMenuItem(Loc.T("menu.exit"));
        exit.Click += (_, _) => ExitRequested?.Invoke();
        Menu.Items.Add(exit);
    }

    private void ApplyTheme()
    {
        if (_dark)
        {
            ToolStripManager.Renderer = new DarkMenuRenderer();
            Menu.RenderMode = ToolStripRenderMode.ManagerRenderMode;
            Menu.BackColor = DarkPalette.Bg;
            Menu.ForeColor = DarkPalette.Text;
        }
        else
        {
            Menu.RenderMode = ToolStripRenderMode.System;
            Menu.ResetBackColor();
            Menu.ResetForeColor();
        }
    }

    private void TintDropDown(ToolStripMenuItem parent)
    {
        if (!_dark) return;
        parent.DropDown.BackColor = DarkPalette.Bg;
        parent.DropDown.ForeColor = DarkPalette.Text;
    }

    private static T Safe<T>(Func<T> f, T fallback,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        try { return f(); }
        catch (Exception ex) { Log.Ex($"TrayMenuBuilder.{caller}", ex); return fallback; }
    }

    public void Dispose() => Menu.Dispose();
}
