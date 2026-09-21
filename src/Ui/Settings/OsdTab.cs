using XiControl.Config;
using XiControl.Localization;

namespace XiControl.Ui.Settings;

/// <summary>
/// Вкладка «Уведомления»: как выглядят всплывающие подсказки (OSD) и какие из них показывать.
///
/// Отдельной вкладкой, а не разделом «Экрана»: OSD сообщают про заряд, режимы, микрофон,
/// тачпад — к экрану как к устройству это отношения не имеет.
///
/// Выключать по отдельности можно только уведомления клавиш-фиксаторов. Остальные плашки —
/// следствие осознанного действия (переключил режим, нажал «В дорогу»), а Caps Lock human
/// задевает случайно, в том числе посреди игры.
/// </summary>
public sealed class OsdTab : SettingsPane
{
    // мс; «2,8 с» — исторический дефолт, поэтому он в списке и подписан как обычное значение
    private static readonly int[] Durations = [1000, 1500, 2000, 2800, 4000, 6000];

    public OsdTab(SettingsToolkit ui, AppConfig cfg, SettingsActions act, Action rebuild) : base(ui)
    {
        ui.AddHeader(this, "settings.tab.osd", "settings.osd.sub");

        ui.AddGroup(this, "settings.osd.look");

        var picker = new OsdPositionPicker(ui, cfg.OsdPosition, p => act.SetOsdPosition(p));
        ui.AddRow(this, "settings.osd.position", "settings.osd.position.desc", picker);

        ui.AddRow(this, "settings.osd.duration", "settings.osd.duration.desc",
            DurationCombo(ui, cfg.OsdDurationMs, act.SetOsdDuration));

        // ширину меряем сами: AutoSize у кнопки срабатывает позже, чем карточка считает
        // раскладку, и кнопка выходит микроскопической (та же грабля, что в DisplayTab)
        var preview = ui.LinkButton("settings.osd.preview.btn", act.PreviewOsd);
        preview.AutoSize = false;
        preview.Width = TextRenderer.MeasureText(Loc.T("settings.osd.preview.btn"), ui.CtlFont).Width + ui.Sc(28);
        preview.Height = ui.Sc(30);
        ui.AddRow(this, "settings.osd.preview", "settings.osd.preview.desc", preview);

        ui.AddGroup(this, "settings.osd.locks");
        ui.AddNote(this, "settings.osd.locks.note");

        AddLock(ui, act, cfg, LockOsd.CapsLock, "settings.osd.lock.caps");
        AddLock(ui, act, cfg, LockOsd.NumLock, "settings.osd.lock.num");
        AddLock(ui, act, cfg, LockOsd.FnLock, "settings.osd.lock.fn");
        AddLock(ui, act, cfg, LockOsd.WinKeyLock, "settings.osd.lock.winkey");

        _ = rebuild; // вкладка перестраивается только по смене языка/темы — своих зависимостей нет
    }

    private void AddLock(SettingsToolkit ui, SettingsActions act, AppConfig cfg, LockOsd key, string titleKey)
        => ui.AddRow(this, titleKey, titleKey + ".desc",
            ui.Toggle(!cfg.HiddenLockOsd.Contains(key), on => act.SetLockOsd(key, on)));

    /// <summary>Длительность в секундах — человек думает в них, а не в миллисекундах.
    /// Значение из config.json вне списка (правили руками) не теряется: добавляем его как есть.</summary>
    private static ComboBox DurationCombo(SettingsToolkit ui, int current, Action<int> apply)
    {
        int[] items = Durations.Contains(current) ? Durations : [.. Durations.Append(current).Order()];
        string[] names = [.. items.Select(ms => Loc.T("settings.osd.duration.sec", ms / 1000.0))];
        int idx = Array.IndexOf(items, current);
        return ui.Combo(names, idx < 0 ? Array.IndexOf(items, 2800) : idx,
                        i => apply(items[i]), ui.Sc(150));
    }
}

/// <summary>
/// Выбор позиции OSD: девять клеток сеткой, рисуем сами (как NavStrip — без картинок и шрифтов
/// со значками). Выбранная клетка залита акцентом, наведённая подсвечена.
///
/// Сетка вместо выпадающего списка сознательно: «снизу слева» списком читается медленнее, чем
/// видно глазом, а выбор тут пространственный.
/// </summary>
public sealed class OsdPositionPicker : Control
{
    private readonly SettingsToolkit _ui;
    private readonly Action<OsdPosition> _changed;
    private OsdPosition _value;
    private int _hover = -1;

    public OsdPositionPicker(SettingsToolkit ui, OsdPosition value, Action<OsdPosition> changed)
    {
        _ui = ui; _value = value; _changed = changed;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        Size = new Size(ui.Sc(CellW * 3 + Gap * 4), ui.Sc(CellH * 3 + Gap * 4));
        BackColor = ui.T.Card;
        AccessibleRole = AccessibleRole.ComboBox;
        UpdateAccessibleName();
    }

    private const int CellW = 26, CellH = 18, Gap = 4;

    private Rectangle CellRect(int i)
    {
        int g = _ui.Sc(Gap), w = _ui.Sc(CellW), h = _ui.Sc(CellH);
        return new Rectangle(g + (i % 3) * (w + g), g + (i / 3) * (h + g), w, h);
    }

    private int HitTest(Point p)
    {
        for (int i = 0; i < 9; i++) if (CellRect(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = HitTest(e.Location);
        if (h != _hover) { _hover = h; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover != -1) { _hover = -1; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int i = HitTest(e.Location);
        if (i >= 0) Select((OsdPosition)i);
        Focus();
        base.OnMouseDown(e);
    }

    // стрелки нужны самому контролу — иначе WinForms уводит фокус на соседний
    protected override bool IsInputKey(Keys keyData)
        => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int i = (int)_value, col = i % 3, row = i / 3;
        switch (e.KeyCode)
        {
            case Keys.Left: col = Math.Max(0, col - 1); break;
            case Keys.Right: col = Math.Min(2, col + 1); break;
            case Keys.Up: row = Math.Max(0, row - 1); break;
            case Keys.Down: row = Math.Min(2, row + 1); break;
            default: base.OnKeyDown(e); return;
        }
        Select((OsdPosition)(row * 3 + col));
        e.Handled = true;
    }

    private void Select(OsdPosition p)
    {
        if (p == _value) return;
        _value = p;
        UpdateAccessibleName();
        Invalidate();
        _changed(p);
    }

    private void UpdateAccessibleName() => AccessibleName = Loc.T("settings.osd.position") + ": " +
        Loc.T("settings.osd.position." + _value.ToString().ToLowerInvariant());

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        for (int i = 0; i < 9; i++)
        {
            var r = CellRect(i);
            bool sel = i == (int)_value;
            using var fill = new SolidBrush(sel ? _ui.T.Accent
                : i == _hover ? _ui.T.Sel : _ui.T.Field);
            g.FillRectangle(fill, r);

            // выбранная клетка несёт миниатюру карточки OSD — чтобы было видно, что
            // настраивается положение плашки, а не абстрактный «угол»
            if (sel)
            {
                int pad = Math.Max(2, r.Height / 4);
                using var card = new SolidBrush(_ui.T.Dark ? Color.FromArgb(0, 45, 74) : Color.White);
                g.FillRectangle(card, Rectangle.Inflate(r, -pad, -pad));
            }
            else
            {
                using var pen = new Pen(_ui.T.Border);
                g.DrawRectangle(pen, r);
            }
        }

        if (Focused)
        {
            using var focus = new Pen(_ui.T.Accent) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
            g.DrawRectangle(focus, 0, 0, Width - 1, Height - 1);
        }
    }
}
