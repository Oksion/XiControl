using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using XiControl.Ui;

// Пути считаем от корня репозитория (ищем XiControl.sln вверх от каталога сборки),
// чтобы инструмент работал из любого рабочего каталога и на любой машине.
string RepoDir()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XiControl.sln")))
        dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}
string Repo(params string[] parts)
{
    string p = RepoDir();
    foreach (var part in parts) p = Path.Combine(p, part);
    return p;
}
string Out(params string[] parts)                       // как Repo, но создаёт каталог назначения
{
    string p = Repo(parts);
    Directory.CreateDirectory(Path.GetDirectoryName(p)!);
    return p;
}

// Режим "menu": эскиз WinUI-меню без запуска приложения и зависимости от UI-фреймворка.
if (args.Length > 0 && args[0] == "menu")
{
    Bitmap RenderMenu(bool dark, bool modes = false)
    {
        (string text, string? icon, bool active, bool enabled, string? value)[] rows = modes
            ?
            [
                ("节能", SvgIcons.MenuPerfEco, false, true, null),
                ("安静", SvgIcons.MenuPerfQuiet, false, true, null),
                ("智能", SvgIcons.MenuPerfAuto, true, true, null),
                ("均衡", SvgIcons.MenuPerfBalance, false, true, null),
                ("极速", SvgIcons.MenuPerfTurbo, false, true, null),
                ("狂暴", SvgIcons.MenuPerfFull, false, true, null),
            ]
            :
            [
                ("电池保护 (80%)", SvgIcons.MenuBattery, true, true, null),
                ("出行充电 (100%)", SvgIcons.MenuTravel, false, true, null),
                ("猫头鹰模式（保持唤醒）", SvgIcons.MenuOwl, false, true, null),
                ("自动刷新率（120/60 Hz）", SvgIcons.MenuRefreshRate, true, true, null),
                ("监视器", SvgIcons.MenuMonitor, false, true, null),
                ("性能模式", SvgIcons.MenuPerformance, false, true, "智能  ›"),
                (string.Empty, null, false, true, null),
                ("设置…", SvgIcons.MenuSettings, false, true, null),
                (string.Empty, null, false, true, null),
                ("退出", SvgIcons.MenuExit, false, true, null),
            ];

        int width = modes ? 236 : 324;
        const int rowHeight = 44, separatorHeight = 12, pad = 8;
        int height = pad * 2 + rows.Sum(row => row.icon is null ? separatorHeight : rowHeight);
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        Color card = dark ? Color.FromArgb(28, 28, 30) : Color.FromArgb(243, 243, 245);
        Color border = dark ? Color.FromArgb(70, 70, 74) : Color.FromArgb(200, 200, 205);
        Color text = dark ? Color.FromArgb(240, 240, 240) : Color.FromArgb(26, 26, 26);
        Color dim = dark ? Color.FromArgb(170, 170, 175) : Color.FromArgb(95, 95, 95);
        Color selected = dark ? Color.FromArgb(48, 74, 163, 255) : Color.FromArgb(35, 0, 95, 184);
        using (var background = Icons.Rounded(new RectangleF(0.5f, 0.5f, width - 1, height - 1), 14))
        using (var brush = new SolidBrush(card))
        using (var pen = new Pen(border))
        {
            graphics.FillPath(brush, background);
            graphics.DrawPath(pen, background);
        }

        using var font = new Font("Segoe UI", 10f);
        using var valueFont = new Font("Segoe UI Semibold", 9.5f);
        using var textBrush = new SolidBrush(text);
        using var dimBrush = new SolidBrush(dim);
        using var selectedBrush = new SolidBrush(selected);
        using var linePen = new Pen(border);
        using var left = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };
        using var right = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
        int y = pad;
        foreach (var row in rows)
        {
            if (row.icon is null)
            {
                graphics.DrawLine(linePen, 46, y + separatorHeight / 2f, width - 12, y + separatorHeight / 2f);
                y += separatorHeight;
                continue;
            }
            var bounds = new RectangleF(6, y + 2, width - 12, rowHeight - 4);
            if (row.active)
            {
                using var selectedPath = Icons.Rounded(bounds, 8);
                graphics.FillPath(selectedBrush, selectedPath);
            }
            Color iconColor = row.enabled ? text : dim;
            graphics.DrawImage(SvgIcons.Render(row.icon, 18, iconColor), 16, y + (rowHeight - 18) / 2);
            graphics.DrawString(row.text, font, row.enabled ? textBrush : dimBrush,
                new RectangleF(48, y, width - 96, rowHeight), left);
            string? accessory = row.value ?? (row.active ? "✓" : null);
            if (accessory is not null)
                graphics.DrawString(accessory, valueFont, textBrush, new RectangleF(width - 90, y, 72, rowHeight), right);
            y += rowHeight;
        }
        return bitmap;
    }

    using var darkPreview = RenderMenu(true);
    using var lightPreview = RenderMenu(false);
    using var modePreview = RenderMenu(true, true);
    int gap = 28, pad = 24;
    using var preview = new Bitmap(darkPreview.Width + lightPreview.Width + modePreview.Width + gap * 2 + pad * 2,
        Math.Max(Math.Max(darkPreview.Height, lightPreview.Height), modePreview.Height) + pad * 2);
    using (var g = Graphics.FromImage(preview))
    {
        g.Clear(Color.FromArgb(210, 213, 220));
        g.DrawImage(darkPreview, pad, pad);
        g.DrawImage(lightPreview, pad + darkPreview.Width + gap, pad);
        g.DrawImage(modePreview, pad + darkPreview.Width + gap + lightPreview.Width + gap, pad);
    }
    string menuOut = Out("reference", "tray-menu-preview.png");
    preview.Save(menuOut, ImageFormat.Png);
    Console.WriteLine("saved: " + menuOut);
    return;
}

// Режим "svg": сгенерировать SVG-лист иконок и отрендерить его для сверки.
if (args.Length > 0 && args[0] == "svg")
{
    string svgOut = Out("reference", "icons-sheet.svg");
    string pngOut = Out("reference", "svg-preview.png");
    string svg = SvgGen.BuildSheet();
    File.WriteAllText(svgOut, svg);
    var doc = Svg.SvgDocument.FromSvg<Svg.SvgDocument>(svg);
    using (var rb = doc.Draw())
        rb.Save(pngOut, ImageFormat.Png);
    Console.WriteLine("svg:  " + svgOut);
    Console.WriteLine("png:  " + pngOut);
    return;
}

// Режим "bench": стоимость операций кадра анимации (панель/OSD) в микросекундах.
if (args.Length > 0 && args[0] == "bench")
{
    string root = Repo("assets", "svg", "osd");
    Bitmap Load(string name, int size)
    {
        var d = Svg.SvgDocument.FromSvg<Svg.SvgDocument>(File.ReadAllText(Path.Combine(root, name + ".svg")));
        d.Width = size; d.Height = size;
        var b = new Bitmap(size, size);
        using var g = Graphics.FromImage(b);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        d.Draw(g);
        return b;
    }
    using var icon40 = Load("perf-auto-dial", 40);
    using var needle40 = Load("perf-auto-needle", 40);
    using var icon64 = Load("perf-auto-dial", 64);

    using var canvas = new Bitmap(484, 230);
    using var g2 = Graphics.FromImage(canvas);
    g2.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
    g2.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    using var cachedFont = new Font("Segoe UI", 9f);

    double Bench(string label, int iters, Action a)
    {
        a(); // прогрев
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < iters; i++) a();
        sw.Stop();
        double us = sw.Elapsed.TotalMilliseconds * 1000.0 / iters;
        Console.WriteLine($"{label,-42} {us,8:F1} мкс");
        return us;
    }

    Console.WriteLine("=== стоимость операций (мкс за операцию) ===");
    double fCreate = Bench("создание+dispose 4 шрифтов", 2000, () =>
    {
        using var f1 = new Font("Segoe UI Semibold", 11f);
        using var f2 = new Font("Segoe UI", 8.5f);
        using var f3 = new Font("Segoe UI", 9f);
        using var f4 = new Font("Segoe UI Semibold", 11f);
    });
    double clear = Bench("clear + скруглённая рамка", 2000, () =>
    {
        g2.Clear(Color.FromArgb(28, 28, 30));
        using var pen = new Pen(Color.FromArgb(70, 70, 74));
        using var path = XiControl.Ui.Icons.Rounded(new RectangleF(0, 0, 483, 229), 18);
        g2.DrawPath(pen, path);
    });
    double icons = Bench("5 иконок 40px c ColorMatrix", 2000, () =>
    {
        using var attrs = new System.Drawing.Imaging.ImageAttributes();
        attrs.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.7f });
        for (int i = 0; i < 5; i++)
            g2.DrawImage(icon40, new Rectangle(20 + i * 90, 50, 40, 40), 0, 0, 40, 40, GraphicsUnit.Pixel, attrs);
    });
    double rot = Bench("поворот стрелки (save/rotate/draw)", 2000, () =>
    {
        var st = g2.Save();
        g2.TranslateTransform(40, 70); g2.RotateTransform(23f);
        g2.DrawImage(needle40, new Rectangle(-20, -20, 40, 40));
        g2.Restore(st);
    });
    using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
    double text = Bench("6 надписей DrawString", 2000, () =>
    {
        for (int i = 0; i < 6; i++)
            g2.DrawString("Полная мощность", cachedFont, Brushes.White,
                new RectangleF(10 + i * 75, 150, 80, 40), centered);
    });
    double osdIcon = Bench("иконка OSD 64px + поворот", 2000, () =>
    {
        var st = g2.Save();
        g2.TranslateTransform(240, 100); g2.RotateTransform(-15f);
        g2.DrawImage(icon64, new Rectangle(-32, -32, 64, 64));
        g2.Restore(st);
    });

    double frame = fCreate + clear + icons + rot + text;
    Console.WriteLine();
    Console.WriteLine($"кадр панели сейчас:  ~{frame:F0} мкс  → при 33 fps ≈ {frame * 33 / 10000:F2}% одного ядра");
    Console.WriteLine($"без пересоздания шрифтов: ~{frame - fCreate:F0} мкс → ≈ {(frame - fCreate) * 33 / 10000:F2}% одного ядра");
    Console.WriteLine($"кадр OSD: ~{clear + osdIcon + text / 3:F0} мкс");
    return;
}

// Режим "one <svg-path> [size]": отрендерить один SVG (белым на тёмном) для отладки.
if (args.Length > 1 && args[0] == "one")
{
    int oneSize = args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 128;
    string t = File.ReadAllText(args[1]).Replace("currentColor", "#F0F0F0");
    var d1 = Svg.SvgDocument.FromSvg<Svg.SvgDocument>(t);
    d1.Width = oneSize; d1.Height = oneSize;
    using var b1 = new Bitmap(oneSize, oneSize);
    using (var g1 = Graphics.FromImage(b1))
    {
        g1.Clear(Color.FromArgb(28, 28, 30));
        g1.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        d1.Draw(g1);
    }
    string onePath = Out("reference", "one-icon.png");
    b1.Save(onePath, ImageFormat.Png);
    Console.WriteLine("saved: " + onePath);
    return;
}

// Режим "ico": собрать app.ico из settings.svg (PNG-вложения 16..256, формат Vista+).
if (args.Length > 0 && args[0] == "ico")
{
    string svgPath = Repo("assets", "svg", "osd", "settings.svg");
    string icoPath = Out("src", "app.ico");
    int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };

    var doc0 = Svg.SvgDocument.FromSvg<Svg.SvgDocument>(File.ReadAllText(svgPath));
    var pngs = new List<byte[]>();
    foreach (int s in sizes)
    {
        doc0.Width = s; doc0.Height = s;
        using var b = new Bitmap(s, s);
        using (var g = Graphics.FromImage(b))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            doc0.Draw(g);
        }
        using var ms = new MemoryStream();
        b.Save(ms, ImageFormat.Png);
        pngs.Add(ms.ToArray());
    }

    using var fs = new FileStream(icoPath, FileMode.Create);
    using var w = new BinaryWriter(fs);
    w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length); // ICONDIR
    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++) // ICONDIRENTRY
    {
        int s = sizes[i];
        w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
        w.Write((byte)0); w.Write((byte)0);       // палитра, резерв
        w.Write((ushort)1); w.Write((ushort)32);  // planes, bpp
        w.Write(pngs[i].Length); w.Write(offset);
        offset += pngs[i].Length;
    }
    foreach (var p in pngs) w.Write(p);
    Console.WriteLine("saved: " + icoPath);
    return;
}

// Режим "user": отрендерить пользовательские SVG-ассеты (assets/svg) в сетку для проверки Svg.NET.
if (args.Length > 0 && args[0] == "user")
{
    string root = Repo("assets", "svg");
    var osdFiles = Directory.GetFiles(Path.Combine(root, "osd"), "*.svg").OrderBy(f => f).ToArray();
    var trayFiles = Directory.GetFiles(Path.Combine(root, "tray"), "*.svg").OrderBy(f => f).ToArray();

    Bitmap RenderSvg(string path, int size, Color? recolor)
    {
        string text = File.ReadAllText(path);
        if (recolor is Color c)
            text = text.Replace("currentColor", $"#{c.R:X2}{c.G:X2}{c.B:X2}");
        var d = Svg.SvgDocument.FromSvg<Svg.SvgDocument>(text);
        d.Width = size; d.Height = size;
        var b = new Bitmap(size, size);
        using var g = Graphics.FromImage(b);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        d.Draw(g);
        return b;
    }

    int uCols = 4, uTileW = 190, uTileH = 140;
    // трейных иконок давно больше четырёх — им нужен не один ряд, а столько же колонок, что и OSD
    int uOsdRows = (osdFiles.Length + uCols - 1) / uCols;
    int uRows = uOsdRows + (trayFiles.Length + uCols - 1) / uCols;
    using var sheet = new Bitmap(uCols * uTileW, uRows * uTileH);
    using (var g = Graphics.FromImage(sheet))
    {
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.FromArgb(28, 28, 30));
        using var f = new Font("Segoe UI", 9);
        using var br = new SolidBrush(Color.FromArgb(240, 240, 240));
        using var pen = new Pen(Color.FromArgb(55, 55, 58));

        for (int i = 0; i < osdFiles.Length; i++)
        {
            int cx = (i % uCols) * uTileW, cy = (i / uCols) * uTileH;
            g.DrawRectangle(pen, cx, cy, uTileW, uTileH);
            using (var img = RenderSvg(osdFiles[i], 96, null))
                g.DrawImage(img, cx + 14, cy + 10);
            g.DrawString(Path.GetFileNameWithoutExtension(osdFiles[i]), f, br, cx + 10, cy + uTileH - 24);
        }
        // трей: белым на тёмном, размеры 16/24/40
        for (int i = 0; i < trayFiles.Length; i++)
        {
            int cx = (i % uCols) * uTileW, ty = (uOsdRows + i / uCols) * uTileH;
            g.DrawRectangle(pen, cx, ty, uTileW, uTileH);
            var trayColor = Color.FromArgb(240, 240, 240);
            using (var i40 = RenderSvg(trayFiles[i], 40, trayColor)) g.DrawImage(i40, cx + 14, ty + 20);
            using (var i24 = RenderSvg(trayFiles[i], 24, trayColor)) g.DrawImage(i24, cx + 70, ty + 28);
            using (var i16 = RenderSvg(trayFiles[i], 16, trayColor)) g.DrawImage(i16, cx + 110, ty + 32);
            g.DrawString(Path.GetFileNameWithoutExtension(trayFiles[i]), f, br, cx + 10, ty + uTileH - 24);
        }
    }
    string userOut = Out("reference", "user-icons-preview.png");
    sheet.Save(userOut, ImageFormat.Png);
    Console.WriteLine("saved: " + userOut);
    return;
}

// Рендерит все иконки в PNG-сетку (тёмный фон, как OSD/трей) для визуальной проверки.

var white = Color.FromArgb(240, 240, 240);
var green = Color.FromArgb(52, 199, 89);
var blue = Color.FromArgb(90, 170, 255);
var orange = Color.FromArgb(255, 149, 0);
var amber = Color.FromArgb(230, 190, 70);
var gray = Color.FromArgb(150, 150, 155);
var red = Color.FromArgb(255, 90, 90);
var card = Color.FromArgb(28, 28, 30);

var items = new (string name, Action<Graphics, RectangleF> draw)[]
{
    ("Тихий · mode-quiet",        (g, r) => Icons.Leaf(g, r, green)),
    ("Авто · mode-auto",          (g, r) => Icons.Gauge(g, r, blue)),
    ("Турбо · mode-turbo",        (g, r) => Icons.Bolt(g, r, orange, true)),
    ("Полная · mode-full",        (g, r) => Icons.DoubleBolt(g, r, orange)),
    ("База · app",                (g, r) => Icons.Toggles(g, r, white)),
    ("Микрофон · mic",            (g, r) => Icons.Mic(g, r, green, false)),
    ("Мик.выкл · mic-muted",      (g, r) => Icons.Mic(g, r, gray, true)),
    ("Подсветка · keyboard-light",(g, r) => Icons.Keyboard(g, r, blue)),
    ("Зарядка · battery-charging",(g, r) => { Icons.Battery(g, r, green, 0.8f); Icons.BoltOverlay(g, r, Color.White, card); }),
    ("От батареи · battery",      (g, r) => Icons.Battery(g, r, amber, 0.6f)),
    ("Беречь вкл · battery-care", (g, r) => { Icons.Battery(g, r, green, 0.8f); Icons.LeafOverlay(g, r, card); }),
    ("Беречь выкл · battery-off", (g, r) => { Icons.Battery(g, r, gray, 0.95f); Icons.Slash(g, r, red); }),
};

int cols = 3, big = 96, small = 28;
int tileW = 240, tileH = 150;
int rows = (items.Length + cols - 1) / cols;
int W = cols * tileW, H = rows * tileH;

using var bmp = new Bitmap(W, H);
using (var g = Graphics.FromImage(bmp))
{
    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    g.Clear(card);
    using var labelFont = new Font("Segoe UI", 10);
    using var labelBrush = new SolidBrush(white);
    using var gridPen = new Pen(Color.FromArgb(55, 55, 58));

    for (int i = 0; i < items.Length; i++)
    {
        int cx = (i % cols) * tileW, cy = (i / cols) * tileH;
        g.DrawRectangle(gridPen, cx, cy, tileW, tileH);

        // крупная версия
        items[i].draw(g, new RectangleF(cx + 20, cy + 18, big, big));
        // мелкая (как в трее)
        items[i].draw(g, new RectangleF(cx + 140, cy + 30, small, small));
        g.DrawRectangle(gridPen, cx + 140, cy + 30, small, small);

        g.DrawString(items[i].name, labelFont, labelBrush, cx + 14, cy + tileH - 26);
    }
}

string outPath = Out("reference", "icon-preview.png");
bmp.Save(outPath, ImageFormat.Png);
Console.WriteLine("saved: " + outPath);
