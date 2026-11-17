using System.Drawing.Drawing2D;

namespace XiControl.Ui;

/// <summary>Что показывает индикатор краёв тачпада.</summary>
public enum EdgeLevel { Brightness, Volume }

/// <summary>
/// Индикатор уровня в стиле системного окна громкости/яркости Windows 11 (XIC-97, issue #90):
/// плашка внизу по центру, значок, полоска акцентным цветом и число. Нужен для яркости с края
/// тачпада: громкость жестом Windows показывает своим окном (эмулируем клавиши), а для яркости
/// виртуальной клавиши нет — пишем через WMI, и система молчит. Автор #90 просил именно
/// «как системное», поэтому здесь не палитра флайаутов, а цвета Windows: тема панели задач
/// и акцент из реестра.
///
/// Живой: пока палец едет по краю, плашка обновляется на месте, не мигая; полоска доводится до
/// нового значения плавно; гаснет через <see cref="DurationMs"/> после последнего шага.
/// Не активируется и пропускает клики сквозь себя — как OSD.
/// </summary>
public sealed class LevelFlyout : Form
{
    private readonly System.Windows.Forms.Timer _hold = new() { Interval = 1500 };
    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 16 };

    private EdgeLevel _kind;
    private int _target;    // что выставлено
    private long _brightnessAt; // когда яркость в последний раз ставили мы — для отсева эха WMI
    private int _brightness;
    private float _shown;   // что нарисовано — догоняет _target
    private bool _fading;
    private bool _light;
    private Color _accent;

    /// <summary>Сколько висит после последнего шага, мс — общая настройка уведомлений.</summary>
    public int DurationMs { get => _hold.Interval; set => _hold.Interval = Math.Max(500, value); }

    private int Sc(float v) => (int)Math.Round(v * DeviceDpi / 96f);

    // Геометрия системного окна Windows 11 при 100 %: ~192×48, скругление 8
    private int W => Sc(192);
    private int H => Sc(48);
    private int Corner => Sc(8);

    public LevelFlyout()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;

        _hold.Tick += (_, _) => { _hold.Stop(); _fading = true; _anim.Start(); };
        _anim.Tick += (_, _) => Step();
    }

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

    /// <summary>Показать уровень 0–100. UI-поток. Повторный вызов во время показа — обновление на
    /// месте; другой край (яркость ↔ громкость) — та же плашка, без второго окна.</summary>
    public void ShowLevel(EdgeLevel kind, int percent)
    {
        _target = Math.Clamp(percent, 0, 100);
        if (kind == EdgeLevel.Brightness) { _brightness = _target; _brightnessAt = Environment.TickCount64; }
        if (kind != _kind) { _kind = kind; _shown = _target; } // с яркости на громкость не «едем»
        _hold.Stop();
        _fading = false;
        Opacity = 1;
        if (!Visible)
        {
            // тема и акцент — на каждый новый показ: их меняют в параметрах на ходу
            _light = Theme.TaskbarIsLight();
            _accent = WindowsAccent.Read(dark: !_light);
            _shown = _target; // с места не едем: старое значение могло быть чужим
            Size = new Size(W, H);
            SetRegion();
            var wa = OsdPlacement.TargetScreen().WorkingArea;
            Location = new Point(wa.Left + (wa.Width - W) / 2, wa.Bottom - H - Sc(OsdPlacement.Margin));
            Show();
        }
        _anim.Start();
        Invalidate();
        _hold.Start();
    }

    /// <summary>
    /// Уровень сменил не жест: Fn+F5/F6, клавиши громкости, другое приложение. Windows покажет
    /// своё окно ровно на этом месте — наше убираем сразу, а не ждём затухания. Яркость приходит
    /// и эхом наших же записей (WMI-событие отстаёт от шага), его отсеиваем по времени и уровню.
    /// </summary>
    public void Elsewhere(EdgeLevel kind, int level)
    {
        if (!Visible) return;
        if (kind == EdgeLevel.Brightness && IsOwnEcho(level, _brightness, Environment.TickCount64 - _brightnessAt)) return;
        _hold.Stop();
        _anim.Stop();
        Hide();
    }

    /// <summary>Событие яркости — эхо нашего шага: тот же уровень или пришло, пока жест ещё идёт.</summary>
    internal static bool IsOwnEcho(int level, int ours, long sinceOursMs) => level == ours || sinceOursMs < 600;

    private void Step()
    {
        if (_fading)
        {
            Opacity -= 0.08;
            if (Opacity <= 0.01) { _anim.Stop(); Hide(); }
            return;
        }
        _shown = Ease(_shown, _target);
        Invalidate();
        if (_shown == _target) _anim.Stop();
    }

    /// <summary>Шаг сглаживания полоски: треть остатка за кадр, последний полпроцента — сразу.</summary>
    internal static float Ease(float shown, int target)
    {
        float next = shown + (target - shown) / 3f;
        return Math.Abs(target - next) < 0.5f ? target : next;
    }

    private void SetRegion()
    {
        var old = Region;
        using var p = Draw.Rounded(new Rectangle(0, 0, Width, Height), Corner);
        Region = new Region(p);
        old?.Dispose(); // присваивание Region не освобождает прежний GDI-хэндл
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // цвета системного окна Windows 11 (снято глазами с Fn+F5/F6)
        Color bg = _light ? Color.FromArgb(249, 249, 249) : Color.FromArgb(44, 44, 44);
        Color border = _light ? Color.FromArgb(222, 222, 222) : Color.FromArgb(62, 62, 62);
        Color text = _light ? Color.FromArgb(26, 26, 26) : Color.FromArgb(255, 255, 255);
        Color track = _light ? Color.FromArgb(134, 134, 134) : Color.FromArgb(158, 158, 158);

        g.Clear(bg);
        using (var pen = new Pen(border))
        using (var path = Draw.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Corner))
            g.DrawPath(pen, path);

        // значок — системный глиф, ровно тот, что рисует сама Windows
        var iconBox = new Rectangle(Sc(14), 0, Sc(20), Height);
        TextRenderer.DrawText(g, Glyph(_kind, _target), IconFont, iconBox, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        // число справа, полоска между
        int numW = Sc(30);
        var numBox = new Rectangle(Width - Sc(12) - numW, 0, numW, Height);
        TextRenderer.DrawText(g, _target.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ScaledFonts.Get(DeviceDpi, "Segoe UI", 11f), numBox, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        int x0 = Sc(44), x1 = numBox.Left - Sc(10), th = Sc(4), y = (Height - th) / 2;
        using (var b = new SolidBrush(track))
        using (var path = Draw.Rounded(new Rectangle(x0, y, x1 - x0, th), th / 2))
            g.FillPath(b, path);
        int fill = (int)Math.Round((x1 - x0) * _shown / 100f);
        if (fill > 0)
        {
            using var b = new SolidBrush(_accent);
            using var path = Draw.Rounded(new Rectangle(x0, y, Math.Max(fill, th), th), th / 2);
            g.FillPath(b, path);
        }
    }

    /// <summary>Глиф Segoe Fluent Icons: яркость E706, громкость — E992..E995 по уровню, как у Windows.</summary>
    internal static string Glyph(EdgeLevel kind, int level) => kind == EdgeLevel.Brightness ? "\uE706" : level switch
    {
        0 => "\uE992",
        <= 33 => "\uE993",
        <= 66 => "\uE994",
        _ => "\uE995",
    };

    // Segoe Fluent Icons — шрифт значков Windows 11; на Windows 10 его нет, там тот же глиф в MDL2
    private static readonly Lazy<string> IconFamily = new(() =>
    {
        using var fonts = new System.Drawing.Text.InstalledFontCollection();
        return fonts.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    });

    private Font IconFont => ScaledFonts.Get(DeviceDpi, IconFamily.Value, 12f);

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _hold.Dispose(); _anim.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Акцентный цвет Windows — как его берёт сама система для полосок флайаутов: в тёмной теме
/// светлый оттенок палитры, в светлой — тёмный (<c>Explorer\Accent\AccentPalette</c>, восемь
/// цветов RGBA от Light3 до Dark3). Нет палитры — <c>DWM\AccentColor</c>, нет и его — синий
/// Windows по умолчанию.
/// </summary>
public static class WindowsAccent
{
    public static readonly Color Default = Color.FromArgb(0, 120, 212);

    public static Color Read(bool dark)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (FromPalette(k?.GetValue("AccentPalette") as byte[], dark) is Color c) return c;
            using var dwm = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (dwm?.GetValue("AccentColor") is int abgr) return FromAbgr(abgr);
        }
        catch (Exception ex) { Log.Ex("WindowsAccent", ex); }
        return Default;
    }

    /// <summary>Оттенок из AccentPalette: тёмная тема — Light2 (индекс 1), светлая — Dark1 (индекс 4).</summary>
    internal static Color? FromPalette(byte[]? palette, bool dark)
    {
        if (palette is not { Length: >= 32 }) return null;
        int i = (dark ? 1 : 4) * 4;
        return Color.FromArgb(palette[i], palette[i + 1], palette[i + 2]);
    }

    /// <summary>DWM хранит акцент как 0xAABBGGRR.</summary>
    internal static Color FromAbgr(int abgr) =>
        Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
}
