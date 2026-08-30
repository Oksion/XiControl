using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using XiControl.Config;
using XiControl.Localization;
using XiControl.SystemIntegration;

namespace XiControl.Ui;

/// <summary>
/// Индикатор-метрика в трее (XIC-35): ВТОРОЙ значок NotifyIcon, на котором цифрой рисуется
/// выбранная метрика (потребление/CPU/GPU/RAM/температура). Windows не даёт «подписать» значок
/// текстом — текст рендерится в саму иконку, как у TrafficMonitor и «процентов батареи».
/// Создаётся ТОЛЬКО при включённой опции: выключено → ни значка, ни таймера, ни источников —
/// ноль дополнительной нагрузки. Замер идёт на пуле (WorkerTimer: WMI не в UI-потоке),
/// отрисовка и Shell_NotifyIcon — в UI-потоке через маршал-контрол. Иконки — PNG-ICO в памяти,
/// без GDI-хэндлов (GetHicon/DestroyIcon не нужны, Icon.Dispose освобождает всё — приём RunCat365).
/// </summary>
public sealed class TrayMetricIcon : IDisposable
{
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    private const int SM_CXSMICON = 49;

    private readonly AppConfig _cfg;
    private readonly Control _ui;    // маршал в UI-поток (NotifyIcon живёт там)
    private readonly Action _click;  // левый клик — открыть «Монитор»
    private readonly IAppTimer _timer;
    private NotifyIcon? _tray;
    private TrayMetricSource? _source;
    private TrayMetric _kind;
    private Icon? _icon;
    private string? _text; // что сейчас на значке — без изменений Shell не дёргаем
    private bool _light;   // светлая ли панель задач (цвет цифры)
    private int _busy;         // защёлка от реентрантности тика (WMI бывает медленным)

    public TrayMetricIcon(AppConfig cfg, Control ui, Action openMonitor, IAppTimer? timer = null)
    {
        _cfg = cfg;
        _ui = ui;
        _click = openMonitor;
        _timer = timer ?? new WorkerTimer();
        _timer.Tick += OnTick;
    }

    /// <summary>Поднять индикатор (вызывать в UI-потоке). Значок появляется сразу с «—»,
    /// первое значение доедет с первого тика — не ждём период.</summary>
    public void Start()
    {
        _kind = TrayMetricFormat.ParseKind(_cfg.TrayMetricKind);
        _source = new TrayMetricSource(_kind, _cfg.ForceAcpiTemperature);
        _light = Theme.TaskbarIsLight();
        _tray = new NotifyIcon { Text = Loc.T("app.name") };
        Apply("—", null);
        _tray.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) _click(); };
        _tray.Visible = true;
        _timer.Interval = PeriodMs();
        _timer.Start();
        _ = Task.Run(OnTick); // первый замер сразу, но не в UI-потоке (DPTF — это WMI)
    }

    /// <summary>Вкладка настроек поменяла метрику/период (сам вкл/выкл решает TrayApp
    /// созданием/уничтожением всего объекта — выключено значит не существует).</summary>
    public void SettingsChanged()
    {
        var kind = TrayMetricFormat.ParseKind(_cfg.TrayMetricKind);
        if (kind != _kind)
        {
            _kind = kind;
            _source?.Dispose();
            _source = new TrayMetricSource(kind, _cfg.ForceAcpiTemperature);
            _text = null; // «12» ватт и «12» процентов — разные значения, перерисовать
        }
        _timer.Stop();
        _timer.Interval = PeriodMs();
        _timer.Start();
        _ = Task.Run(OnTick);
    }

    /// <summary>Сменилась тема панели задач — перекрасить цифру (вызывать в UI-потоке).</summary>
    public void ThemeChanged()
    {
        bool light = Theme.TaskbarIsLight();
        if (light == _light) return;
        _light = light;
        if (_text is string t) { _text = null; Apply(t, null); }
    }

    private int PeriodMs() => Math.Clamp(_cfg.TrayMetricPeriodSec, 1, 60) * 1000;

    // Тик на пуле: читаем источник и маршалим готовый текст в UI. Защёлка — на случай,
    // когда WMI-запрос идёт дольше периода (иначе тики бы наслаивались).
    private void OnTick()
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) return;
        try
        {
            var src = _source;
            if (src is null) return; // уже выключаемся
            float v = src.Read();
            string text = TrayMetricFormat.IconText(_kind, v);
            // флаги читаем сразу за Read — они про этот же тик
            string tip = Tip(v, src.PowerFromCpuPackage, src.TempFromAcpiZone);
            if (_ui.IsHandleCreated) _ui.BeginInvoke(new Action(() => Apply(text, tip)));
        }
        catch (Exception ex) { Log.Ex("TrayMetric", ex); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    // UI-поток: перерисовать значок, если текст изменился; тултип — каждый раз (он дешёвый).
    private void Apply(string text, string? tip)
    {
        if (_tray is null) return; // индикатор выключили, отложенный BeginInvoke догнал
        if (tip is not null) _tray.Text = tip.Length <= 127 ? tip : tip[..127];
        if (text == _text) return;
        _text = text;
        var old = _icon;
        _icon = Render(text, _light);
        _tray.Icon = _icon;
        old?.Dispose(); // порядок: присвоить новую → освободить старую
    }

    // Тултип: имя приложения • метрика: точное значение с единицами (форматы — из «Монитора»).
    // cpuPackage — значение пришло из RAPL вместо датчика батареи: величина другая, и назвать
    // её надо иначе, иначе ватты пакета CPU читаются как потребление всей системы.
    private string Tip(float v, bool cpuPackage, bool acpiZone)
    {
        // подменённую величину называем своим именем — иначе число читается как враньё
        string name = Loc.T((_kind, cpuPackage, acpiZone) switch
        {
            (TrayMetric.Power, true, _) => "traymetric.power.cpu",
            (TrayMetric.Temp, _, true) => "traymetric.temp.zone",
            _ => "traymetric." + TrayMetricFormat.Key(_kind),
        });
        string val = float.IsNaN(v) ? "—" : _kind switch
        {
            TrayMetric.Power => Loc.T("monitor.watts", MathF.Abs(v)),
            TrayMetric.Temp => Loc.T("monitor.temp.c", (int)MathF.Round(v)),
            _ => $"{v:0}%",
        };
        return $"{Loc.T("app.name")} • {name}: {val}";
    }

    // Цифра во весь значок: рендер строго в фактический размер трея (системный даунскейл размывает),
    // цвет — контраст к панели задач, как у основного значка (TrayIcons). Единицы — в тултипе:
    // двухэтажный вариант «число + единица» пробовали, на этом размере нижняя строка нечитаема.
    private static Icon Render(string text, bool lightTaskbar)
    {
        int size = Math.Max(16, GetSystemMetrics(SM_CXSMICON));
        Color col = lightTaskbar ? Color.FromArgb(32, 32, 32) : Color.FromArgb(240, 240, 240);
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            // размер шрифта под длину: 1–2 знака крупно, 3 — плотнее, 4 («100», «45°») — мельче
            float em = text.Length switch { <= 2 => size * 0.72f, 3 => size * 0.56f, _ => size * 0.46f };
            using var font = new Font("Segoe UI", em, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fmt = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
            };
            using var brush = new SolidBrush(col);
            // рамка шире битмапа: DrawString не должен переносить/сжимать — обрежет сам битмап
            g.DrawString(text, font, brush, new RectangleF(-size * 0.5f, 0, size * 2f, size), fmt);
        }
        return ToIcon(bmp);
    }

    // ICO с PNG-содержимым, собранный в памяти: полный 32bpp-альфа и никаких GDI-хэндлов —
    // Icon владеет своими данными, обычный Dispose освобождает всё (приём RunCat365).
    private static Icon ToIcon(Bitmap bmp)
    {
        using var png = new MemoryStream();
        bmp.Save(png, ImageFormat.Png);
        byte[] data = png.ToArray();

        using var ico = new MemoryStream();
        using var w = new BinaryWriter(ico);
        w.Write((short)0); w.Write((short)1); w.Write((short)1); // ICONDIR: резерв, тип «иконка», 1 кадр
        w.Write((byte)(bmp.Width >= 256 ? 0 : bmp.Width));
        w.Write((byte)(bmp.Height >= 256 ? 0 : bmp.Height));
        w.Write((byte)0); w.Write((byte)0);    // палитры нет, резерв
        w.Write((short)1); w.Write((short)32); // цветовые планы, бит на пиксель
        w.Write(data.Length); w.Write(22);     // размер PNG и его смещение (сразу за каталогом)
        w.Write(data);
        ico.Position = 0;
        return new Icon(ico);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _source?.Dispose();
        _source = null;
        if (_tray is not null)
        {
            _tray.Visible = false; // иначе «призрак» значка висит в трее до наведения мыши
            _tray.Dispose();
            _tray = null;
        }
        _icon?.Dispose();
        _icon = null;
    }
}
