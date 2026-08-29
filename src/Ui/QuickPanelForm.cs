using System.Runtime.InteropServices;
using XiControl.Config;
using XiControl.Localization;
using XiControl.SystemIntegration;
using XiControl.Wmi;

namespace XiControl.Ui;

/// <summary>
/// Интерактивная панель по Mi-кнопке: переключатель режимов (иконки),
/// сегмент заряда 80/100 и крестик. Закрывается по X, Esc и клику вне окна.
/// </summary>
public sealed class QuickPanelForm : FlyoutForm
{
    private readonly IMifsClient _mifs;
    private readonly AppConfig _cfg;
    private readonly TouchpadControl _tp;
    private readonly TouchscreenControl _ts;

    // видимые режимы (Эко/Полная мощность скрываются в Настройках или config.json)
    private (PerfMode mode, string key, Color accent)[] _modes = [];
    private Rectangle[] _modeRects = [];
    private Rectangle _care80, _care100, _travelCell, _tpCell, _tsCell, _hzCell, _awake, _close, _monBtn, _settingsBtn;

    private PerfMode? _mode;
    private bool _tpAvail, _tpOn; // тачпад: найден в системе / включён
    private bool _tsAvail, _tsOn; // сенсорный экран: найден в системе / включён
    private int _hover = -1; // 0..N-1 режимы, 10=80, 11=100, 12=close, 13=сова, 14=монитор, 15=герцовка, 16=в дорогу, 17=тачпад, 18=тачскрин, 19=настройки

    // клавиатурная навигация (Фаза 6.3): порядок обхода ячеек стрелками ←/→,
    // _focus — индекс в _order (-1 = фокуса нет, до первого нажатия стрелки)
    private readonly List<int> _order = [];
    private int _focus = -1;

    // единый таймер анимаций (работает, пока панель видна): hover-проявление ячеек
    // (~120 мс на цикл) + время t для живых иконок (стрелка, лист, пламя, звёзды...)
    private const float HoverMs = 120f;
    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 15 };

    // тултип по иконочным ячейкам: FlyoutTip — OwnerDraw в палитре флайаутов + dwell-задержка,
    // отрисовка в окне тултипа, наш GDI+-холст не трогается (XIC-8)
    private readonly FlyoutTip _tip;
    private float[] _hoverT = [];
    private float _gaugeT;

    // шрифты — из кэша ScaledFonts под текущий DPI (в OnPaint не создаём):
    // пропорции с геометрией Sc не разъезжаются после смены разрешения/масштаба
    private Font TitleFont => ScaledFonts.Get(DeviceDpi, "Segoe UI Semibold", 11f);
    private Font LabelFont => ScaledFonts.Get(DeviceDpi, "Segoe UI", 8.5f);
    private Font CapFont => ScaledFonts.Get(DeviceDpi, "Segoe UI", 9f);
    private Font PillFont => ScaledFonts.Get(DeviceDpi, "Segoe UI Semibold", 11f);

    // Команды — в AppController: панель железо не пишет, только читает состояние для
    // отрисовки. Обратная связь придёт колбэками контроллера через TrayApp (панель видима →
    // RefreshUi), честная ошибка прошивки — OSD поверх (Фаза 6.2).
    public Action<PerfMode>? SetMode;
    public Action<bool>? SetCare;
    public Action<bool>? SetTravel;
    public Action? ToggleOwl;
    public Action<bool>? SetAutoHz;
    public Action? ToggleTouchpad;
    public Action? ToggleTouchscreen;

    /// <summary>Перед показом: подтянуть порог заряда из прошивки в конфиг (его мог сменить кто-то
    /// снаружи). Запись в конфиг — дело контроллера, панель только просит (XIC-17).</summary>
    public Action? SyncCare;

    /// <summary>Кнопка-график слева от крестика: открыть окно «Монитор» (владелец — трей).</summary>
    public Action? MonitorRequested;

    /// <summary>Кнопка-шестерёнка левее «Монитора»: открыть окно настроек (владелец — трей).</summary>
    public Action? SettingsRequested;

    public QuickPanelForm(IMifsClient mifs, AppConfig cfg, TouchpadControl touchpad, TouchscreenControl touchscreen)
    {
        _mifs = mifs;
        _cfg = cfg;
        _tp = touchpad;
        _ts = touchscreen;
        _tip = new FlyoutTip(this);
        ReloadModes();
        _anim.Tick += (_, _) =>
        {
            _gaugeT += 0.015f;
            StepHoverAnim();
            Invalidate();
        };

        // borderless tool-window поверх всех окон — база FlyoutForm
        AccessibleName = Loc.T("panel.title");
        _ = Handle; // форсируем хэндл (нужен DeviceDpi и маршалинг)
    }

    private long _hiddenAt; // тик последнего скрытия (см. защиту ниже)

    public void Toggle()
    {
        if (Visible) { Hide(); return; }
        // клик по значку трея при открытой панели: mouse down ловит наш глобальный хук
        // (клик «вне окна» → панель прячется), а затем приходит MouseUp от NotifyIcon —
        // без этой защиты Toggle видит уже скрытую панель и тут же открывает её заново
        if (Environment.TickCount64 - _hiddenAt < 300) return;
        _focus = -1; // фокус появляется с первым нажатием стрелки
        RefreshState();
        DoLayout();
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (int)(wa.Height * 0.58));
        Show();
        Activate();
    }

    private void RefreshState()
    {
        // деградация с логом: панель откроется и без ответивших подсистем
        try { _mode = _mifs.GetPerfMode(); }
        catch (Exception ex) { Log.Ex("QuickPanel.Mode", ex); _mode = null; }
        // порог заряда рисуем из конфига, но сперва приводим его к прошивке: иначе внешняя смена
        // до панели не доходила вовсе, и подпись расходилась с поведением клика (XIC-17)
        try { SyncCare?.Invoke(); }
        catch (Exception ex) { Log.Ex("QuickPanel.SyncCare", ex); }
        try { _tpAvail = _cfg.TouchpadFeature && _tp.Available; _tpOn = _tp.IsEnabled() ?? false; }
        catch (Exception ex) { Log.Ex("QuickPanel.Touchpad", ex); _tpAvail = false; }
        try { _tsAvail = _cfg.TouchscreenFeature && _ts.Available; _tsOn = _ts.IsEnabled() ?? false; }
        catch (Exception ex) { Log.Ex("QuickPanel.Touchscreen", ex); _tsAvail = false; }
    }

    /// <summary>
    /// Пересобрать набор видимых режимов. Правило берём из общего <see cref="ModeVisibility"/>,
    /// а не повторяем здесь: панель уже разъезжалась с контроллером — свой фильтр знал только
    /// про Эко и Полную мощность и пропускал скрытый «Баланс».
    /// </summary>
    public void ReloadModes()
    {
        _modes = ModeVisibility.Visible(AppController.AllModes, _cfg.HiddenModes)
            .Select(m => (m, ModeUi.Key(m) ?? "mode.auto", ModeUi.Accent(m)))
            .ToArray();
        _modeRects = new Rectangle[_modes.Length];
        _hoverT = new float[_modes.Length];
        _hover = -1;
        if (Visible) { RefreshState(); DoLayout(); Invalidate(); }
    }

    /// <summary>Перечитать состояние и перерисовать (режим сменили извне, напр. Mi-кнопкой).</summary>
    public void RefreshUi()
    {
        RefreshState();
        Invalidate();
    }

    private void DoLayout()
    {
        int n = _modes.Length;
        int p = Sc(16), header = Sc(28), cellW = Sc(92), cellH = Sc(94), gap = Sc(8);
        // ширина панели не зависит от числа видимых режимов: при скрытых Эко/Полной
        // растягиваются сами ячейки (иконки остаются прежними, растут только границы),
        // иначе сжимался нижний ряд и пилюли 80/100 становились мелкими.
        // cellW расширен (~+10%) с добавлением ячейки тачскрина — чтобы нижний ряд
        // [В дорогу][80][100][тачскрин][тачпад][герцовка][сова] не теснился.
        int total = AppController.AllModes.Length;
        int content = cellW * total + gap * (total - 1);
        int width = content + p * 2;

        int modeY = p + header + Sc(4);
        int capY = modeY + cellH + Sc(12);
        int pillsY = capY + Sc(20);
        int pillsH = Sc(42);
        int height = pillsY + pillsH + p;

        int cellWn = (content - gap * (n - 1)) / n;
        for (int i = 0; i < n; i++)
        {
            int x = p + i * (cellWn + gap);
            int w = i == n - 1 ? p + content - x : cellWn; // последняя добирает остаток округления
            _modeRects[i] = new Rectangle(x, modeY, w, cellH);
        }

        // ряд заряда: [В дорогу] [80%] [100%] … [тачскрин] [тачпад] [авто-герцовка] [Не спать]
        int owlW = _cfg.OwlMode ? Sc(56) : 0;
        int hzW = _cfg.RefreshRateFeature ? Sc(56) : 0;
        int tpW = _tpAvail ? Sc(56) : 0;
        int tsW = _tsAvail ? Sc(56) : 0;
        int travelW = Sc(46);
        int pillsW = content - travelW - gap
            - (hzW > 0 ? hzW + gap : 0)
            - (tpW > 0 ? tpW + gap : 0) - (tsW > 0 ? tsW + gap : 0) - (owlW > 0 ? owlW + gap : 0);
        int half = (pillsW - gap) / 2;
        _travelCell = new Rectangle(p, pillsY, travelW, pillsH);
        _care80 = new Rectangle(_travelCell.Right + gap, pillsY, half, pillsH);
        _care100 = new Rectangle(_care80.Right + gap, pillsY, half, pillsH);
        _tsCell = tsW > 0 ? new Rectangle(_care100.Right + gap, pillsY, tsW, pillsH) : Rectangle.Empty;
        int afterTs = tsW > 0 ? _tsCell.Right : _care100.Right;
        _tpCell = tpW > 0 ? new Rectangle(afterTs + gap, pillsY, tpW, pillsH) : Rectangle.Empty;
        int afterTp = tpW > 0 ? _tpCell.Right : afterTs;
        _hzCell = hzW > 0 ? new Rectangle(afterTp + gap, pillsY, hzW, pillsH) : Rectangle.Empty;
        int afterHz = hzW > 0 ? _hzCell.Right : afterTp;
        _awake = owlW > 0 ? new Rectangle(afterHz + gap, pillsY, owlW, pillsH) : Rectangle.Empty;
        _close = new Rectangle(width - p - Sc(22), p - Sc(2), Sc(22), Sc(22));
        _monBtn = new Rectangle(_close.X - Sc(28), _close.Y, Sc(22), Sc(22));
        _settingsBtn = new Rectangle(_monBtn.X - Sc(28), _close.Y, Sc(22), Sc(22));

        Size = new Size(width, height);
        SetRoundedRegion(Sc(18));

        // порядок клавиатурного обхода — чистая логика в UiNav (покрыта тестами)
        _order.Clear();
        _order.AddRange(UiNav.PanelOrder(n, !_tsCell.IsEmpty, !_tpCell.IsEmpty, !_hzCell.IsEmpty, !_awake.IsEmpty));
        _focus = UiNav.KeepFocus(_focus, _order.Count); // раскладка сузилась — сбросить
    }

    private Rectangle RectOf(int id) => id switch
    {
        10 => _care80,
        11 => _care100,
        12 => _close,
        13 => _awake,
        14 => _monBtn,
        19 => _settingsBtn,
        15 => _hzCell,
        16 => _travelCell,
        17 => _tpCell,
        18 => _tsCell,
        _ => id >= 0 && id < _modeRects.Length ? _modeRects[id] : Rectangle.Empty,
    };

    // Стрелки/Enter/Space — до диалоговой обработки WinForms (детей-контролов у панели нет)
    protected override bool ProcessDialogKey(Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Right:
            case Keys.Down:
                _focus = UiNav.NextFocus(_focus, _order.Count, forward: true);
                Invalidate();
                return true;
            case Keys.Left:
            case Keys.Up:
                _focus = UiNav.NextFocus(_focus, _order.Count, forward: false);
                Invalidate();
                return true;
            case Keys.Enter:
            case Keys.Space:
                if (_focus >= 0) { Activate(_order[_focus]); return true; }
                break;
        }
        return base.ProcessDialogKey(keyData);
    }

    // Esc как системный хоткей на время показа: панель открывается из события WMI-клавиши,
    // и Windows может не отдать ей фокус — обычный KeyDown тогда не приходит.
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    private const int WM_HOTKEY = 0x0312, HkEscId = 1;
    private const uint VK_ESCAPE = 0x1B;

    // Глобальный хук мыши: закрывать панель по клику вне её габаритов, не полагаясь на
    // активацию окна. OnDeactivate у borderless topmost tool-window ненадёжен (панель не
    // всегда получает фокус — та же причина, по которой Esc сделан через RegisterHotKey),
    // и после наведения/анимации внешний клик переставал её закрывать.
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207;
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? lpModuleName);

    // POINT (два int) в начале структуры разложены полями ptX/ptY — layout идентичен
    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public int ptX; public int ptY; public uint mouseData; public uint flags; public uint time; public IntPtr dwExtraInfo; }

    private IntPtr _mouseHook;
    private HookProc? _mouseProc; // держим делегат живым — иначе GC соберёт его и колбэк упадёт

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            _gaugeT = 0f;
            _anim.Start();
            if (!RegisterHotKey(Handle, HkEscId, 0, VK_ESCAPE))
                Log.Write("QuickPanel: RegisterHotKey(Esc) не удалась — Esc занят другим приложением");
            InstallMouseHook();
        }
        else
        {
            _hiddenAt = Environment.TickCount64; // для защиты Toggle от мгновенного реопена
            _anim.Stop();
            _tip.Hide(); // иначе отложенный dwell-показ всплывёт над уже скрытой панелью
            UnregisterHotKey(Handle, HkEscId);
            RemoveMouseHook();
        }
    }

    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) return;
        _mouseProc = MouseHookProc; // ссылка в поле — защита от сборки делегата
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0);
        if (_mouseHook == IntPtr.Zero)
            Log.Write("QuickPanel: SetWindowsHookEx(WH_MOUSE_LL) не удалась — панель закроется только по деактивации");
    }

    private void RemoveMouseHook()
    {
        if (_mouseHook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
        _mouseProc = null;
    }

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // сравниваем в long: (int)IntPtr на x64 молча обрезает старшие биты (CA2020)
        if (nCode >= 0 && (long)wParam is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
        {
            var h = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            // клик не проглатываем — просто прячем панель, если он вне её габаритов
            if (Visible && !Bounds.Contains(h.ptX, h.ptY))
                BeginInvoke(new Action(Hide));
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && (int)m.WParam == HkEscId) { Hide(); return; }
        base.WndProc(ref m);
    }

    // ---- закрытие ---- (Esc — в базе FlyoutForm)
    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); Hide(); }

    // ---- ввод ----
    // DPI монитора сменился — пересобрать раскладку (Size/Region/ячейки) под новый DeviceDpi
    protected override void OnDpiRescaled() => DoLayout();

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = HitTest(e.Location);
        if (h != _hover) { _hover = h; UpdateTip(h, e.Location); Invalidate(); }
    }
    protected override void OnMouseLeave(EventArgs e) { if (_hover != -1) { _hover = -1; _tip.Hide(); Invalidate(); } }

    // Показать/спрятать тултип на смене hover-ячейки (иконочные — остальные подписаны).
    private void UpdateTip(int id, Point at) => _tip.Update(TipFor(id), at);

    private static string? TipFor(int id) => id switch
    {
        12 => Loc.T("panel.close"),
        13 => Loc.T("panel.awake"),
        14 => Loc.T("menu.monitor"),
        19 => Loc.T("menu.settings"),
        15 => Loc.T("panel.hz"),
        16 => Loc.T("panel.travel"),
        17 => Loc.T("panel.touchpad"),
        18 => Loc.T("panel.touchscreen"),
        _ => null, // режимы и пилюли 80/100 подписаны текстом — тултип не нужен
    };

    // ведём прогресс каждой ячейки к цели (1 — под курсором, 0 — нет)
    private void StepHoverAnim()
    {
        const float step = 15f / HoverMs;
        for (int i = 0; i < _hoverT.Length; i++)
        {
            float target = _hover == i ? 1f : 0f;
            if (Math.Abs(_hoverT[i] - target) < 0.001f) continue;
            _hoverT[i] = Math.Clamp(_hoverT[i] + Math.Sign(target - _hoverT[i]) * step, 0f, 1f);
        }
    }

    protected override void OnMouseClick(MouseEventArgs e) => Activate(HitTest(e.Location));

    // Общий исполнитель для мыши и клавиатуры (h — id ячейки из HitTest/_order)
    private void Activate(int h)
    {
        if (h == 12) { Hide(); return; }
        if (h == 14) { MonitorRequested?.Invoke(); return; }
        if (h == 19) { SettingsRequested?.Invoke(); return; } // окно настроек заберёт фокус → панель скроется
        if (h >= 0 && h < _modes.Length)
        {
            SetMode?.Invoke(_modes[h].mode);
        }
        else if (h == 10 || h == 11)
        {
            SetCare?.Invoke(h == 10); // явный выбор лимита; отмену «в дорогу» делает контроллер
        }
        else if (h == 16)
        {
            if (!_cfg.ChargeCare) return; // при постоянном 100% ячейка неактивна
            SetTravel?.Invoke(!_cfg.TravelMode);
        }
        else if (h == 13)
        {
            ToggleOwl?.Invoke(); // «Не спать»: экран/сон + крышка на AC (см. AwakeMode)
        }
        else if (h == 15 && !_hzCell.IsEmpty)
        {
            SetAutoHz?.Invoke(!_cfg.AutoRefreshRate); // вкл — контроллер сразу применит частоту
        }
        else if (h == 17 && !_tpCell.IsEmpty)
        {
            // CM-вызов небыстрый (сотни мс) — контроллер уйдёт в фон; состояние переключаем
            // оптимистично, колбэк TouchpadToggled уточнит фактическое (через RefreshUi)
            _tpOn = !_tpOn;
            Invalidate();
            ToggleTouchpad?.Invoke();
        }
        else if (h == 18 && !_tsCell.IsEmpty)
        {
            // сенсорный экран: то же оптимистичное переключение
            _tsOn = !_tsOn;
            Invalidate();
            ToggleTouchscreen?.Invoke();
        }
    }

    private int HitTest(Point pt)
    {
        if (_close.Contains(pt)) return 12;
        if (_monBtn.Contains(pt)) return 14;
        if (_settingsBtn.Contains(pt)) return 19;
        for (int i = 0; i < _modes.Length; i++) if (_modeRects[i].Contains(pt)) return i;
        if (_travelCell.Contains(pt)) return 16;
        if (_care80.Contains(pt)) return 10;
        if (_care100.Contains(pt)) return 11;
        if (!_tpCell.IsEmpty && _tpCell.Contains(pt)) return 17;
        if (!_tsCell.IsEmpty && _tsCell.Contains(pt)) return 18;
        if (_hzCell.Contains(pt)) return 15;
        if (!_awake.IsEmpty && _awake.Contains(pt)) return 13;
        return -1;
    }

    // ---- отрисовка ----
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintChrome(g, Sc(18));

        TextRenderer.DrawText(g, Loc.T("panel.title"), TitleFont,
            new Rectangle(Sc(16), Sc(12), Width, Sc(22)), FlyoutPalette.Text, TextFormatFlags.Left | TextFormatFlags.Top);

        // крестик, «Монитор» и шестерёнка «Настройки» слева от него
        Draw.CloseButton(g, _close, _hover == 12);
        Draw.MonitorButton(g, _monBtn, _hover == 14);
        Draw.SettingsButton(g, _settingsBtn, _hover == 19);

        // режимы
        for (int i = 0; i < _modes.Length; i++)
        {
            var r = _modeRects[i];
            bool active = _mode == _modes[i].mode;
            bool hover = _hover == i;
            DrawCell(g, r, active, hover, _modes[i].accent, Sc(10));

            // цветные SVG-иконки: активная — в полный цвет; hover плавно проявляет и подращивает
            float t = _hoverT[i];
            float grow = Sc(40) * 0.08f * t;
            var iconR = new RectangleF(
                r.X + (r.Width - Sc(40) - grow) / 2f, r.Y + Sc(9) - grow / 2f,
                Sc(40) + grow, Sc(40) + grow);
            var (op, sat) = active ? (1f, 1f) : InactiveFx(t);
            // активная ячейка «живёт» всегда, остальные — по мере наведения
            float k = active ? 1f : t;
            if (_anim.Enabled && k > 0.01f)
                DrawModeIconAnimated(g, _modes[i].mode, iconR, op, k, sat);
            else
                DrawModeIcon(g, _modes[i].mode, iconR, op, sat);

            TextRenderer.DrawText(g, Loc.T(_modes[i].key), LabelFont,
                new Rectangle(r.X + Sc(3), r.Bottom - Sc(38), r.Width - Sc(6), Sc(36)),
                active ? FlyoutPalette.Text : FlyoutPalette.Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }

        // заряд (заголовок слева) + «Не спать» (заголовок справа, над совой)
        TextRenderer.DrawText(g, Loc.T("panel.charge"), CapFont,
            new Rectangle(Sc(16), _travelCell.Y - Sc(20), Width, Sc(18)), FlyoutPalette.Dim, TextFormatFlags.Left | TextFormatFlags.Top);

        // «В дорогу»: активна = TravelMode; неактивна (серая), когда базово стоит постоянный 100%.
        // Пилюли 80/100 показывают базовую настройку (ChargeCare), «В дорогу» — временный оверрайд.
        bool travelEnabled = _cfg.ChargeCare;
        DrawCell(g, _travelCell, _cfg.TravelMode, travelEnabled && _hover == 16, FlyoutPalette.Orange, Sc(10));
        float trIcon = Math.Min(_travelCell.Width, _travelCell.Height) - Sc(8);
        // недоступна (постоянный 100%): в светлой теме прозрачность + почти ч/б, иначе «грязи» нет
        var (trOp, trSat) = !travelEnabled
            ? (FlyoutPalette.Dark ? (0.28f, 1f) : (0.45f, 0.15f))
            : CellFx(_cfg.TravelMode || _hover == 16);
        var trRect = new RectangleF(_travelCell.X + (_travelCell.Width - trIcon) / 2f, _travelCell.Y + (_travelCell.Height - trIcon) / 2f, trIcon, trIcon);
        if (_cfg.TravelMode)
            SvgIcons.DrawTravelPulse(g, trRect, _gaugeT, trOp); // молния мигает, когда режим активен
        else
            SvgIcons.Draw(g, SvgIcons.TravelOff, trRect, trOp, trSat);

        // левая пилюля — выбранный порог «беречь» (X% из настроек), не хардкод 80
        DrawPill(g, _care80, _cfg.CarePercent() + "%", _cfg.ChargeCare, _hover == 10, FlyoutPalette.Green, PillFont);
        DrawPill(g, _care100, "100%", !_cfg.ChargeCare, _hover == 11, Color.FromArgb(120, 120, 125), PillFont);

        // тачпад: подсвечиваем ячейку, когда он ВЫКЛЮЧЕН — нестандартное состояние заметнее
        if (!_tpCell.IsEmpty)
        {
            DrawCell(g, _tpCell, !_tpOn, _hover == 17, FlyoutPalette.Red, Sc(10));
            float tpIcon = Math.Min(_tpCell.Width, _tpCell.Height) - Sc(8);
            var (tpOp, tpSat) = CellFx(!_tpOn || _hover == 17);
            SvgIcons.Draw(g,
                _tpOn ? SvgIcons.Touchpad : SvgIcons.TouchpadOff,
                new RectangleF(_tpCell.X + (_tpCell.Width - tpIcon) / 2f, _tpCell.Y + (_tpCell.Height - tpIcon) / 2f, tpIcon, tpIcon),
                tpOp, tpSat);
        }

        // сенсорный экран: та же логика подсветки «выключен = заметнее», что и у тачпада
        if (!_tsCell.IsEmpty)
        {
            DrawCell(g, _tsCell, !_tsOn, _hover == 18, FlyoutPalette.Red, Sc(10));
            float tsIcon = Math.Min(_tsCell.Width, _tsCell.Height) - Sc(8);
            var (tsOp, tsSat) = CellFx(!_tsOn || _hover == 18);
            SvgIcons.Draw(g,
                _tsOn ? SvgIcons.Touchscreen : SvgIcons.TouchscreenOff,
                new RectangleF(_tsCell.X + (_tsCell.Width - tsIcon) / 2f, _tsCell.Y + (_tsCell.Height - tsIcon) / 2f, tsIcon, tsIcon),
                tsOp, tsSat);
        }

        // авто-герцовка: монитор с круговыми стрелками, активна при включённой опции
        // (ячейки нет, если «управление частотой» отключено фичей)
        if (!_hzCell.IsEmpty)
        {
            DrawCell(g, _hzCell, _cfg.AutoRefreshRate, _hover == 15, FlyoutPalette.Blue, Sc(10));
            float hzIcon = Math.Min(_hzCell.Width, _hzCell.Height) - Sc(8);
            var (hzOp, hzSat) = CellFx(_cfg.AutoRefreshRate || _hover == 15);
            SvgIcons.Draw(g,
                _cfg.AutoRefreshRate ? SvgIcons.RefreshRate : SvgIcons.RefreshRateOff,
                new RectangleF(_hzCell.X + (_hzCell.Width - hzIcon) / 2f, _hzCell.Y + (_hzCell.Height - hzIcon) / 2f, hzIcon, hzIcon),
                hzOp, hzSat);
        }

        // сова: ячейка в стиле режимов, бодрая при включённом «Не спать»
        if (!_awake.IsEmpty)
        {
            TextRenderer.DrawText(g, Loc.T("panel.awake"), CapFont,
                new Rectangle(0, _awake.Y - Sc(20), _awake.Right, Sc(18)), FlyoutPalette.Dim, TextFormatFlags.Right | TextFormatFlags.Top);

            DrawCell(g, _awake, _cfg.Awake, _hover == 13, FlyoutPalette.Blue, Sc(10));
            float owlIcon = Math.Min(_awake.Width, _awake.Height) - Sc(8);
            var (owlOp, owlSat) = CellFx(_cfg.Awake || _hover == 13);
            SvgIcons.Draw(g,
                _cfg.Awake ? SvgIcons.OwlAwake : SvgIcons.OwlAsleep,
                new RectangleF(_awake.X + (_awake.Width - owlIcon) / 2f, _awake.Y + (_awake.Height - owlIcon) / 2f, owlIcon, owlIcon),
                owlOp, owlSat);
        }

        // клавиатурный фокус: пунктирное кольцо вокруг текущей ячейки
        if (_focus >= 0 && _focus < _order.Count && RectOf(_order[_focus]) is { IsEmpty: false } fr)
        {
            using var pen = new Pen(FlyoutPalette.Text, 1.4f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            using var path = Draw.Rounded(new RectangleF(fr.X - 2.5f, fr.Y - 2.5f, fr.Width + 5f, fr.Height + 5f), Sc(11));
            g.DrawPath(pen, path);
        }
    }

    private static void DrawCell(Graphics g, Rectangle r, bool active, bool hover, Color accent, int corner)
    {
        using var bg = new SolidBrush(active
            ? Blend(FlyoutPalette.Cell, accent, 0.18f)
            : (hover ? FlyoutPalette.CellHover : FlyoutPalette.Cell));
        using var path = Draw.Rounded(r, corner);
        g.FillPath(bg, path);
        if (active)
        {
            using var pen = new Pen(accent, 1.6f);
            g.DrawPath(pen, path);
        }
    }

    // при наведении: та же иконка, но живая; k = прогресс hover (амплитуда вкатывается плавно)
    // Эффект неактивной иконки, зависящий от темы: тёмная гасит прозрачностью (исторический
    // вид), светлая — приглушает насыщенность при полной яркости: полупрозрачные цветные иконки
    // на светлом фоне выглядят белёсыми, десатурация — нет. t — прогресс hover (0..1).
    private static (float Op, float Sat) InactiveFx(float t)
        => FlyoutPalette.Dark ? (0.45f + 0.55f * t, 1f) : (1f, 0.55f + 0.45f * t);

    // То же для ячеек нижнего ряда (у них hover без плавности — состояние бинарное)
    private static (float Op, float Sat) CellFx(bool lit)
        => lit ? (1f, 1f) : FlyoutPalette.Dark ? (0.6f, 1f) : (1f, 0.6f);

    private void DrawModeIconAnimated(Graphics g, PerfMode m, RectangleF r, float opacity, float k, float saturation = 1f)
    {
        switch (m)
        {
            case PerfMode.Eco: SvgIcons.DrawLeafSway(g, r, _gaugeT, k, opacity, saturation); break;
            case PerfMode.Quiet: SvgIcons.DrawMoonTwinkle(g, r, _gaugeT, k, opacity, saturation); break;
            case PerfMode.Auto: SvgIcons.DrawGauge(g, r, k * OsdForm.SweepAngle(_gaugeT), opacity, saturation); break;
            case PerfMode.Turbo: SvgIcons.DrawBoltPulse(g, r, _gaugeT, k, opacity, saturation); break;
            case PerfMode.FullSpeed: SvgIcons.DrawRocket(g, r, _gaugeT, k, opacity, saturation); break;
            default: DrawModeIcon(g, m, r, opacity, saturation); break;
        }
    }

    private static void DrawModeIcon(Graphics g, PerfMode m, RectangleF r, float opacity, float saturation = 1f)
    {
        string name = m switch
        {
            PerfMode.Eco => SvgIcons.PerfEco,
            PerfMode.Quiet => SvgIcons.PerfQuiet,
            PerfMode.Balance => SvgIcons.PerfBalance,
            PerfMode.Auto => SvgIcons.PerfAuto,
            PerfMode.Turbo => SvgIcons.PerfTurbo,
            PerfMode.FullSpeed => SvgIcons.PerfFull,
            _ => SvgIcons.PerfAuto,
        };
        SvgIcons.Draw(g, name, r, opacity, saturation);
    }

    private static void DrawPill(Graphics g, Rectangle r, string text, bool active, bool hover, Color accent, Font font)
    {
        Color bg = active ? accent : (hover ? FlyoutPalette.CellHover : FlyoutPalette.Cell);
        using (var b = new SolidBrush(bg))
        using (var path = Draw.Rounded(r, r.Height / 2f))
            g.FillPath(b, path);

        TextRenderer.DrawText(g, text, font, r,
            active ? Color.White : FlyoutPalette.Dim,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }


    private static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _anim.Dispose(); _tip.Dispose(); RemoveMouseHook(); }
        base.Dispose(disposing);
    }
}
