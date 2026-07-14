using System.Drawing.Drawing2D;

namespace XiControl.Ui;

/// <summary>Общие примитивы рисования.</summary>
public static class Draw
{
    /// <summary>Крестик закрытия окна (единый для панели/монитора): hover — красная плашка.</summary>
    public static void CloseButton(Graphics g, Rectangle r, bool hover)
    {
        if (hover)
        {
            using var b = new SolidBrush(Color.FromArgb(200, 60, 60));
            using var path = Rounded(r, r.Width * 0.23f);
            g.FillPath(b, path);
        }
        using var pen = new Pen(hover ? Color.White : Color.FromArgb(150, 150, 155), 1.8f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
        };
        float m = r.Width * 0.32f;
        g.DrawLine(pen, r.X + m, r.Y + m, r.Right - m, r.Bottom - m);
        g.DrawLine(pen, r.Right - m, r.Y + m, r.X + m, r.Bottom - m);
    }

    /// <summary>Кнопка «Монитор» (мини-график) в стиле крестика: hover — синяя плашка.</summary>
    public static void MonitorButton(Graphics g, Rectangle r, bool hover)
    {
        if (hover)
        {
            using var b = new SolidBrush(Color.FromArgb(60, 120, 190));
            using var path = Rounded(r, r.Width * 0.23f);
            g.FillPath(b, path);
        }
        using var pen = new Pen(hover ? Color.White : Color.FromArgb(150, 150, 155), 1.8f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        float w = r.Width, h = r.Height;
        g.DrawLines(pen,
        [
            new PointF(r.X + w * 0.24f, r.Y + h * 0.68f),
            new PointF(r.X + w * 0.42f, r.Y + h * 0.46f),
            new PointF(r.X + w * 0.58f, r.Y + h * 0.58f),
            new PointF(r.X + w * 0.78f, r.Y + h * 0.30f),
        ]);
    }

    /// <summary>Кнопка «вид» (полный/мини/ватты): большой и малый прямоугольники. Hover — синяя плашка.</summary>
    public static void ViewButton(Graphics g, Rectangle r, bool hover)
    {
        if (hover)
        {
            using var b = new SolidBrush(Color.FromArgb(60, 120, 190));
            using var path = Rounded(r, r.Width * 0.23f);
            g.FillPath(b, path);
        }
        using var pen = new Pen(hover ? Color.White : Color.FromArgb(150, 150, 155), 1.8f)
        {
            LineJoin = LineJoin.Round,
        };
        float w = r.Width, h = r.Height;
        g.DrawRectangle(pen, r.X + w * 0.20f, r.Y + h * 0.24f, w * 0.42f, h * 0.34f);
        g.DrawRectangle(pen, r.X + w * 0.44f, r.Y + h * 0.48f, w * 0.34f, h * 0.28f);
    }

    /// <summary>Скруглённый прямоугольник (путь надо Dispose-ить).</summary>
    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = Math.Max(1f, radius * 2);
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
