using System.Drawing.Drawing2D;
using System.Management;
using XiControl.Config;
using XiControl.Localization;
using XiControl.Wmi;

namespace XiControl.Ui;

/// <summary>
/// «Монитор»: живые графики с момента открытия — потребление (Вт, датчик батареи),
/// CPU %, GPU % (Intel IGCL, если есть) и RAM %. Семплирование (1 Гц) работает только пока окно видно.
/// Ватты честные только от батареи/на зарядке: на питании от сети датчика нет — «—».
/// Три вида — полный (графики), мини (строка Power/CPU/RAM), только ватты;
/// переключаются кнопкой «вид» или двойным кликом по виджету, выбор запоминается.
/// </summary>
public sealed class MonitorForm : FlyoutForm
{
    // семантические алиасы общей палитры флайаутов + свои цвета графиков
    private static readonly Color DischargeCol = FlyoutPalette.Orange;     // разряд батареи (вниз)
    private static readonly Color ChargeCol = FlyoutPalette.Green;         // заряд в батарею (вверх)
    private static readonly Color CpuCol = FlyoutPalette.Blue;
    private static readonly Color GpuCol = Color.FromArgb(255, 213, 79);   // Amber 300: соседи по рядам холодные (CPU, RAM), а тёплые — через ряд
    private static readonly Color RamCol = Color.FromArgb(179, 157, 219);  // сиреневый (зелёный ушёл под заряд)
    private static readonly Color TempCol = Color.FromArgb(255, 111, 97);     // коралловый — температура (норма)
    private static readonly Color TempHotCol = Color.FromArgb(206, 32, 62);   // вишнёвый — горячая/крит-зона (сочно на OLED)

    // шрифты — из кэша ScaledFonts под текущий DPI: не разъезжаются с геометрией после смены разрешения
    private Font TitleFont => ScaledFonts.Get(DeviceDpi, "Segoe UI Semibold", 11f);
    private Font ValueFont => ScaledFonts.Get(DeviceDpi, "Segoe UI Semibold", 13f);
    private Font LabelFont => ScaledFonts.Get(DeviceDpi, "Segoe UI", 8.5f);
    private Font BigFont => ScaledFonts.Get(DeviceDpi, "Segoe UI Semibold", 15f); // вид «только ватты»

    private const int Capacity = 180; // ~3 минуты при 1 Гц

    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    private readonly List<float> _power = new(); // Вт со знаком: + заряд в батарею, − разряд; NaN = от сети без заряда
    private readonly List<float> _cpu = new();   // 0..100
    private readonly List<float> _gpu = new();   // 0..100; NaN = нет данных
    private readonly List<float> _ram = new();   // 0..100
    private readonly List<float> _temp = new();  // °C горячей точки (Intel DPTF); NaN = нет данных

    private const float TempMax = 100f; // верх шкалы графика температуры, °C (троттлинг ~95–100)

    private ManagementObjectSearcher? _battery;
    private readonly SystemIntegration.PowerDraw _powerDraw = new(); // живая мощность через Battery IOCTL
    private readonly SystemIntegration.GpuTelemetry _gpuTel = new(); // iGPU через Intel IGCL (ленивая инициализация)
    private readonly SystemIntegration.CpuLoad _cpuLoad = new();          // GetSystemTimes (общий с индикатором трея)
    private readonly SystemIntegration.TemperatureSource _tempSrc;        // DPTF, иначе ACPI-зона (общий с индикатором трея)
    private float _ramUsedGb, _ramTotalGb;
    private int _adapterWatts; // ватты подключённого PD-БП (0 — нет/не PD); MIFS, driver-free
    private float _gpuMhz, _gpuWatts; // текущая частота и мощность GPU — в подстроку ряда
    private bool _hasGpu;      // IGCL поднялся → резервируем ряд GPU (иначе виджет как раньше)
    private bool _hasTemp;     // на этой модели DPTF отдаёт температуры → резервируем строку/высоту
    private float _critC;      // критический порог, °C (0 = неизвестен); из ACPI-термозоны, best-effort
    private bool _critTried;   // порог уже пробовали прочитать (ACPI капризна — не долбим повторно)

    // с этого порога температура «горячая» → вишнёвый цвет; от крита с запасом, иначе разумный дефолт
    private float HotAt => _critC > 0 ? _critC - 15f : 88f;

    private Rectangle _close, _viewBtn, _expandBtn;

    // тултип по кнопкам заголовка: FlyoutTip — палитра флайаутов + dwell, холст не трогаем (XIC-8)
    private readonly FlyoutTip _tip;
    private int _tipBtn; // 0 нет, 1 close, 2 view, 3 expand — чтобы не пере-показывать на той же
    private bool _closeHover, _viewHover, _expandHover;

    // вид виджета: полный (графики) / мини (строка индикаторов) / только ватты
    private enum ViewKind { Full, Mini, Power }
    private ViewKind _view;
    private int _corner; // скругление текущего вида (общее для Region и рамки)

    private readonly AppConfig _cfg;
    private readonly IMifsClient _mifs;

    public MonitorForm(AppConfig cfg, IMifsClient mifs)
    {
        _cfg = cfg;
        _mifs = mifs;
        _tempSrc = new SystemIntegration.TemperatureSource(cfg.ForceAcpiTemperature);
        _tip = new FlyoutTip(this);
        _view = cfg.MonitorView?.ToLowerInvariant() switch
        {
            "mini" => ViewKind.Mini,
            "power" => ViewKind.Power,
            _ => ViewKind.Full,
        };
        // borderless tool-window поверх всех окон — база FlyoutForm
        _tick.Tick += (_, _) => { Sample(); Invalidate(); };
        _ = Handle;
    }

    public void Popup()
    {
        if (Visible) { Hide(); return; }

        _power.Clear(); _cpu.Clear(); _gpu.Clear(); _ram.Clear(); _temp.Clear();
        _cpuLoad.Reset(); // база времён CPU протухла, пока виджет был закрыт
        _gpuTel.Reset(); // иначе первая загрузка GPU размажется по времени, что виджет был закрыт
        Sample(); // первая точка сразу (заодно определит наличие DPTF-температур и IGCL до ApplyView)

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
            case ViewKind.Mini: // Power | CPU | [GPU] | RAM + [развернуть]/[вид]/[крестик] справа
                w = Sc(16) + Sc(104) + Sc(8) + Sc(72) + Sc(8) + (_hasGpu ? Sc(72) + Sc(8) : 0)
                    + Sc(72) + Sc(8) + Sc(18) + Sc(6) + Sc(18) + Sc(6) + Sc(18) + Sc(10);
                h = Sc(56);
                _corner = Sc(14);
                _close = new Rectangle(w - Sc(10) - Sc(18), (h - Sc(18)) / 2, Sc(18), Sc(18));
                _viewBtn = new Rectangle(_close.X - Sc(6) - Sc(18), _close.Y, Sc(18), Sc(18));
                _expandBtn = new Rectangle(_viewBtn.X - Sc(6) - Sc(18), _close.Y, Sc(18), Sc(18)); // слева от «вид» — сразу в полный
                break;
            case ViewKind.Power: // только ватты; кнопок нет — дальше по кругу двойным кликом
                w = Sc(116); h = Sc(46);
                _corner = Sc(12);
                _close = Rectangle.Empty;
                _viewBtn = Rectangle.Empty;
                _expandBtn = Rectangle.Empty;
                break;
            default:
                // базовые три ряда (питание, CPU, RAM) + GPU, если есть IGCL, + температура, если есть DPTF
                w = Sc(400); h = Sc(96) * (3 + (_hasGpu ? 1 : 0) + (_hasTemp ? 1 : 0)) + Sc(52);
                _corner = Sc(18);
                _close = new Rectangle(w - Sc(16) - Sc(22), Sc(14), Sc(22), Sc(22)); // как в панели
                _viewBtn = new Rectangle(_close.X - Sc(28), _close.Y, Sc(22), Sc(22));
                _expandBtn = Rectangle.Empty; // уже полный — разворачивать некуда
                break;
        }
        Size = new Size(w, h);
        SetRoundedRegion(_corner);
    }

    // Полный → мини → только ватты → снова полный (кнопка «вид» / двойной клик)
    private void CycleView() => SetView(_view switch
    {
        ViewKind.Full => ViewKind.Mini,
        ViewKind.Mini => ViewKind.Power,
        _ => ViewKind.Full,
    });

    // Переключить на конкретный вид; выбор запоминается в конфиге
    private void SetView(ViewKind v)
    {
        _view = v;
        _cfg.MonitorView = v switch { ViewKind.Mini => "mini", ViewKind.Power => "power", _ => null };
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
            _tip.Hide(); // отложенный dwell-показ не должен всплыть над скрытым виджетом
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
            if (!_close.Contains(p) && !_viewBtn.Contains(p) && !_expandBtn.Contains(p)) m.Result = HTCAPTION;
        }
    }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (_close.Contains(e.Location)) Hide();
        else if (_viewBtn.Contains(e.Location)) CycleView();
        else if (_expandBtn.Contains(e.Location)) SetView(ViewKind.Full); // из мини — сразу в полный
    }

    // DPI монитора сменился — пересчитать размер/Region/кнопки под новый DeviceDpi
    protected override void OnDpiRescaled() => ApplyView();

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool h = _close.Contains(e.Location);
        if (h != _closeHover) { _closeHover = h; Invalidate(_close); }
        bool v = _viewBtn.Contains(e.Location);
        if (v != _viewHover) { _viewHover = v; Invalidate(_viewBtn); }
        bool x = _expandBtn.Contains(e.Location);
        if (x != _expandHover) { _expandHover = x; Invalidate(_expandBtn); }

        int btn = h ? 1 : v ? 2 : x ? 3 : 0; // тултип по кнопке под курсором
        if (btn != _tipBtn)
        {
            _tipBtn = btn;
            _tip.Update(TipFor(btn), e.Location);
        }
    }

    private static string? TipFor(int btn) => btn switch
    {
        1 => Loc.T("panel.close"),
        2 => Loc.T("monitor.view"),
        3 => Loc.T("monitor.expand"),
        _ => null,
    };

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_closeHover) { _closeHover = false; Invalidate(_close); }
        if (_viewHover) { _viewHover = false; Invalidate(_viewBtn); }
        if (_expandHover) { _expandHover = false; Invalidate(_expandBtn); }
        if (_tipBtn != 0) { _tipBtn = 0; _tip.Hide(); }
    }

    // ---------- семплирование ----------

    private void Sample()
    {
        Push(_cpu, SampleCpu());
        Push(_gpu, SampleGpu());
        Push(_ram, SampleRam());
        Push(_power, SamplePowerWatts());
        Push(_temp, SampleTempC());
        try { _adapterWatts = _mifs.GetAdapterWatts(); } catch (Exception ex) { Log.Ex("Monitor.Adapter", ex); _adapterWatts = 0; }
    }

    /// <summary>
    /// Загрузка встроенного GPU через Intel IGCL — driver-free, права администратора не нужны
    /// (детали и раскладка телеметрии — в <see cref="SystemIntegration.GpuTelemetry"/>). Заодно
    /// запоминаем частоту и мощность для подстроки ряда. Первая выборка после открытия виджета
    /// только набирает базу счётчиков — она даёт NaN, график начинается со второй точки.
    /// IGCL нет (не Intel) → ряд GPU не показываем вовсе.
    /// </summary>
    private float SampleGpu()
    {
        bool ok = _gpuTel.TryRead(out float load, out float watts, out float mhz);
        if (!_gpuTel.Available) return float.NaN; // не Intel / телеметрия не та — ряда не будет
        _hasGpu = true;
        if (!ok) return float.NaN;
        (_gpuMhz, _gpuWatts) = (mhz, watts);
        return load;
    }

    /// <summary>
    /// Температура «горячей точки» через Intel DPTF — сам опрос вынесен в общий
    /// <see cref="SystemIntegration.DptfTemperature"/> (его же использует индикатор трея XIC-35).
    /// Здесь остаётся только резервирование строки и чтение крит-порога при первом успехе.
    /// </summary>
    private float SampleTempC()
    {
        float c = _tempSrc.ReadMaxC();
        if (_tempSrc.Present && !_hasTemp) { _hasTemp = true; ReadCriticalOnce(); } // источник есть → строка + крит-порог
        return c;
    }

    /// <summary>
    /// Критический порог температуры из ACPI-термозоны (WMI MSAcpi_ThermalZoneTemperature.CriticalTripPoint),
    /// в десятых Кельвина → °C. Значение статичное — читаем один раз, best-effort (класс капризный); при
    /// неудаче остаётся дефолтный HotAt. Отсюда «горячая зона» графика подсвечивается вишнёвым.
    /// </summary>
    private void ReadCriticalOnce()
    {
        if (_critTried) return;
        _critTried = true;
        try
        {
            using var s = new ManagementObjectSearcher(@"root\wmi",
                "SELECT CriticalTripPoint FROM MSAcpi_ThermalZoneTemperature");
            foreach (ManagementObject o in s.Get())
            {
                object? v = o["CriticalTripPoint"];
                o.Dispose();
                if (v is null) continue;
                float c = Convert.ToSingle(v, System.Globalization.CultureInfo.InvariantCulture) / 10f - 273.15f;
                if (c is > 60f and < 120f) { _critC = c; break; } // правдоподобный крит-порог
            }
        }
        catch (Exception ex) { Log.Ex("Monitor.Crit", ex); }
    }

    private static void Push(List<float> list, float v)
    {
        list.Add(v);
        if (list.Count > Capacity) list.RemoveAt(0);
    }

    // CPU и RAM — через общие классы SystemIntegration (их же использует индикатор трея XIC-35):
    // первая выборка CPU лишь набирает базу времён — даёт NaN, график начинается со второй точки.
    private float SampleCpu() => _cpuLoad.TryRead(out float pct) ? pct : float.NaN;

    private float SampleRam() =>
        SystemIntegration.MemoryLoad.TryRead(out float pct, out _ramUsedGb, out _ramTotalGb) ? pct : float.NaN;

    /// <summary>Вт с датчика батареи со знаком: заряд в батарею +, разряд −, от сети без заряда — NaN.</summary>
    private float SamplePowerWatts()
    {
        // Battery IOCTL — живое значение (не «залипает» без сторонних поллеров вроде HWiNFO);
        // если Battery API недоступен, мягко откатываемся на WMI.
        if (_powerDraw.TryReadWatts(out float w)) return w;
        return SamplePowerWattsWmi();
    }

    private float SamplePowerWattsWmi()
    {
        try
        {
            _battery ??= new ManagementObjectSearcher(@"root\wmi",
                "SELECT ChargeRate, DischargeRate, Discharging FROM BatteryStatus");
            return SystemIntegration.WmiQuery.First(_battery, o =>
            {
                bool discharging = (bool)o["Discharging"];
                uint rate = discharging ? (uint)(int)o["DischargeRate"] : (uint)(int)o["ChargeRate"];
                if (rate == 0) return float.NaN;
                float w = rate / 1000f;
                return discharging ? -w : w;   // разряд — вниз (минус), заряд — вверх (плюс)
            }) ?? float.NaN;                   // датчика нет вовсе — как и раньше, «неизвестно»
        }
        catch (Exception ex) { Log.Ex("Monitor.Power", ex); _battery = null; return float.NaN; }
    }

    // ---------- отрисовка ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintChrome(g, _corner);

        float pw = _power.Count > 0 ? _power[^1] : float.NaN;
        Color pColor = float.IsNaN(pw) ? FlyoutPalette.Dim : (pw >= 0 ? ChargeCol : DischargeCol);

        if (_view == ViewKind.Power) { PaintPower(g, pw, pColor); return; }
        if (_view == ViewKind.Mini) { PaintMini(g, pw, pColor); return; }

        TextRenderer.DrawText(g, Loc.T("monitor.title"), TitleFont,
            new Rectangle(Sc(16), Sc(12), Width, Sc(24)), FlyoutPalette.Text, TextFormatFlags.Left | TextFormatFlags.Top);

        // кнопка «вид» и крестик — общие с панелью
        Draw.ViewButton(g, _viewBtn, _viewHover);
        Draw.CloseButton(g, _close, _closeHover);

        int rowH = Sc(96), top = Sc(44);
        string powerText = !float.IsNaN(pw)
            ? (pw >= 0 ? "+" : "") + Loc.T("monitor.watts", MathF.Abs(pw))  // всегда положительное число; направление — цветом
            : Loc.T("monitor.na");

        // ряды идут подряд, а GPU и температура опциональны → позицию считаем счётчиком, а не множителем
        int row = 0;
        Rectangle Next() => new(Sc(16), top + rowH * row++, Width - Sc(32), rowH);

        float powerMax = NiceMax(_power);
        DrawRow(g, Next(),
            Loc.T("monitor.power"), powerText, pColor, _power, powerMax,
            sub: _adapterWatts > 0 ? Loc.T("monitor.adapter", _adapterWatts) : null, // рейтинг подключённого БП
            scaleLabel: Loc.T("monitor.watts.scale", powerMax),
            pick: v => v >= 0f ? ChargeCol : DischargeCol); // цвет по направлению тока
        DrawRow(g, Next(),
            "CPU", _cpu.Count > 0 && !float.IsNaN(_cpu[^1]) ? $"{_cpu[^1]:0}%" : "—", CpuCol, _cpu, 100f);

        if (_hasGpu)
        {
            float gl = _gpu.Count > 0 ? _gpu[^1] : float.NaN;
            DrawRow(g, Next(),
                "GPU", float.IsNaN(gl) ? "—" : $"{gl:0}%", GpuCol, _gpu, 100f,
                _gpuMhz > 0 ? Loc.T("monitor.gpu.sub", _gpuMhz, _gpuWatts) : null); // «2450 МГц · 21 Вт»
        }

        DrawRow(g, Next(),
            "RAM", _ram.Count > 0 ? $"{_ram[^1]:0}%" : "—", RamCol, _ram, 100f,
            _ramTotalGb > 0 ? Loc.T("monitor.ram.of", _ramUsedGb, _ramTotalGb) : null);

        if (_hasTemp)
        {
            float tc = _temp.Count > 0 ? _temp[^1] : float.NaN;
            Color now = !float.IsNaN(tc) && tc >= HotAt ? TempHotCol : TempCol; // текущее значение — вишнёвым, если горячо
            DrawRow(g, Next(),
                // у ACPI-зоны другой смысл (плата, не горячая точка) — подпись строки это говорит
                Loc.T(_tempSrc.Source == SystemIntegration.TempSource.AcpiZone ? "monitor.temp.zone" : "monitor.temp"),
                float.IsNaN(tc) ? "—" : Loc.T("monitor.temp.c", (int)tc), now, _temp, TempMax,
                scaleLabel: Loc.T("monitor.temp.c", (int)TempMax),
                pick: v => v >= HotAt ? TempHotCol : TempCol); // горячая зона на графике — вишнёвая
        }
    }

    // Мини-вид: три индикатора в строку без графиков, подписи латиницей во всех локалях
    private void PaintMini(Graphics g, float pw, Color pColor)
    {
        string powerVal = float.IsNaN(pw) ? "—" : Loc.T("monitor.watts", MathF.Abs(pw));
        int x = Sc(16);
        x = MiniCell(g, x, Sc(104), "Power", powerVal, pColor);
        x = MiniCell(g, x, Sc(72), "CPU", _cpu.Count > 0 && !float.IsNaN(_cpu[^1]) ? $"{_cpu[^1]:0}%" : "—", CpuCol);
        if (_hasGpu) // в мини-виде у GPU только процент — частота и ватты живут в полном виде
            x = MiniCell(g, x, Sc(72), "GPU", _gpu.Count > 0 && !float.IsNaN(_gpu[^1]) ? $"{_gpu[^1]:0}%" : "—", GpuCol);
        MiniCell(g, x, Sc(72), "RAM", _ram.Count > 0 ? $"{_ram[^1]:0}%" : "—", RamCol);

        Draw.ExpandButton(g, _expandBtn, _expandHover); // развернуть в полный
        Draw.ViewButton(g, _viewBtn, _viewHover);
        Draw.CloseButton(g, _close, _closeHover);
    }

    private int MiniCell(Graphics g, int x, int w, string label, string value, Color color)
    {
        TextRenderer.DrawText(g, label, LabelFont,
            new Rectangle(x, Sc(8), w, Sc(16)), FlyoutPalette.Dim, TextFormatFlags.Left | TextFormatFlags.Top);
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
        string? sub = null, string? scaleLabel = null, Func<float, Color>? pick = null)
    {
        TextRenderer.DrawText(g, label, LabelFont,
            new Rectangle(r.X, r.Y + Sc(8), Sc(110), Sc(16)), FlyoutPalette.Dim, TextFormatFlags.Left | TextFormatFlags.Top);
        TextRenderer.DrawText(g, value, ValueFont,
            new Rectangle(r.X, r.Y + Sc(26), Sc(110), Sc(26)), color, TextFormatFlags.Left | TextFormatFlags.Top);
        if (sub != null)
            TextRenderer.DrawText(g, sub, LabelFont,
                new Rectangle(r.X, r.Y + Sc(54), Sc(112), Sc(16)), FlyoutPalette.Dim, TextFormatFlags.Left | TextFormatFlags.Top);

        var plot = new Rectangle(r.X + Sc(116), r.Y + Sc(8), r.Width - Sc(116), r.Height - Sc(20));
        using (var bg = new SolidBrush(FlyoutPalette.PlotBg))
        using (var path = Draw.Rounded(plot, Sc(8)))
            g.FillPath(bg, path);

        // подпись шкалы (верх графика = это значение; линии сетки — пятые доли)
        if (scaleLabel != null)
            TextRenderer.DrawText(g, scaleLabel, LabelFont,
                new Rectangle(plot.X, plot.Y + Sc(2), plot.Width - Sc(6), Sc(14)), FlyoutPalette.Dim,
                TextFormatFlags.Right | TextFormatFlags.Top);

        // сетка: горизонтали через 20% шкалы, вертикали каждые 30 секунд
        using (var grid = new Pen(FlyoutPalette.PlotGrid))
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
        if (pick != null) { DrawColored(g, plot, data, max, stepX, pick); return; }

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

    // Заливка от низа по модулю значения, но каждый тик красится своим цветом через pick():
    // питание — по направлению тока (заряд/разряд), температура — норма/горячая зона.
    private void DrawColored(Graphics g, Rectangle plot, List<float> data, float max, float stepX, Func<float, Color> pick)
    {
        float baseY = plot.Bottom - Sc(3), top = plot.Y + Sc(3), span = baseY - top;
        for (int i = 0; i < data.Count - 1; i++)
        {
            if (float.IsNaN(data[i]) || float.IsNaN(data[i + 1])) continue;
            float x0 = plot.X + i * stepX, x1 = plot.X + (i + 1) * stepX;
            float y0 = baseY - span * Math.Clamp(MathF.Abs(data[i]) / max, 0f, 1f);
            float y1 = baseY - span * Math.Clamp(MathF.Abs(data[i + 1]) / max, 0f, 1f);
            Color c = pick(data[i + 1]); // цвет по значению текущего тика
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
            _tip.Dispose();
            _battery?.Dispose();
            _tempSrc.Dispose();
            _powerDraw.Dispose();
            _gpuTel.Dispose();
        }
        base.Dispose(disposing);
    }
}
