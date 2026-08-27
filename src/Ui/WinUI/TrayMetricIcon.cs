using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;
using XiControl.Config;
using XiControl.Localization;
using XiControl.SystemIntegration;

namespace XiControl.Ui;

public sealed class TrayMetricIcon : IDisposable
{
    private const int SmCxSmIcon = 49;
    private readonly AppConfig _cfg;
    private readonly Action _click;
    private readonly DispatcherQueue _queue;
    private readonly IAppTimer _timer;
    private NativeTrayIcon? _tray;
    private TrayMetricSource? _source;
    private TrayMetric _kind;
    private Icon? _icon;
    private string? _text;
    private bool _light;
    private int _busy;
    private readonly object _sourceLock = new();

    public TrayMetricIcon(AppConfig cfg, Action openMonitor, IAppTimer? timer = null)
    {
        _cfg = cfg;
        _click = openMonitor;
        _queue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("TrayMetricIcon должен создаваться в WinUI-потоке.");
        _timer = timer ?? new WorkerTimer();
        _timer.Tick += OnTick;
    }

    public void Start()
    {
        _kind = TrayMetricFormat.ParseKind(_cfg.TrayMetricKind);
        _source = new TrayMetricSource(_kind);
        _light = Theme.TaskbarIsLight();
        _tray = new NativeTrayIcon(2);
        _tray.Activated += _click;
        Apply("—", Loc.T("app.name"));
        _tray.Show();
        _timer.Interval = PeriodMs();
        _timer.Start();
        _ = Task.Run(OnTick);
    }

    public void SettingsChanged()
    {
        TrayMetric kind = TrayMetricFormat.ParseKind(_cfg.TrayMetricKind);
        if (kind != _kind)
        {
            _kind = kind;
            lock (_sourceLock)
            {
                _source?.Dispose();
                _source = new TrayMetricSource(kind);
            }
            _text = null;
        }
        _timer.Stop();
        _timer.Interval = PeriodMs();
        _timer.Start();
        _ = Task.Run(OnTick);
    }

    public void ThemeChanged()
    {
        bool light = Theme.TaskbarIsLight();
        if (light == _light) return;
        _light = light;
        if (_text is string text) { _text = null; Apply(text, null); }
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_sourceLock)
        {
            _source?.Dispose();
            _source = null;
        }
        _tray?.Dispose();
        _tray = null;
        _icon?.Dispose();
        _icon = null;
    }

    private int PeriodMs() => Math.Clamp(_cfg.TrayMetricPeriodSec, 1, 60) * 1000;

    private void OnTick()
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) return;
        try
        {
            float value;
            lock (_sourceLock)
            {
                if (_source is null) return;
                value = _source.Read();
            }
            string text = TrayMetricFormat.IconText(_kind, value);
            string tip = Tip(value);
            _queue.TryEnqueue(() => Apply(text, tip));
        }
        catch (Exception ex) { Log.Ex("TrayMetric", ex); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private void Apply(string text, string? tooltip)
    {
        if (_tray is null) return;
        if (tooltip is not null) _tray.Tooltip = tooltip;
        if (text == _text) return;
        _text = text;
        Icon? old = _icon;
        _icon = Render(text, _light);
        _tray.Icon = _icon;
        old?.Dispose();
    }

    private string Tip(float value)
    {
        string name = Loc.T("traymetric." + TrayMetricFormat.Key(_kind));
        string val = float.IsNaN(value) ? "—" : _kind switch
        {
            TrayMetric.Power => Loc.T("monitor.watts", MathF.Abs(value)),
            TrayMetric.Temp => Loc.T("monitor.temp.c", (int)MathF.Round(value)),
            _ => $"{value:0}%",
        };
        return $"{Loc.T("app.name")} • {name}: {val}";
    }

    private static Icon Render(string text, bool lightTaskbar)
    {
        int size = Math.Max(16, GetSystemMetrics(SmCxSmIcon));
        Color color = lightTaskbar ? Color.FromArgb(32, 32, 32) : Color.FromArgb(240, 240, 240);
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            float em = text.Length switch { <= 2 => size * 0.72f, 3 => size * 0.56f, _ => size * 0.46f };
            using var font = new Font("Segoe UI", em, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
            };
            using var brush = new SolidBrush(color);
            graphics.DrawString(text, font, brush, new RectangleF(-size * 0.5f, 0, size * 2f, size), format);
        }
        return ToIcon(bitmap);
    }

    private static Icon ToIcon(Bitmap bitmap)
    {
        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        byte[] data = png.ToArray();
        using var ico = new MemoryStream();
        using var writer = new BinaryWriter(ico, Encoding.UTF8, leaveOpen: true);
        writer.Write((short)0); writer.Write((short)1); writer.Write((short)1);
        writer.Write((byte)(bitmap.Width >= 256 ? 0 : bitmap.Width));
        writer.Write((byte)(bitmap.Height >= 256 ? 0 : bitmap.Height));
        writer.Write((byte)0); writer.Write((byte)0);
        writer.Write((short)1); writer.Write((short)32);
        writer.Write(data.Length); writer.Write(22);
        writer.Write(data);
        writer.Flush();
        ico.Position = 0;
        return new Icon(ico);
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
