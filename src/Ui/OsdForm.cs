using System.Drawing.Drawing2D;

namespace XiControl.Ui;

public enum OsdKind { Charging, ChargingLimited, OnBattery, Eco, Quiet, Auto, Turbo, Full, CareOn, CareOff, MicOn, MicOff, Backlight, BacklightMid, BacklightOff, BacklightAuto, FnLockOn, FnLockOff, CapsLockOn, CapsLockOff, RefreshRate, RefreshRateOff, Travel, TravelOff, TouchpadOn, TouchpadOff, TouchscreenOn, TouchscreenOff, AutoBrightOn, AutoBrightOff, Error }

/// <summary>Значок-оверлей качества зарядника поверх иконки заряда (независимо от лимита 80/100).</summary>
public enum ChargeBadge { None, Slow, NoPd }

/// <summary>
/// OSD-оверлей: тёмная скруглённая карточка по центру с иконкой и текстом,
/// плавно затухает. Не активируется и не перехватывает клики. Авто-размер под текст.
/// </summary>
public sealed class OsdForm : Form
{
    // палитра общая с флайаутами (FlyoutPalette) — тема настраивается (Фаза 6.4)
    private static Color Card => FlyoutPalette.Card;
    private static Color Border => FlyoutPalette.Border;
    private static Color TextCol => FlyoutPalette.Text;
    private static Color DimCol => FlyoutPalette.Dim;

    // сколько OSD висит до затухания. Единая база, чтобы всплывашки читались одинаково;
    // «Авто» чуть дольше (+600 мс) — стрелка спидометра успевает плавно «настроиться».
    // DurationMs настраивается из config.json (AppConfig.OsdDurationMs); клэмп снизу,
    // чтобы всплывашка не мигала мгновенно. TrayApp выставляет её на старте.
    private int _displayMs = 2800;
    public int DurationMs { get => _displayMs; set => _displayMs = Math.Max(500, value); }
    private int DisplayMsAuto => _displayMs + 600;

    private readonly System.Windows.Forms.Timer _display = new() { Interval = 2800 };
    private readonly System.Windows.Forms.Timer _fade = new() { Interval = 16 };

    // шрифты — от текущего DPI (как и вся геометрия Sc): пропорции с иконкой
    // не разъезжаются после смены разрешения/масштаба экрана
    private Font TitleFont => ScaledFonts.Get(DeviceDpi, "Segoe UI Semibold", 12.5f);
    private Font SubFont => ScaledFonts.Get(DeviceDpi, "Segoe UI", 9f);

    private OsdKind _kind;
    private string _title = "";
    private string? _sub;
    private ChargeBadge _badge; // оверлей поверх иконки заряда (медленно/нет PD), None — без него

    // все размеры — через Sc(): на HiDPI шрифты масштабируются системой,
    // и иконка с отступами должны расти вместе с ними
    private int Sc(float v) => (int)Math.Round(v * DeviceDpi / 96f);
    private int IconSize => Sc(64);
    private int PadX => Sc(28);
    private int PadTop => Sc(22);
    private int GapIcon => Sc(10);
    private int GapText => Sc(4);
    private int PadBottom => Sc(18);
    private int MinWidth => Sc(240);
    private int Corner => Sc(18);

    public OsdForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Card;
        Size = new Size(240, 152);

        _display.Tick += (_, _) => { _display.Stop(); _fade.Start(); };
        _fade.Tick += (_, _) =>
        {
            Opacity -= 0.08;
            if (Opacity <= 0.01) { _fade.Stop(); _gauge.Stop(); Hide(); }
        };
        _gauge.Tick += (_, _) => { _gaugeT += 0.03f; Invalidate(); };
    }

    // ---- «настройка» спидометра (Авто): медленный ход стрелки через всю шкалу ----
    // Шкала циферблата: зелёный край ≈ +45° поворота стрелки, красный ≈ −115°;
    // качаем синусом вокруг середины, старт из исходного положения стрелки.
    private readonly System.Windows.Forms.Timer _gauge = new() { Interval = 30 };
    private float _gaugeT;

    internal static float SweepAngle(float t)
        => -10f + 30f * MathF.Sin(0.34f + t * 1.4f); // мягкий ход ±30°, период ~4.5 с

    private float NeedleAngle() => SweepAngle(_gaugeT);

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TRANSPARENT = 0x20;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        var old = Region;
        using var p = Draw.Rounded(new Rectangle(0, 0, Width, Height), Corner);
        Region = new Region(p);
        old?.Dispose(); // присваивание Region не освобождает прежний GDI-хэндл
    }

    private (int w, int h, int titleH, int subH) Measure()
    {
        var t = TextRenderer.MeasureText(_title, TitleFont);
        int subH = 0, subW = 0;
        if (!string.IsNullOrEmpty(_sub))
        {
            var s = TextRenderer.MeasureText(_sub, SubFont);
            subH = s.Height; subW = s.Width;
        }
        int content = Math.Max(IconSize, Math.Max(t.Width, subW));
        int w = Math.Max(MinWidth, content + PadX * 2);
        int h = PadTop + IconSize + GapIcon + t.Height + (subH > 0 ? GapText + subH : 0) + PadBottom;
        return (w, h, t.Height, subH);
    }

    /// <summary>Показать OSD (перезапускает таймер показа).</summary>
    public void Flash(OsdKind kind, string title, string? subtitle = null, ChargeBadge badge = ChargeBadge.None)
    {
        _kind = kind; _title = title; _sub = subtitle; _badge = badge;

        var (w, h, _, _) = Measure();
        Size = new Size(w, h);

        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Left + (wa.Width - w) / 2, wa.Top + (int)(wa.Height * 0.60));

        _display.Stop(); _fade.Stop();
        Opacity = 1.0;
        _gauge.Stop();
        // для «Авто» показываем дольше — стрелка успевает плавно «настроиться»
        _display.Interval = kind == OsdKind.Auto ? DisplayMsAuto : _displayMs;
        if (IsAnimated(kind)) { _gaugeT = 0f; _gauge.Start(); }
        Invalidate();
        if (!Visible) Show();
        else BringToFront();
        _display.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        g.Clear(Card);
        using (var pen = new Pen(Border))
        using (var path = Draw.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Corner))
            g.DrawPath(pen, path);

        var (_, _, titleH, subH) = Measure();
        int y = PadTop;
        DrawIcon(g, _kind, new RectangleF((Width - IconSize) / 2f, y, IconSize, IconSize));
        y += IconSize + GapIcon;

        TextRenderer.DrawText(g, _title, TitleFont,
            new Rectangle(0, y, Width, titleH), TextCol,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
        y += titleH + GapText;

        if (subH > 0)
            TextRenderer.DrawText(g, _sub, SubFont,
                new Rectangle(0, y, Width, subH), DimCol,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
    }

    private static bool IsAnimated(OsdKind kind) => kind is
        OsdKind.Auto or OsdKind.Quiet or OsdKind.Turbo or OsdKind.Full or OsdKind.Eco or OsdKind.Charging or OsdKind.Travel;

    private void DrawIcon(Graphics g, OsdKind kind, RectangleF r)
    {
        DrawBase(g, kind, r);
        // оверлей качества зарядника: база несёт лимит 80/100 (Charging/ChargingLimited/Travel),
        // бейдж — только мощность. Бейджи нарисованы в полном кадре 128×128 уже на своём месте
        // (угол), поэтому накладываем 1:1 в тот же прямоугольник, что и базу — сходится точно.
        if (_badge != ChargeBadge.None)
            SvgIcons.Draw(g, _badge == ChargeBadge.Slow ? SvgIcons.BadgeSlow : SvgIcons.BadgeNoPd, r);
    }

    private void DrawBase(Graphics g, OsdKind kind, RectangleF r)
    {
        switch (kind)
        {
            case OsdKind.Auto:     SvgIcons.DrawGauge(g, r, NeedleAngle()); return;
            case OsdKind.Quiet:    SvgIcons.DrawMoonTwinkle(g, r, _gaugeT, 1f); return;
            case OsdKind.Turbo:    SvgIcons.DrawBoltPulse(g, r, _gaugeT, 1f); return;
            case OsdKind.Full:     SvgIcons.DrawRocket(g, r, _gaugeT, 1f); return;
            case OsdKind.Eco:      SvgIcons.DrawLeafSway(g, r, _gaugeT, 1f); return;
            case OsdKind.Charging: SvgIcons.DrawChargingPulse(g, r, _gaugeT); return;
            case OsdKind.Travel:   SvgIcons.DrawTravelPulse(g, r, _gaugeT); return;
        }
        string name = kind switch
        {
            OsdKind.Charging        => SvgIcons.BatteryCharging,
            OsdKind.ChargingLimited => SvgIcons.BatterySaverOn,
            OsdKind.OnBattery       => SvgIcons.BatteryDischarge,
            OsdKind.CareOn          => SvgIcons.BatterySaverOn,
            OsdKind.CareOff         => SvgIcons.BatterySaverOff,
            OsdKind.Eco             => SvgIcons.PerfEco,
            OsdKind.Quiet           => SvgIcons.PerfQuiet,
            OsdKind.Auto            => SvgIcons.PerfAuto,
            OsdKind.Turbo           => SvgIcons.PerfTurbo,
            OsdKind.Full            => SvgIcons.PerfFull,
            OsdKind.MicOn           => SvgIcons.MicOn,
            OsdKind.MicOff          => SvgIcons.MicOff,
            OsdKind.Backlight       => SvgIcons.KeyboardBacklight,
            OsdKind.BacklightMid    => SvgIcons.KeyboardBacklight50,
            OsdKind.BacklightOff    => SvgIcons.KeyboardBacklightOff,
            OsdKind.BacklightAuto   => SvgIcons.KeyboardBacklightAuto,
            OsdKind.FnLockOn        => SvgIcons.FnLockOn,
            OsdKind.FnLockOff       => SvgIcons.FnLockOff,
            OsdKind.CapsLockOn      => SvgIcons.CapsLockOn,
            OsdKind.CapsLockOff     => SvgIcons.CapsLockOff,
            OsdKind.RefreshRate     => SvgIcons.RefreshRate,
            OsdKind.RefreshRateOff  => SvgIcons.RefreshRateOff,
            OsdKind.Travel          => SvgIcons.Travel,
            OsdKind.TravelOff       => SvgIcons.TravelOff,
            OsdKind.TouchpadOn      => SvgIcons.Touchpad,
            OsdKind.TouchpadOff     => SvgIcons.TouchpadOff,
            OsdKind.TouchscreenOn   => SvgIcons.Touchscreen,
            OsdKind.TouchscreenOff  => SvgIcons.TouchscreenOff,
            OsdKind.AutoBrightOn    => SvgIcons.AutoBrightness,
            OsdKind.AutoBrightOff   => SvgIcons.AutoBrightnessOff,
            _ => SvgIcons.Settings,
        };
        SvgIcons.Draw(g, name, r);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _display.Dispose(); _fade.Dispose(); _gauge.Dispose();
            // шрифты общие (кэш ScaledFonts) — не диспозим
        }
        base.Dispose(disposing);
    }
}
