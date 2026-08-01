using System.Drawing.Drawing2D;
using XiControl.Localization;

namespace XiControl.Ui.Settings;

/// <summary>Глифы навигации (рисуем сами — без иконочных шрифтов и ресурсов).</summary>
public enum NavGlyph { General, Features, Battery, Display, Touchpad, Perf, Keys, Api, About }

/// <summary>
/// Левая навигация окна настроек (кастомная отрисовка): подсветка hover/выбора,
/// акцентная полоска, «О программе» прижата вниз. Доступна с клавиатуры (Фаза 6.3):
/// Tab фокусирует полосу, стрелки переключают вкладку сразу (как в Настройках Windows).
/// </summary>
public sealed class NavStrip : Panel
{
    private readonly SettingsToolkit _ui;
    public (string key, NavGlyph glyph)[] Tabs = [];
    public Action<int>? SelectedChanged;
    private int _hover = -1;
    private int _selected;

    /// <summary>Активная вкладка; заодно обновляет имя для экранного диктора.</summary>
    public int Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            if (value >= 0 && value < Tabs.Length) AccessibleName = Loc.T(Tabs[value].key);
        }
    }

    public NavStrip(SettingsToolkit ui)
    {
        _ui = ui;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.PageTabList;
    }

    // стрелки нужны самому контролу — иначе WinForms уводит фокус на соседний контрол
    protected override bool IsInputKey(Keys keyData)
        => keyData is Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Tabs.Length > 0 && e.KeyCode is Keys.Up or Keys.Down)
        {
            int next = (Selected + (e.KeyCode == Keys.Down ? 1 : -1) + Tabs.Length) % Tabs.Length;
            SelectedChanged?.Invoke(next); // форма выставит Selected и перерисует
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    private int ItemH => _ui.Sc(40);
    private int TopPad => _ui.Sc(12);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int i = HitTest(e.Y);
        if (i != _hover) { _hover = i; Invalidate(); Cursor = i >= 0 ? Cursors.Hand : Cursors.Default; }
        base.OnMouseMove(e);
    }
    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        Focus(); // клик мышью тоже даёт полосе клавиатурный фокус
        int i = HitTest(e.Y);
        if (i >= 0 && i != Selected) SelectedChanged?.Invoke(i);
        base.OnMouseClick(e);
    }

    // «О программе» прижата вниз
    private bool IsBottom(int i) => i == Tabs.Length - 1;
    private int RowY(int i) => IsBottom(i) ? Height - _ui.Sc(12) - ItemH : TopPad + i * (ItemH + _ui.Sc(2));
    private int HitTest(int y)
    {
        for (int i = 0; i < Tabs.Length; i++)
            if (y >= RowY(i) && y < RowY(i) + ItemH) return i;
        return -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(_ui.T.NavBg);
        int pad = _ui.Sc(8);

        for (int i = 0; i < Tabs.Length; i++)
        {
            int y = RowY(i);
            var rect = new Rectangle(pad, y, Width - pad * 2, ItemH);
            bool sel = i == Selected, hov = i == _hover;
            if (sel || hov)
            {
                using var b = new SolidBrush(sel ? _ui.T.Sel : Color.FromArgb(_ui.T.Dark ? 22 : 14, _ui.T.Text));
                using var path = Draw.Rounded(rect, _ui.Sc(5));
                g.FillPath(b, path);
            }
            if (sel)
            {
                using var ab = new SolidBrush(_ui.T.Accent);
                using var bar = Draw.Rounded(new RectangleF(rect.X, rect.Y + ItemH * 0.28f, _ui.Sc(3), ItemH * 0.44f), _ui.Sc(1.5f));
                g.FillPath(ab, bar);
            }
            var gc = sel ? _ui.T.Accent : _ui.T.Text2;
            DrawGlyph(g, Tabs[i].glyph, new RectangleF(rect.X + _ui.Sc(12), rect.Y + (ItemH - _ui.Sc(18)) / 2f, _ui.Sc(18), _ui.Sc(18)), gc);
            TextRenderer.DrawText(g, Loc.T(Tabs[i].key), _ui.TitleFont,
                new Rectangle(rect.X + _ui.Sc(40), rect.Y, rect.Width - _ui.Sc(40), ItemH),
                _ui.T.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            // клавиатурный фокус: пунктирная рамка вокруг активной вкладки
            if (sel && Focused)
            {
                using var fr = new Pen(_ui.T.Text2) { DashStyle = DashStyle.Dash };
                using var fp = Draw.Rounded(new RectangleF(rect.X + 1.5f, rect.Y + 1.5f, rect.Width - 3f, rect.Height - 3f), _ui.Sc(4));
                g.DrawPath(fr, fp);
            }
        }
    }

    private void DrawGlyph(Graphics g, NavGlyph k, RectangleF r, Color c)
    {
        using var pen = new Pen(c, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float x = r.X, y = r.Y, w = r.Width, h = r.Height;
        switch (k)
        {
            case NavGlyph.General:
                Draw.Gear(g, r, c); // общий силуэт с кнопкой панели — не «солнышко» из лучей
                break;
            case NavGlyph.Features:
                // два «ползунка» на рельсах — набор включаемых функций
                g.DrawLine(pen, x + w * 0.16f, y + h * 0.34f, x + w * 0.84f, y + h * 0.34f);
                g.DrawLine(pen, x + w * 0.16f, y + h * 0.66f, x + w * 0.84f, y + h * 0.66f);
                using (var knob = new SolidBrush(c))
                {
                    float kr = w * 0.11f;
                    g.FillEllipse(knob, x + w * 0.64f - kr, y + h * 0.34f - kr, kr * 2, kr * 2); // верхний — справа
                    g.FillEllipse(knob, x + w * 0.32f - kr, y + h * 0.66f - kr, kr * 2, kr * 2); // нижний — слева
                }
                break;
            case NavGlyph.Battery:
                g.DrawRectangle(pen, x + w * 0.08f, y + h * 0.3f, w * 0.72f, h * 0.4f);
                g.DrawLine(pen, x + w * 0.86f, y + h * 0.42f, x + w * 0.86f, y + h * 0.58f);
                g.DrawLines(pen, [new PointF(x + w * 0.42f, y + h * 0.36f), new PointF(x + w * 0.32f, y + h * 0.52f), new PointF(x + w * 0.46f, y + h * 0.52f), new PointF(x + w * 0.36f, y + h * 0.66f)]);
                break;
            case NavGlyph.Display:
                g.DrawRectangle(pen, x + w * 0.1f, y + h * 0.2f, w * 0.8f, h * 0.5f);
                g.DrawLine(pen, x + w * 0.35f, y + h * 0.86f, x + w * 0.65f, y + h * 0.86f);
                g.DrawLine(pen, x + w * 0.5f, y + h * 0.7f, x + w * 0.5f, y + h * 0.86f);
                break;
            case NavGlyph.Touchpad:
                // прямоугольник панели + полоска у нижнего края — та самая мёртвая зона
                g.DrawRectangle(pen, x + w * 0.12f, y + h * 0.22f, w * 0.76f, h * 0.56f);
                using (var zone = new SolidBrush(Color.FromArgb(90, c)))
                    g.FillRectangle(zone, x + w * 0.12f, y + h * 0.64f, w * 0.76f, h * 0.14f);
                break;
            case NavGlyph.Perf:
                g.DrawArc(pen, x + w * 0.12f, y + h * 0.2f, w * 0.76f, h * 0.76f, 180, 180);
                g.DrawLine(pen, x + w / 2f, y + h * 0.58f, x + w * 0.72f, y + h * 0.32f);
                break;
            case NavGlyph.Keys:
                g.DrawRectangle(pen, x + w * 0.08f, y + h * 0.28f, w * 0.84f, h * 0.44f);
                for (int d = 0; d < 4; d++) g.DrawLine(pen, x + w * (0.22f + d * 0.18f), y + h * 0.42f, x + w * (0.22f + d * 0.18f), y + h * 0.42f);
                g.DrawLine(pen, x + w * 0.32f, y + h * 0.58f, x + w * 0.68f, y + h * 0.58f);
                break;
            case NavGlyph.Api:
                // «глобус»: окружность + экватор + меридиан — сетевой доступ
                g.DrawEllipse(pen, x + w * 0.14f, y + h * 0.14f, w * 0.72f, h * 0.72f);
                g.DrawLine(pen, x + w * 0.14f, y + h * 0.5f, x + w * 0.86f, y + h * 0.5f);
                g.DrawEllipse(pen, x + w * 0.35f, y + h * 0.14f, w * 0.3f, h * 0.72f);
                break;
            case NavGlyph.About:
                g.DrawEllipse(pen, x + w * 0.15f, y + h * 0.15f, w * 0.7f, h * 0.7f);
                g.DrawLine(pen, x + w / 2f, y + h * 0.45f, x + w / 2f, y + h * 0.68f);
                using (var dot = new SolidBrush(c))
                    g.FillEllipse(dot, x + w / 2f - _ui.Sc(1), y + h * 0.3f, _ui.Sc(2.2f), _ui.Sc(2.2f));
                break;
        }
    }
}
