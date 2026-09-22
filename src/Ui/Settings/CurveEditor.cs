using System.Drawing.Drawing2D;
using XiControl.Config;
using XiControl.Localization;
using XiControl.SystemIntegration;

namespace XiControl.Ui.Settings;

/// <summary>
/// График кривой авто-яркости, который можно править: тащить точки мышью (XIC-33), добавлять
/// двойным кликом и убирать правой кнопкой (XIC-66). Правится кривая ОДНОГО источника — того,
/// что выбран сегментом над графиком; вторая рисуется бледной линией, чтобы было с чем
/// сравнивать, но схватить её нельзя: две кривые под одной мышью — гарантированные промахи.
///
/// Правила правки (зажим между соседями, пределы числа точек) живут в <see cref="CurveEdit"/>
/// и покрыты тестами — здесь только пиксели, попадания и клавиши.
///
/// <b>Почему линии больше не «срезаны» лимитом яркости.</b> Раньше график рисовал эффективное
/// поведение: кривая упиралась в лимит XIC-29. Для редактора это не годится — точка на 100%
/// при лимите 60% рисовалась бы на 60%, и мышь брала бы её не там, где она есть. Теперь кривые
/// показывают намерение, а лимит — отдельная пунктирная черта: всё выше неё экран не покажет.
/// </summary>
internal sealed class CurveEditor : Panel
{
    private const int Samples = 64;
    private const int HitPx = 9;        // радиус попадания по точке
    private const double LuxStep = 0.1; // шаг перемещения по свету с клавиатуры (в лог-шкале)

    // батарейная кривая — оранжевым (Material Orange 500, как разряд в «Мониторе»)
    private static readonly Color BatteryColor = Color.FromArgb(0xFF, 0x98, 0x00);
    private static readonly float[] Decades = [1, 10, 100, 1000, 10_000];
    private static readonly int[] Percents = [0, 50, 100];

    private readonly SettingsToolkit _ui;
    private readonly AppConfig _cfg;
    private readonly SettingsActions _act;

    private bool _ac;                    // какую из двух кривых правим
    private List<BrightnessPoint> _pts;  // рабочая копия правимой кривой
    private int _drag = -1;              // точка под мышью, пока её тащат
    private int _sel = -1;               // выбранная точка (клавиатура)
    private bool _dirty;                 // есть несохранённая правка

    public CurveEditor(SettingsToolkit ui, AppConfig cfg, SettingsActions act, bool ac)
    {
        _ui = ui;
        _cfg = cfg;
        _act = act;
        _ac = ac;
        _pts = [.. act.BrightnessCurvePoints(ac)];

        DoubleBuffered = true;
        Width = ui.RowW;
        Height = ui.Sc(150);
        BackColor = ui.T.Card;
        Margin = new Padding(0, 0, 0, ui.Sc(4));
        Region = new Region(Draw.Rounded(new RectangleF(0, 0, Width, Height), ui.Sc(6)));
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.Diagram;
        AccessibleName = Loc.T(ac ? "settings.bright.curve.ac" : "settings.bright.curve.battery");
        AccessibleDescription = Loc.T("settings.bright.curve.edit");
    }

    /// <summary>Переключить правимую кривую (сегмент над графиком). Меняется только этот
    /// контрол, поэтому вкладку не пересобираем: пересборка уносит фокус и прокрутку, а
    /// рисовать надо ровно другую линию.</summary>
    public void SetSource(bool ac)
    {
        if (_dirty) Commit(); // недосохранённую правку не теряем
        _ac = ac;
        _pts = [.. _act.BrightnessCurvePoints(ac)];
        _drag = _sel = -1;
        AccessibleName = Loc.T(ac ? "settings.bright.curve.ac" : "settings.bright.curve.battery");
        Invalidate();
    }

    // ---- Геометрия: пиксели ↔ (люксы, проценты) ----

    private Rectangle Plot
    {
        get
        {
            int padL = _ui.Sc(30), padR = _ui.Sc(14), padT = _ui.Sc(10), padB = _ui.Sc(20);
            return new Rectangle(padL, padT, Width - padL - padR, Height - padT - padB);
        }
    }

    private static double MaxLog => Math.Log10(1 + CurveEdit.MaxLux);

    private float X(float lux) => Plot.Left + (float)(Math.Log10(1 + Math.Max(0, lux)) / MaxLog) * Plot.Width;

    private float Y(int percent) => Plot.Bottom - percent / 100f * Plot.Height;

    private float LuxAt(int x) =>
        (float)Math.Round(Math.Pow(10, MaxLog * Math.Clamp((x - Plot.Left) / (double)Plot.Width, 0, 1)) - 1, 1);

    private int PercentAt(int y) =>
        (int)Math.Round(Math.Clamp((Plot.Bottom - y) / (double)Plot.Height, 0, 1) * 100);

    // порог различимости соседних точек — тот же гистерезис, которым меряет обучение:
    // поставленная ближе точка была бы съедена первой же правкой яркости
    private double Gap => Math.Max(0.01, _cfg.AutoBrightnessHysteresis);

    private int HitTest(Point at)
    {
        int r = _ui.Sc(HitPx);
        for (int i = 0; i < _pts.Count; i++)
        {
            float dx = X(_pts[i].Lux) - at.X, dy = Y(_pts[i].Percent) - at.Y;
            if (dx * dx + dy * dy <= r * r) return i;
        }
        return -1;
    }

    // ---- Мышь ----

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        int hit = HitTest(e.Location);
        if (e.Button == MouseButtons.Right && hit >= 0 && CurveEdit.Remove(_pts, hit) is { } left)
        {
            _pts = left;
            _sel = -1;
            Commit();
        }
        else if (e.Button == MouseButtons.Left && hit >= 0)
        {
            _drag = _sel = hit;
        }
        Invalidate();
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag >= 0)
        {
            _pts = CurveEdit.Move(_pts, _drag, LuxAt(e.X), PercentAt(e.Y), Gap);
            _dirty = true;
            Invalidate();
        }
        else
        {
            Cursor = HitTest(e.Location) >= 0 ? Cursors.SizeAll : Cursors.Default;
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_drag >= 0)
        {
            _drag = -1;
            Commit();
        }
        base.OnMouseUp(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        // двойной клик по пустому месту — новая точка ровно туда, куда ткнули
        if (e.Button == MouseButtons.Left && HitTest(e.Location) < 0)
            Add(LuxAt(e.X), PercentAt(e.Y));
        base.OnMouseDoubleClick(e);
    }

    // ---- Клавиатура: стрелки двигают, Delete/Insert меняют число точек ----

    protected override bool IsInputKey(Keys keyData)
        => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down
            or Keys.Delete or Keys.Insert || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_pts.Count == 0) { base.OnKeyDown(e); return; }
        if (_sel < 0 || _sel >= _pts.Count) _sel = 0;

        switch (e.KeyCode)
        {
            case Keys.Left when e.Control: Nudge(-LuxStep, 0); break;
            case Keys.Right when e.Control: Nudge(LuxStep, 0); break;
            case Keys.Left: _sel = Math.Max(0, _sel - 1); break;
            case Keys.Right: _sel = Math.Min(_pts.Count - 1, _sel + 1); break;
            case Keys.Up: Nudge(0, e.Shift ? 5 : 1); break;
            case Keys.Down: Nudge(0, e.Shift ? -5 : -1); break;
            case Keys.Delete:
                if (CurveEdit.Remove(_pts, _sel) is { } left)
                {
                    _pts = left;
                    _sel = Math.Min(_sel, _pts.Count - 1);
                    Commit();
                }
                break;
            case Keys.Insert: InsertBeside(); break;
            default: base.OnKeyDown(e); return;
        }
        e.Handled = true;
        Invalidate();
    }

    // сохраняем по отпусканию клавиши: зажатая стрелка иначе писала бы конфиг на каждый шаг
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (_dirty) Commit();
        base.OnKeyUp(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }

    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    private void Nudge(double dLog, int dPercent)
    {
        var p = _pts[_sel];
        float lux = dLog == 0 ? p.Lux : (float)Math.Max(0, Math.Pow(10, Math.Log10(1 + p.Lux) + dLog) - 1);
        _pts = CurveEdit.Move(_pts, _sel, lux, p.Percent + dPercent, Gap);
        _dirty = true;
    }

    // Insert ставит точку посередине (в лог-шкале) между выбранной и соседней справа —
    // а у последней точки слева: «вставить» всегда должно что-то делать
    private void InsertBeside()
    {
        int next = _sel < _pts.Count - 1 ? _sel + 1 : _sel - 1;
        if (next < 0) return;
        double mid = (Math.Log10(1 + _pts[_sel].Lux) + Math.Log10(1 + _pts[next].Lux)) / 2;
        float lux = (float)Math.Max(0, Math.Pow(10, mid) - 1);
        Add(lux, new BrightnessCurve([.. _pts]).Predict(lux));
    }

    private void Add(float lux, int percent)
    {
        if (CurveEdit.Add(_pts, lux, percent, Gap) is not { } grown) return; // места нет — молча ничего
        _pts = grown;
        _sel = _pts.FindIndex(p => Math.Abs(p.Lux - lux) < 0.05f);
        Commit();
        Invalidate();
    }

    private void Commit()
    {
        _act.SetBrightnessCurve(_ac, _pts);
        _dirty = false;
    }

    // ---- Отрисовка ----

    protected override void OnPaint(PaintEventArgs e)
    {
        // пока точку тащат (или правку ещё не сохранили) — показываем СВОЮ копию: снимок из
        // конфига отстаёт и дёргал бы точку назад из-под курсора
        if (_drag < 0 && !_dirty) _pts = [.. _act.BrightnessCurvePoints(_ac)];

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var plot = Plot;

        using var grid = new Pen(_ui.T.Border);
        using var dim = new SolidBrush(_ui.T.Text2);
        foreach (float d in Decades)
        {
            float x = X(d);
            g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
            g.DrawString(d >= 1000 ? $"{d / 1000:0}k" : $"{d:0}", _ui.DescFont, dim, x - _ui.Sc(7), plot.Bottom + _ui.Sc(3));
        }
        foreach (int p in Percents)
        {
            float y = Y(p);
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            g.DrawString($"{p}", _ui.DescFont, dim, _ui.Sc(4), y - _ui.Sc(7));
        }

        var mine = _ac ? _ui.T.Accent : BatteryColor;
        var other = _ac ? BatteryColor : _ui.T.Accent;
        DrawCurve(g, [.. _act.BrightnessCurvePoints(!_ac)], Color.FromArgb(70, other), dots: false);
        DrawCurve(g, _pts, mine, dots: true);

        // лимит яркости своего источника (XIC-29) — потолок, выше которого экран не пойдёт
        int cap = _cfg.BrightnessCapEnabled
            ? Math.Clamp(_ac ? _cfg.BrightnessCapAc : _cfg.BrightnessCapBattery, 10, 100)
            : 100;
        if (cap < 100)
        {
            using var capPen = new Pen(_ui.T.Text2) { DashStyle = DashStyle.Dot };
            float y = Y(cap);
            g.DrawLine(capPen, plot.Left, y, plot.Right, y);
            g.DrawString(Loc.T("settings.bright.curve.cap", cap), _ui.DescFont, dim, plot.Right - _ui.Sc(78), y - _ui.Sc(13));
        }

        // легенда: слева вверху — там пусто, при малых люксах обе кривые прижаты к низу
        using var mineBrush = new SolidBrush(mine);
        int lx = plot.Left + _ui.Sc(8), ly = plot.Top + _ui.Sc(2);
        g.FillRectangle(mineBrush, lx, ly + _ui.Sc(4), _ui.Sc(10), _ui.Sc(3));
        g.DrawString(Loc.T(_ac ? "settings.bright.curve.ac" : "settings.bright.curve.battery"),
            _ui.DescFont, dim, lx + _ui.Sc(14), ly);

        // маркер текущей освещённости: пунктир всегда, точка — только если правим ту кривую,
        // что сейчас рулит экраном (иначе точка врала бы про чужое предсказание)
        float now = _act.CurrentLux();
        if (!float.IsNaN(now) && _pts.Count > 0)
        {
            using var cur = new Pen(_ui.T.Text2) { DashStyle = DashStyle.Dash };
            float cx = X(now);
            g.DrawLine(cur, cx, plot.Top, cx, plot.Bottom);
            if (_ac == PowerLine.IsOnline())
            {
                using var mark = new SolidBrush(_ui.T.Text);
                float my = Y(Math.Min(new BrightnessCurve([.. _pts]).Predict(now), cap));
                g.FillEllipse(mark, cx - _ui.Sc(3), my - _ui.Sc(3), _ui.Sc(6), _ui.Sc(6));
            }
        }

        _ui.PaintCardBorder(g, Width, Height);
        base.OnPaint(e);
    }

    private void DrawCurve(Graphics g, List<BrightnessPoint> pts, Color color, bool dots)
    {
        if (pts.Count == 0) return;
        var curve = new BrightnessCurve([.. pts]); // копия-снимок: обучение может идти параллельно

        var line = new PointF[Samples + 1];
        for (int i = 0; i <= Samples; i++)
        {
            float lux = (float)(Math.Pow(10, MaxLog * i / Samples) - 1);
            line[i] = new PointF(X(lux), Y(curve.Predict(lux)));
        }
        using var pen = new Pen(color, _ui.Sc(2));
        g.DrawLines(pen, line);
        if (!dots) return;

        using var dot = new SolidBrush(color);
        using var ring = new Pen(_ui.T.Card, _ui.Sc(2));     // ободок цветом карточки: точки не сливаются с линией
        using var selPen = new Pen(_ui.T.Text, _ui.Sc(1));
        for (int i = 0; i < pts.Count; i++)
        {
            int r = _ui.Sc(i == _sel ? 5 : 4);
            var box = new RectangleF(X(pts[i].Lux) - r, Y(pts[i].Percent) - r, r * 2, r * 2);
            g.FillEllipse(dot, box);
            g.DrawEllipse(ring, box);
            if (i == _sel && Focused)
                g.DrawEllipse(selPen, RectangleF.Inflate(box, _ui.Sc(3), _ui.Sc(3)));
        }
    }
}
