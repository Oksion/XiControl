using Microsoft.Win32;
using XiControl.Config;
using XiControl.Localization;
using XiControl.SystemIntegration;
using XiControl.Ui.Settings;

namespace XiControl.Ui;

/// <summary>
/// Единое окно настроек в стиле Windows 11: слева навигация по группам, справа прокручиваемая
/// панель опций. Создаётся лениво, между открытиями прячется (не диспозится) — в фоне таймеров
/// нет, поэтому «живёт» только пока открыто. Тема (тёмная/светлая) берётся из системы при показе.
/// Само содержимое — вкладки-контролы из Ui/Settings на общем тулките SettingsToolkit;
/// форма только хостит, переключает и задаёт хром.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly AppConfig _cfg;
    private readonly SettingsActions _act;

    private SettingsToolkit _ui;
    private NavStrip _nav = null!;
    private Panel _host = null!;
    // ленивое построение вкладок: _factories[i] строит панель при первом заходе на неё,
    // _panes[i] — кэш построенной (null = ещё не строили). Так тяжёлые вкладки (BatteryTab
    // с синхронным WMI-запросом) не платятся, пока их не открыли.
    private readonly List<Func<Panel>> _factories = [];
    private readonly List<Panel?> _panes = [];
    private int _tab;

    // смена разрешения/масштаба сыплется пачкой событий — гасим дребезг и пересобираем один раз
    // (шов IAppTimer тут не нужен: окно юнит-тестами не покрывается)
    private readonly UiTimer _rescale = new() { Interval = 250 };

    // MifsClient сюда сознательно не передаётся: окно железо не трогает — все «умные»
    // операции идут через колбэки SettingsActions в TrayApp/AppController.
    public SettingsForm(AppConfig cfg, SettingsActions act)
    {
        _cfg = cfg;
        _act = act;

        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual; // позицию считаем сами в Popup (всегда центр)
        KeyPreview = true;
        DoubleBuffered = true;
        // масштабируем себя сами (Sc + ScaledFonts от DeviceDpi) — иначе WinForms домасштабирует
        // поверх наших размеров, и геометрия разъедется вдвойне
        AutoScaleMode = AutoScaleMode.None;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { /* иконки нет — не критично */ }

        _ = Handle; // форсируем хэндл (нужен DeviceDpi)
        _ui = new SettingsToolkit(this, SettingsTheme.Load());
        ClientSize = new Size(_ui.Sc(824), _ui.Sc(700));

        // разрешение могло смениться и без смены DPI (удалённый рабочий стол подгоняет его
        // под клиента) — WM_DPICHANGED тогда не приходит, а пересобраться всё равно надо
        _rescale.Tick += () => { _rescale.Stop(); if (Visible) Rescale(); };
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        BuildAll();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _rescale.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Открыть окно. Содержимое пересобирается при каждом открытии: настройки могли смениться
    /// из трея/панели, пока окно было спрятано (авто-герцовка и т.п.), тема — тоже. Пересборка
    /// дешёвая (несколько десятков контролов), а окно открывают редко.
    /// </summary>
    public void Popup()
    {
        BuildAll();
        FitToScreen(Screen.FromPoint(Cursor.Position)); // открываемся по центру экрана с курсором
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    /// <summary>Разместить окно по центру экрана: высота — чтобы вкладки влезали без прокрутки,
    /// но не выше рабочей области (она меняется вместе с разрешением).</summary>
    private void FitToScreen(Screen screen)
    {
        var wa = screen.WorkingArea;
        var size = new Size(_ui.Sc(824), Math.Min(_ui.Sc(700), wa.Height - _ui.Sc(80)));
        if (ClientSize != size) ClientSize = size;
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
    }

    /// <summary>Пересобрать окно под текущий DPI/разрешение. Sc() и шрифты у нас пиксельные и
    /// снимаются в момент постройки: без пересборки текст остаётся в старом масштабе и расходится
    /// с разметкой — заметнее всего на пояснениях, высота карточки под них считается один раз
    /// по DescFont (SettingsToolkit.AddRow). Механика та же, что у смены темы: пересборка дешёвая.</summary>
    private void Rescale()
    {
        BuildAll();
        FitToScreen(Screen.FromControl(this));
    }

    /// <summary>Масштаб сменился или окно переехало на монитор с другим DPI — пересобрать
    /// (для флайаутов это же делает FlyoutForm.OnDpiRescaled).</summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Rescale();
    }

    // Сменилось разрешение/раскладка мониторов. Событие приходит из системного потока и пачкой —
    // маршалим в UI и гасим дребезг. Спрятанное окно не трогаем: его пересоберёт следующий Popup.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (!IsHandleCreated || IsDisposed || !Visible) return;
        try { BeginInvoke(new Action(() => { _rescale.Stop(); _rescale.Start(); })); }
        catch (ObjectDisposedException) { /* окно закрылось, пока событие шло к нам */ }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            ActiveControl = null; // форсим Leave у текстовых полей — сохранить недописанный путь
            e.Cancel = true;
            Hide(); // прячем, не закрываем
        }
        base.OnFormClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            ActiveControl = null; // как и при закрытии крестиком — коммит текстовых полей
            Hide();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        FormChrome.SetDwmDark(this, Theme.IsDark());
    }

    /// <summary>Windows сменила тему, окно открыто — пересобрать с новой палитрой «на лету»
    /// (Фаза 6.4; раньше тема перечитывалась только при следующем Popup).</summary>
    public void ThemeChanged()
    {
        if (Theme.IsDark() == _ui.T.Dark) return; // тема приложений не менялась — не мигаем
        BuildAll();
    }

    // ---- Построение ----

    private void BuildAll()
    {
        // окно видно (смена языка/видимости режимов) — гасим перерисовку целиком,
        // иначе пересборка мигает белым; в конце один Refresh
        bool live = IsHandleCreated && Visible;
        if (live) FormChrome.SetRedraw(this, false);
        try
        {
            BuildAllCore();
        }
        finally
        {
            if (live) { FormChrome.SetRedraw(this, true); Refresh(); }
        }
    }

    private void BuildAllCore()
    {
        _ui = new SettingsToolkit(this, SettingsTheme.Load()); // свежая тема на каждую пересборку
        FormChrome.SetDwmDark(this, _ui.T.Dark);
        Text = Loc.T("settings.title");
        BackColor = _ui.T.WinBg;

        SuspendLayout();
        // Clear не диспозит старые контролы — освобождаем сами (хэндлы, Region'ы), как в BuildMenu
        var stale = Controls.Cast<Control>().ToArray();
        Controls.Clear();
        foreach (var c in stale) c.Dispose();
        _panes.Clear();
        _factories.Clear();

        // ambient-шрифт формы — тоже из ScaledFonts: контролы без явного Font наследуют его,
        // а пунктовый шрифт при смене DPI разъезжается с нашими пиксельными
        Font = _ui.NoteFont;

        _host = new Panel { Dock = DockStyle.Fill, BackColor = _ui.T.WinBg, Tag = "host" };
        Controls.Add(_host);

        _nav = new NavStrip(_ui) { Dock = DockStyle.Left, Width = _ui.Sc(212) };
        _nav.SelectedChanged = SelectTab;
        Controls.Add(_nav); // Fill(_host) добавлен раньше → Left(_nav) резервирует левую полосу

        // пересборка — после выхода из обработчика: иначе смена языка/действия клавиши
        // диспозит контрол прямо под его же событием
        Action rebuild = () => BeginInvoke(new Action(BuildAll));

        // вкладки строим списком: «Экран» показываем, только пока «управление частотой» —
        // включённая функция (её мастер-тумблер живёт на вкладке «Функции»)
        // регистрируем вкладки фабриками (панель строится лениво в SelectTab, не тут):
        // так открытие окна и смена языка не конструируют неоткрытые вкладки
        var tabs = new List<(string key, NavGlyph glyph)>();
        void AddTab(string key, NavGlyph glyph, Func<Panel> make)
        {
            tabs.Add((key, glyph));
            _factories.Add(make);
            _panes.Add(null);
        }

        AddTab("settings.tab.general", NavGlyph.General, () => new GeneralTab(_ui, _cfg, _act, rebuild));
        AddTab("settings.tab.features", NavGlyph.Features, () => new FeaturesTab(_ui, _cfg, _act, rebuild));
        AddTab("settings.tab.battery", NavGlyph.Battery, () => new BatteryTab(_ui, _cfg, _act, rebuild));
        // «Экран» с XIC-29 виден всегда (там яркость); фича «управление частотой» скрывает
        // только раздел частоты внутри вкладки
        AddTab("settings.tab.display", NavGlyph.Display, () => new DisplayTab(_ui, _cfg, _act, rebuild));
        AddTab("settings.tab.touchpad", NavGlyph.Touchpad, () => new TouchpadTab(_ui, _cfg, _act, rebuild));
        AddTab("settings.tab.perf", NavGlyph.Perf, () => new PerfTab(_ui, _cfg, _act, rebuild));
        AddTab("settings.tab.keys", NavGlyph.Keys, () => new KeysTab(_ui, _cfg, rebuild));
        AddTab("settings.tab.api", NavGlyph.Api, () => new ApiTab(_ui, _cfg, _act, rebuild));
        AddTab("settings.tab.about", NavGlyph.About, () => new AboutTab(_ui, _act, rebuild));

        _nav.Tabs = [.. tabs];
        _tab = UiNav.ClampTab(_tab, _panes.Count); // вкладка исчезла (скрыли «Экран») — на первую
        _nav.Selected = _tab;

        SelectTab(_tab);
        ResumeLayout();
    }

    private void SelectTab(int i)
    {
        _tab = i;
        // ленивое построение: панель вкладки создаётся при первом заходе на неё
        if (_panes[i] is null)
        {
            var pane = _factories[i]();
            // хвостовой «воздух»: FlowLayoutPanel.AutoScroll не учитывает нижний Padding — без
            // спейсера последняя карточка обрезается при прокрутке
            pane.Controls.Add(new Panel { Width = _ui.RowW, Height = _ui.Sc(20), BackColor = _ui.T.WinBg, Margin = new Padding(0) });
            _host.Controls.Add(pane);
            FormChrome.SetDarkScrollbars(pane, _ui.T.Dark); // системная полоса в тон темы, не классическая светлая
            _panes[i] = pane;
        }
        for (int k = 0; k < _panes.Count; k++)
            if (_panes[k] is Panel p) p.Visible = k == i;
        _nav.Selected = i;
        _nav.Invalidate();
    }
}
