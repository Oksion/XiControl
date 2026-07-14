using System.Drawing.Drawing2D;
using System.Management;
using System.Runtime.InteropServices;
using XiControl.Config;
using XiControl.Localization;

namespace XiControl.Ui;

/// <summary>
/// «Монитор»: живые графики с момента открытия — потребление (Вт, датчик батареи),
/// CPU % и RAM %. Семплирование (1 Гц) работает только пока окно видно.
/// Ватты честные только от батареи/на зарядке: на питании от сети датчика нет — «—».
/// Три вида — полный (графики), мини (строка Power/CPU/RAM), только ватты;
/// переключаются кнопкой «вид» или двойным кликом по виджету, выбор запоминается.
/// </summary>
public sealed class MonitorForm : Form
{
    private static readonly Color Card = Color.FromArgb(28, 28, 30);
    private static readonly Color Border = Color.FromArgb(70, 70, 74);
    private static readonly Color TextCol = Color.FromArgb(238, 238, 238);
    private static readonly Color DimCol = Color.FromArgb(150, 150, 155);
    private static readonly Color DischargeCol = Color.FromArgb(255, 149, 0); // оранжевый — разряд батареи (вниз)
    private static readonly Color ChargeCol = Color.FromArgb(52, 199, 89);    // зелёный — заряд в батарею (вверх)
    private static readonly Color CpuCol = Color.FromArgb(90, 170, 255);
    private static readonly Color RamCol = Color.FromArgb(179, 157, 219);     // сиреневый (зелёный ушёл под заряд)

    private static readonly Font TitleFont = new("Segoe UI Semibold", 11f);
    private static readonly Font ValueFont = new("Segoe UI Semibold", 13f);
    private static readonly Font LabelFont = new("Segoe UI", 8.5f);
    private static readonly Font BigFont = new("Segoe UI Semibold", 15f); // вид «только ватты»

    private const int Capacity = 180; // ~3 минуты при 1 Гц

    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    private readonly List<float> _power = new(); // Вт со знаком: + заряд в батарею, − разряд; NaN = от сети без заряда
    private readonly List<float> _cpu = new();   // 0..100
    private readonly List<float> _ram = new();   // 0..100

    private ManagementObjectSearcher? _battery;
    private long _prevIdle, _prevKernel, _prevUser;
    private float _ramUsedGb, _ramTotalGb;

    private Rectangle _close, _viewBtn;
    private bool _closeHover, _viewHover;

    // вид виджета: полный (графики) / мини (строка индикаторов) / только ватты
    private enum ViewKind { Full, Mini, Power }
    private ViewKind _view;
    private int _corner; // скругление текущего вида (общее для Region и рамки)

    private readonly AppConfig _cfg;

    public MonitorForm(AppConfig cfg)
    {
        _cfg = cfg;
        _view = cfg.MonitorView?.ToLowerInvariant() switch
        {
            "mini" => ViewKind.Mini,
            "power" => ViewKind.Power,
            _ => ViewKind.Full,
        };
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = Card;

        _tick.Tick += (_, _) => { Sample(); Invalidate(); };
        _ = Handle;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            return cp;
        }
    }

    private int Sc(float v) => (int)Math.Round(v * DeviceDpi / 96f);

    public void Popup()
    {
        if (Visible) { Hide(); return; }

        _power.Clear(); _cpu.Clear(); _ram.Clear();
        _prevIdle = _prevKernel = _prevUser = 0;
        Sample(); // первая точка сразу

        ApplyView();

        // восстановить сохранённую позицию (если она всё ещё на каком-то экране)
        var saved = _cfg.MonitorX is int mx && _cfg.MonitorY is int my ? new Point(mx, my) : (Point?)null;
        if (saved is Point pt && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(pt, Size))))
        {
            Location = pt;
        }
        else
        {
            var wa = Screen.PrimaryScreen!.WorkingArea;
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (int)(wa.Height * 0.50));
        }
        Show();
        Activate();
    }

    /// <summary>Размер, скругление и кнопки под текущий вид (позиция не трогается).</summary>
    private void ApplyView()
    {
        int w, h;
        switch (_view)
        {
            case ViewKind.Mini: // строка Power | CPU | RAM + кнопки «вид»/крестик справа
                w = Sc(16) + Sc(104) + Sc(8) + Sc(72) + Sc(8) + Sc(72) + Sc(8) + Sc(18) + Sc(6) + Sc(18) + Sc(10);
                h = Sc(56);
                _corner = Sc(14);
                _close = new Rectangle(w - Sc(10) - Sc(18), (h - Sc(18)) / 2, Sc(18), Sc(18));
                _viewBtn = new Rectangle(_close.X - Sc(6) - Sc(18), _close.Y, Sc(18), Sc(18));
                break;
            case ViewKind.Power: // только ватты; кнопок нет — дальше по кругу двойным кликом
                w = Sc(116); h = Sc(46);
                _corner = Sc(12);
                _close = Rectangle.Empty;
                _viewBtn = Rectangle.Empty;
                break;
            default:
                w = Sc(400); h = Sc(96) * 3 + Sc(52);
                _corner = Sc(18);
                _close = new Rectangle(w - Sc(16) - Sc(22), Sc(14), Sc(22), Sc(22)); // как в панели
                _viewBtn = new Rectangle(_close.X - Sc(28), _close.Y, Sc(22), Sc(22));
                break;
        }
        Size = new Size(w, h);
        var old = Region;
        using (var p = Draw.Rounded(new Rectangle(0, 0, w, h), _corner))
            Region = new Region(p);
        old?.Dispose();
    }

    // Полный → мини → только ватты → снова полный; выбор запоминается в конфиге
    private void CycleView()
    {
        _view = _view switch
        {
            ViewKind.Full => ViewKind.Mini,
            ViewKind.Mini => ViewKind.Power,
            _ => ViewKind.Full,
        };
        _cfg.MonitorView = _view switch { ViewKind.Mini => "mini", ViewKind.Power => "power", _ => null };
        _cfg.Save();
        ApplyView();
        Invalidate();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) _tick.Start();
        else
        {
            _tick.Stop(); // скрыт — не семплируем вообще
            _cfg.MonitorX = Left; _cfg.MonitorY = Top;
            _cfg.Save();
        }
    }

    // виджет: перетаскивается за любое место, кроме кнопок; не прячется при потере фокуса
    private const int WM_NCHITTEST = 0x84, HTCLIENT = 1, HTCAPTION = 2;
    private const int WM_NCLBUTTONDBLCLK = 0xA3;
    protected override void WndProc(ref Message m)
    {
        // двойной клик по «шапке» (то есть почти всему виджету) — следующий вид;
        // base не зовём, чтобы не сработало системное разворачивание окна
        if (m.Msg == WM_NCLBUTTONDBLCLK) { CycleView(); return; }
        base.WndProc(ref m);
        if (m.Msg == WM_NCHITTEST && (int)m.Result == HTCLIENT)
        {
            long lp = m.LParam.ToInt64();
            var p = PointToClient(new Point(unchecked((short)(lp & 0xFFFF)), unchecked((short)((lp >> 16) & 0xFFFF))));
            if (!_close.Contains(p) && !_viewBtn.Contains(p)) m.Result = HTCAPTION;
        }
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Hide();
    }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (_close.Contains(e.Location)) Hide();
        else if (_viewBtn.Contains(e.Location)) CycleView();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool h = _close.Contains(e.Location);
        if (h != _closeHover) { _closeHover = h; Invalidate(_close); }
        bool v = _viewBtn.Contains(e.Location);
        if (v != _viewHover) { _viewHover = v; Invalidate(_viewBtn); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_closeHover) { _closeHover = false; Invalidate(_close); }
        if (_viewHover) { _viewHover = false; Invalidate(_viewBtn); }
    }

    // ---------- семплирование ----------

    private void Sample()
    {
        Push(_cpu, SampleCpu());
        Push(_ram, SampleRam());
        Push(_power, SamplePowerWatts());
    }

    private static void Push(List<float> list, float v)
    {
        list.Add(v);
        if (list.Count > Capacity) list.RemoveAt(0);
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    private float SampleCpu()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user)) return float.NaN;
        float cpu = 0f;
        long dIdle = idle - _prevIdle, dBusy = (kernel - _prevKernel) + (user - _prevUser);
        if (_prevKernel != 0 && dBusy > 0)
            cpu = Math.Clamp(100f * (dBusy - dIdle) / dBusy, 0f, 100f);
        (_prevIdle, _prevKernel, _prevUser) = (idle, kernel, user);
        return _prevKernel == kernel && cpu == 0f && _cpu.Count == 0 ? float.NaN : cpu;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    private float SampleRam()
    {
        var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref m)) return float.NaN;
        _ramTotalGb = m.TotalPhys / 1073741824f;
        _ramUsedGb = (m.TotalPhys - m.AvailPhys) / 1073741824f;
        return m.MemoryLoad;
    }

    /// <summary>Вт с датчика батареи со знаком: заряд в батарею +, разряд −, от сети без заряда — NaN.</summary>
    private float SamplePowerWatts()
    {
        try
        {
            _battery ??= new ManagementObjectSearcher(@"root\wmi",
                "SELECT ChargeRate, DischargeRate, Discharging FROM BatteryStatus");
            foreach (ManagementObject o in _battery.Get())
            {
                bool discharging = (bool)o["Discharging"];
                uint rate = discharging ? (uint)(int)o["DischargeRate"] : (uint)(int)o["ChargeRate"];
                o.Dispose();
                if (rate == 0) return float.NaN;
                float w = rate / 1000f;
                return discharging ? -w : w;   // разряд — вниз (минус), заряд — вверх (плюс)
            }
        }
        catch (Exception ex) { Log.Ex("Monitor.Power", ex); _battery = null; }
        return float.NaN;
    }

    // ---------- отрисовка ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        g.Clear(Card);
        using (var pen = new Pen(Border))
        using (var path = Draw.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), _corner))
            g.DrawPath(pen, path);

        float pw = _power.Count > 0 ? _power[^1] : float.NaN;
        Color pColor = float.IsNaN(pw) ? DimCol : (pw >= 0 ? ChargeCol : DischargeCol);

        if (_view == ViewKind.Power) { PaintPower(g, pw, pColor); return; }
        if (_view == ViewKind.Mini) { PaintMini(g, pw, pColor); return; }

        TextRenderer.DrawText(g, Loc.T("monitor.title"), TitleFont,
            new Rectangle(Sc(16), Sc(12), Width, Sc(24)), TextCol, TextFormatFlags.Left | TextFormatFlags.Top);

        // кнопка «вид» и крестик — общие с панелью
        Draw.ViewButton(g, _viewBtn, _viewHover);
        Draw.CloseButton(g, _close, _closeHover);

        int rowH = Sc(96), top = Sc(44);
        string powerText = !float.IsNaN(pw)
            ? (pw >= 0 ? "+" : "") + Loc.T("monitor.watts", MathF.Abs(pw))  // всегда положительное число; направление — цветом
            : Loc.T("monitor.na");

        float powerMax = NiceMax(_power);
        DrawRow(g, new Rectangle(Sc(16), top, Width - Sc(32), rowH),
            Loc.T("monitor.power"), powerText, pColor, _power, powerMax,
            scaleLabel: Loc.T("monitor.watts.scale", powerMax), colored: true);
        DrawRow(g, new Rectangle(Sc(16), top + rowH, Width - Sc(32), rowH),
            "CPU", _cpu.Count > 0 && !float.IsNaN(_cpu[^1]) ? $"{_cpu[^1]:0}%" : "—", CpuCol, _cpu, 100f);
        DrawRow(g, new Rectangle(Sc(16), top + rowH * 2, Width - Sc(32), rowH),
            "RAM", _ram.Count > 0 ? $"{_ram[^1]:0}%" : "—", RamCol, _ram, 100f,
            _ramTotalGb > 0 ? Loc.T("monitor.ram.of", _ramUsedGb, _ramTotalGb) : null);
    }

    // Мини-вид: три индикатора в строку без графиков, подписи латиницей во всех локалях
    private void PaintMini(Graphics g, float pw, Color pColor)
    {
        string powerVal = float.IsNaN(pw) ? "—" : Loc.T("monitor.watts", MathF.Abs(pw));
        int x = Sc(16);
        x = MiniCell(g, x, Sc(104), "Power", powerVal, pColor);
        x = MiniCell(g, x, Sc(72), "CPU", _cpu.Count > 0 && !float.IsNaN(_cpu[^1]) ? $"{_cpu[^1]:0}%" : "—", CpuCol);
        MiniCell(g, x, Sc(72), "RAM", _ram.Count > 0 ? $"{_ram[^1]:0}%" : "—", RamCol);

        Draw.ViewButton(g, _viewBtn, _viewHover);
        Draw.CloseButton(g, _close, _closeHover);
    }

    private int MiniCell(Graphics g, int x, int w, string label, string value, Color color)
    {
        TextRenderer.DrawText(g, label, LabelFont,
            new Rectangle(x, Sc(8), w, Sc(16)), DimCol, TextFormatFlags.Left | TextFormatFlags.Top);
        TextRenderer.DrawText(g, value, ValueFont,
            new Rectangle(x, Sc(24), w, Sc(26)), color, TextFormatFlags.Left | TextFormatFlags.Top);
        return x + w + Sc(8);
    }

    // Вид «только ватты»: одно целое число во всё окно; направление тока — цветом,
    // как на графике (зелёный — заряд, оранжевый — разряд, серое «—» — от сети)
    private void PaintPower(Graphics g, float pw, Color pColor)
    {
        string text = float.IsNaN(pw) ? "—" : Loc.T("monitor.watts.scale", MathF.Abs(pw));
        TextRenderer.DrawText(g, text, BigFont, ClientRectangle, pColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    /// <summary>Верх шкалы ватт: максимум данных с запасом, округлённый вверх до кратного 5 (мин. 10).</summary>
    private static float NiceMax(List<float> data)
    {
        float max = 8f;
        foreach (var v in data) if (!float.IsNaN(v) && MathF.Abs(v) > max) max = MathF.Abs(v);
        return MathF.Ceiling(max * 1.05f / 5f) * 5f;
    }

    private void DrawRow(Graphics g, Rectangle r, string label, string value, Color color, List<float> data, float max,
        string? sub = null, string? scaleLabel = null, bool colored = false)
    {
        TextRenderer.DrawText(g, label, LabelFont,
            new Rectangle(r.X, r.Y + Sc(8), Sc(110), Sc(16)), DimCol, TextFormatFlags.Left | TextFormatFlags.Top);
        TextRenderer.DrawText(g, value, ValueFont,
            new Rectangle(r.X, r.Y + Sc(26), Sc(110), Sc(26)), color, TextFormatFlags.Left | TextFormatFlags.Top);
        if (sub != null)
            TextRenderer.DrawText(g, sub, LabelFont,
                new Rectangle(r.X, r.Y + Sc(54), Sc(112), Sc(16)), DimCol, TextFormatFlags.Left | TextFormatFlags.Top);

        var plot = new Rectangle(r.X + Sc(116), r.Y + Sc(8), r.Width - Sc(116), r.Height - Sc(20));
        using (var bg = new SolidBrush(Color.FromArgb(38, 38, 41)))
        using (var path = Draw.Rounded(plot, Sc(8)))
            g.FillPath(bg, path);

        // подпись шкалы (верх графика = это значение; линии сетки — пятые доли)
        if (scaleLabel != null)
            TextRenderer.DrawText(g, scaleLabel, LabelFont,
                new Rectangle(plot.X, plot.Y + Sc(2), plot.Width - Sc(6), Sc(14)), DimCol,
                TextFormatFlags.Right | TextFormatFlags.Top);

        // сетка: горизонтали через 20% шкалы, вертикали каждые 30 секунд
        using (var grid = new Pen(Color.FromArgb(50, 50, 54)))
        {
            for (int i = 1; i <= 4; i++)
            {
                float gy = plot.Bottom - Sc(3) - (plot.Height - Sc(6)) * i / 5f;
                g.DrawLine(grid, plot.X + Sc(2), gy, plot.Right - Sc(2), gy);
            }
            float sx = (float)plot.Width / (Capacity - 1);
            for (int s = 30; s < Capacity; s += 30)
                g.DrawLine(grid, plot.X + s * sx, plot.Y + Sc(2), plot.X + s * sx, plot.Bottom - Sc(2));
        }

        if (data.Count < 2 || max <= 0) return;

        float stepX = (float)plot.Width / (Capacity - 1);
        if (colored) { DrawColored(g, plot, data, max, stepX); return; }

        var pts = new List<PointF>(data.Count);
        for (int i = 0; i < data.Count; i++)
        {
            if (float.IsNaN(data[i])) { DrawSegment(g, pts, plot, color); pts.Clear(); continue; }
            float x = plot.X + i * stepX;
            float y = plot.Bottom - Sc(3) - (plot.Height - Sc(6)) * Math.Clamp(data[i] / max, 0f, 1f);
            pts.Add(new PointF(x, y));
        }
        DrawSegment(g, pts, plot, color);
    }

    // График питания: во всю высоту от низа (по модулю), но каждый тик красится по направлению —
    // зелёный (заряд, ток в батарею) или оранжевый (разряд).
    private void DrawColored(Graphics g, Rectangle plot, List<float> data, float max, float stepX)
    {
        float baseY = plot.Bottom - Sc(3), top = plot.Y + Sc(3), span = baseY - top;
        for (int i = 0; i < data.Count - 1; i++)
        {
            if (float.IsNaN(data[i]) || float.IsNaN(data[i + 1])) continue;
            float x0 = plot.X + i * stepX, x1 = plot.X + (i + 1) * stepX;
            float y0 = baseY - span * Math.Clamp(MathF.Abs(data[i]) / max, 0f, 1f);
            float y1 = baseY - span * Math.Clamp(MathF.Abs(data[i + 1]) / max, 0f, 1f);
            Color c = data[i + 1] >= 0f ? ChargeCol : DischargeCol; // цвет по направлению текущего тика
            using (var fill = new SolidBrush(Color.FromArgb(45, c)))
                g.FillPolygon(fill, new[] { new PointF(x0, y0), new PointF(x1, y1), new PointF(x1, baseY), new PointF(x0, baseY) });
            using var pen = new Pen(c, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLine(pen, x0, y0, x1, y1);
        }
    }

    private void DrawSegment(Graphics g, List<PointF> pts, Rectangle plot, Color color)
    {
        if (pts.Count < 2) return;
        // полупрозрачная заливка под линией
        using (var fill = new SolidBrush(Color.FromArgb(45, color)))
        {
            var area = new List<PointF>(pts) { new(pts[^1].X, plot.Bottom - Sc(2)), new(pts[0].X, plot.Bottom - Sc(2)) };
            g.FillPolygon(fill, area.ToArray());
        }
        using var pen = new Pen(color, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, pts.ToArray());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tick.Dispose();
            _battery?.Dispose();
        }
        base.Dispose(disposing);
    }
}
