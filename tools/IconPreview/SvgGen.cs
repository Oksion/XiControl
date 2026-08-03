using System.Globalization;
using System.Text;

namespace XiControl.Ui; // как Icons.cs: генератор живёт рядом с геометрией иконок

/// <summary>
/// Генерирует SVG-версии иконок (та же геометрия, что в Icons.cs) и собирает лист,
/// как в PNG-превью. Координаты считаются кодом — без ручной арифметики.
/// </summary>
internal static class SvgGen
{
    private const string green = "#34C759", blue = "#5AAAFF", orange = "#FF9500", amber = "#E6BE46",
                 gray = "#969699", red = "#FF5A5A", white = "#F0F0F0", batt = "#EBEBEB",
                 card = "#1C1C1E", txt = "#EDEDED";

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static double fx(double f) => 12 + f * 24;
    private static double fy(double f) => 12 + f * 24;

    private static string PathS(string d, string col, double sw = 2) => $"<path d=\"{d}\" fill=\"none\" stroke=\"{col}\" stroke-width=\"{N(sw)}\"/>";
    private static string Line(double x1, double y1, double x2, double y2, string col, double sw = 2) =>
        $"<line x1=\"{N(x1)}\" y1=\"{N(y1)}\" x2=\"{N(x2)}\" y2=\"{N(y2)}\" stroke=\"{col}\" stroke-width=\"{N(sw)}\"/>";
    private static string Circle(double x, double y, double r, string fill) => $"<circle cx=\"{N(x)}\" cy=\"{N(y)}\" r=\"{N(r)}\" fill=\"{fill}\"/>";
    private static string Rect(double x, double y, double w, double h, double rx, string fill, string? stroke = null, double sw = 2)
    {
        string s = stroke == null ? "" : $" stroke=\"{stroke}\" stroke-width=\"{N(sw)}\"";
        return $"<rect x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(w)}\" height=\"{N(h)}\" rx=\"{N(rx)}\" fill=\"{fill}\"{s}/>";
    }
    private static string Arc(double cX, double cY, double r, double start, double sweep, string col, double sw = 2)
    {
        double a = start * Math.PI / 180, b = (start + sweep) * Math.PI / 180;
        double sx = cX + Math.Cos(a) * r, sy = cY + Math.Sin(a) * r, ex = cX + Math.Cos(b) * r, ey = cY + Math.Sin(b) * r;
        int large = Math.Abs(sweep) > 180 ? 1 : 0, sweepF = sweep > 0 ? 1 : 0;
        return $"<path d=\"M{N(sx)},{N(sy)} A{N(r)},{N(r)} 0 {large} {sweepF} {N(ex)},{N(ey)}\" fill=\"none\" stroke=\"{col}\" stroke-width=\"{N(sw)}\"/>";
    }
    private static string Bolt(double bcx, double bcy, double bw, double bh, string fill)
    {
        string pts = $"{N(bcx + 0.10 * bw)},{N(bcy - 0.42 * bh)} {N(bcx - 0.22 * bw)},{N(bcy + 0.05 * bh)} {N(bcx - 0.03 * bw)},{N(bcy + 0.05 * bh)} {N(bcx - 0.10 * bw)},{N(bcy + 0.42 * bh)} {N(bcx + 0.22 * bw)},{N(bcy - 0.05 * bh)} {N(bcx + 0.03 * bw)},{N(bcy - 0.05 * bh)}";
        return $"<polygon points=\"{pts}\" fill=\"{fill}\"/>";
    }

    private static string Leaf(string col) =>
        PathS($"M{N(fx(0.36))},{N(fy(-0.32))} C{N(fx(0))},{N(fy(-0.30))} {N(fx(-0.34))},{N(fy(-0.04))} {N(fx(-0.30))},{N(fy(0.30))} " +
              $"C{N(fx(-0.12))},{N(fy(0.44))} {N(fx(0.34))},{N(fy(0.22))} {N(fx(0.36))},{N(fy(-0.32))} Z", col) +
        PathS($"M{N(fx(0.26))},{N(fy(-0.22))} C{N(fx(0.02))},{N(fy(-0.02))} {N(fx(-0.12))},{N(fy(0.10))} {N(fx(-0.22))},{N(fy(0.24))}", col);

    private static string Gauge(string col)
    {
        double size = 0.82 * 24, rrX = (24 - size) / 2, rrY = (24 - size) / 2 + 0.04 * 24;
        double gcx = rrX + size / 2, gcy = rrY + size / 2, r = size / 2;
        double nx = gcx + Math.Cos(300 * Math.PI / 180) * size * 0.36, ny = gcy + Math.Sin(300 * Math.PI / 180) * size * 0.36;
        return Arc(gcx, gcy, r, 140, 260, col)
             + Line(gcx, gcy, nx, ny, col)
             + Circle(gcx, gcy, 1.008 * 1.2, col);
    }

    private static string Toggles(string col)
    {
        double pillW = 0.74 * 24, pillH = 0.36 * 24, gap = 0.12 * 24, x = (24 - pillW) / 2, total = pillH * 2 + gap, y0 = (24 - total) / 2;
        double sw = pillH / 6, inset = pillH * 0.20, td = pillH - 2 * inset, rr = td / 2;
        var sb = new StringBuilder();
        sb.Append(Rect(x, y0, pillW, pillH, pillH / 2, "none", col, sw));
        sb.Append(Circle(x + pillW - inset - td + rr, y0 + inset + rr, rr, col));
        sb.Append(Rect(x, y0 + pillH + gap, pillW, pillH, pillH / 2, "none", col, sw));
        sb.Append(Circle(x + inset + rr, y0 + pillH + gap + inset + rr, rr, col));
        return sb.ToString();
    }

    private static string Battery(string accent, double fill)
    {
        double w = 0.82 * 24, h = 0.46 * 24, bx = (24 - w) / 2, by = (24 - h) / 2, bw = w - 0.09 * 24, rx = h * 0.16;
        double tx = bx + bw + 0.02 * 24, ty = by + h * 0.3, tw = 0.05 * 24, th = 0.4 * h;
        double pad = h * 0.16, ix = bx + pad, iy = by + pad, iw = (bw - 2 * pad) * fill, ih = h - 2 * pad;
        var sb = new StringBuilder();
        sb.Append(Rect(bx, by, bw, h, rx, "none", batt));
        sb.Append(Rect(tx, ty, tw, th, 0, batt));
        if (fill > 0) sb.Append(Rect(ix, iy, iw, ih, pad * 0.6, accent));
        return sb.ToString();
    }

    private static string BoltOverlay()
    {
        double cx = 12, cy = 12, w = 24, h = 24;
        string pts = $"{N(cx + 0.05 * w)},{N(cy - 0.20 * h)} {N(cx - 0.12 * w)},{N(cy + 0.03 * h)} {N(cx - 0.017 * w)},{N(cy + 0.03 * h)} {N(cx - 0.05 * w)},{N(cy + 0.20 * h)} {N(cx + 0.12 * w)},{N(cy - 0.03 * h)} {N(cx + 0.017 * w)},{N(cy - 0.03 * h)}";
        return $"<polygon points=\"{pts}\" fill=\"{white}\" stroke=\"{card}\" stroke-width=\"1\"/>";
    }

    private static string LeafOverlay()
    {
        string d = $"M{N(fx(-0.09))},{N(fy(0.11))} C{N(fx(-0.16))},{N(fy(-0.05))} {N(fx(-0.03))},{N(fy(-0.17))} {N(fx(0.13))},{N(fy(-0.14))} " +
                   $"C{N(fx(0.08))},{N(fy(0.03))} {N(fx(-0.017))},{N(fy(0.11))} {N(fx(-0.09))},{N(fy(0.11))}";
        return PathS(d, card, 1.3) + PathS(d, white, 0.9);
    }

    private static string Slash() => Line(0.18 * 24, 0.20 * 24, 24 - 0.18 * 24, 24 - 0.20 * 24, red, 1.4);

    private static string Mic(string col, bool muted)
    {
        double capW = 0.28 * 24, capTop = 0.12 * 24, capH = 0.42 * 24, cx0 = 12 - capW / 2;
        double arcR = 0.24 * 24, arcX = 12 - arcR, arcY = capTop + capH * 0.28, arcCy = arcY + arcR;
        double baseY = 0.88 * 24, standTop = arcY + arcR * 2 - arcR * 0.2;
        var sb = new StringBuilder();
        sb.Append(Rect(cx0, capTop, capW, capH, capW / 2, col));
        sb.Append(Arc(12, arcCy, arcR, 20, 140, col));
        sb.Append(Line(12, standTop, 12, baseY, col));
        sb.Append(Line(12 - 0.15 * 24, baseY, 12 + 0.15 * 24, baseY, col));
        if (muted) sb.Append(Line(0.14 * 24, 0.14 * 24, 24 - 0.14 * 24, 24 - 0.14 * 24, red, 1.4));
        return sb.ToString();
    }

    private static string Keyboard(string col)
    {
        double bx = 12 - 0.40 * 24, by = 0.52 * 24, bw = 0.80 * 24, bh = 0.32 * 24, rx = bh * 0.06;
        double sunR = 0.14 * 24, scy = by - 0.05 * 24;
        var sb = new StringBuilder();
        sb.Append(Rect(bx, by, bw, bh, rx, "none", col));
        sb.Append(Line(bx + bw * 0.16, by + bh * 0.36, bx + bw - bw * 0.16, by + bh * 0.36, col));
        sb.Append(Line(bx + bw * 0.30, by + bh * 0.68, bx + bw - bw * 0.30, by + bh * 0.68, col));
        sb.Append(Arc(12, scy, sunR, 180, 180, col));
        sb.Append(Line(12 - sunR, scy, 12 + sunR, scy, col));
        double ri = sunR * 1.5, ro = sunR * 2.3;
        for (int deg = 205; deg <= 335; deg += 32)
        {
            double a = deg * Math.PI / 180, dx = Math.Cos(a), dy = Math.Sin(a);
            sb.Append(Line(12 + dx * ri, scy + dy * ri, 12 + dx * ro, scy + dy * ro, col));
        }
        return sb.ToString();
    }

    private static (string label, string file, string inner)[] Icons() => new[]
    {
        ("Тихий", "mode-quiet",         Leaf(green)),
        ("Авто", "mode-auto",           Gauge(blue)),
        ("Турбо", "mode-turbo",         Bolt(12, 12, 24, 24, orange)),
        ("Полная", "mode-full",         DoubleBolt(orange)),
        ("База", "app",                 Toggles(white)),
        ("Микрофон", "mic",             Mic(green, false)),
        ("Мик.выкл", "mic-muted",       Mic(gray, true)),
        ("Подсветка", "keyboard-light", Keyboard(blue)),
        ("Зарядка", "battery-charging", Battery(green, 0.8) + BoltOverlay()),
        ("От батареи", "battery",       Battery(amber, 0.6)),
        ("Беречь вкл", "battery-care",  Battery(green, 0.8) + LeafOverlay()),
        ("Беречь выкл", "battery-off",  Battery(gray, 0.95) + Slash()),
    };

    private static string DoubleBolt(string col)
    {
        double bw = 0.6 * 24, off = 0.18 * 24, x = 12 - bw / 2;
        double lcx = (x - off) + bw / 2, rcx = (x + off) + bw / 2;
        return Bolt(lcx, 12, bw, 24, col) + Bolt(rcx, 12, bw, 24, col);
    }

    public static string BuildSheet()
    {
        var icons = Icons();
        int cols = 3, cellW = 180, cellH = 150, iconPx = 66;
        double k = iconPx / 24.0;
        int rows = (icons.Length + cols - 1) / cols;
        int W = cols * cellW, H = rows * cellH;

        var inv = System.Globalization.CultureInfo.InvariantCulture; // SVG требует точку в числах при любой локали
        var sb = new StringBuilder();
        sb.AppendLine(inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {W} {H}\" font-family=\"Segoe UI, sans-serif\">");
        sb.AppendLine(inv, $"<rect width=\"{W}\" height=\"{H}\" fill=\"{card}\"/>");
        for (int i = 0; i < icons.Length; i++)
        {
            int col = i % cols, row = i / cols;
            double tx = col * cellW + (cellW - iconPx) / 2.0, ty = row * cellH + 20;
            sb.AppendLine(inv, $"<g transform=\"translate({N(tx)},{N(ty)}) scale({N(k)})\" stroke-linecap=\"round\" stroke-linejoin=\"round\">{icons[i].inner}</g>");
            sb.AppendLine(inv, $"<text x=\"{col * cellW + cellW / 2.0}\" y=\"{row * cellH + cellH - 22}\" fill=\"{txt}\" font-size=\"13\" text-anchor=\"middle\">{icons[i].label} · {icons[i].file}</text>");
        }
        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
