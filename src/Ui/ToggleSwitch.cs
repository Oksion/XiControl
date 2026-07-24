using System.Drawing.Drawing2D;

namespace XiControl.Ui;

/// <summary>
/// Тумблер в стиле Windows 11 (для окна настроек). Рисуется сам, без нативного чекбокса,
/// чтобы точно попадать в тему. Живёт только пока окно открыто — таймеров/анимаций нет.
/// </summary>
public sealed class ToggleSwitch : Control
{
    private bool _checked;
    private bool _hover;

    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => _checked;
        set { if (_checked == value) return; _checked = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); }
    }

    /// <summary>Цвет заливки во включённом состоянии.</summary>
    public Color Accent { get; set; } = Color.FromArgb(0, 95, 184);
    /// <summary>Цвет «пятки» включённого тумблера (контраст к Accent).</summary>
    public Color OnKnob { get; set; } = Color.White;
    /// <summary>Цвет обводки/пятки в выключенном состоянии.</summary>
    public Color OffLine { get; set; } = Color.FromArgb(140, 140, 140);

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.CheckButton; // экранный диктор читает как переключатель
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Space || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) { Checked = !Checked; e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    // Enabled=false: клики блокирует сам Control, а «серость» рисуем полупрозрачностью —
    // недоступный тумблер (напр. команда API при выключенной фиче) виден, но явно погашен
    private Color A(Color c) => Enabled ? c : Color.FromArgb(80, c);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        float pad = 0.5f;
        var track = new RectangleF(pad, pad, Width - 1 - pad, Height - 1 - pad);
        float rad = track.Height / 2f;

        using var path = Draw.Rounded(track, rad);
        if (_checked)
        {
            using var b = new SolidBrush(A(_hover && Enabled ? Blend(Accent, Color.White, 0.12f) : Accent));
            g.FillPath(b, path);
        }
        else
        {
            using var pen = new Pen(A(OffLine), 1.2f);
            g.DrawPath(pen, path);
            if (_hover && Enabled)
            {
                using var b = new SolidBrush(Color.FromArgb(28, OffLine));
                g.FillPath(b, path);
            }
        }

        // пятка
        float knob = _checked ? track.Height * 0.62f : track.Height * 0.5f;
        float cy = track.Y + track.Height / 2f;
        float cx = _checked ? track.Right - rad : track.X + rad;
        using var kb = new SolidBrush(A(_checked ? OnKnob : OffLine));
        g.FillEllipse(kb, cx - knob / 2f, cy - knob / 2f, knob, knob);

        // фокус с клавиатуры (Tab) должен быть виден
        if (Focused) ControlPaint.DrawFocusRectangle(g, ClientRectangle);
    }

    private static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t),
        (int)(a.G + (b.G - a.G) * t),
        (int)(a.B + (b.B - a.B) * t));
}
