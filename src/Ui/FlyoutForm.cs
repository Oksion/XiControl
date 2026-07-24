using System.Drawing.Drawing2D;

namespace XiControl.Ui;

/// <summary>
/// Палитра флайаутов (панель Mi-кнопки, OSD, «Монитор») — единственный источник их цветов.
/// Тема настраивается (Фаза 6.4): AppConfig.FlyoutTheme = null («тёмная», как исторически),
/// "light" или "system" (следовать теме Windows). Apply() пересчитывает флаг Dark; формы
/// берут цвета в момент отрисовки, поэтому смена темы — это Apply + Invalidate видимых окон.
/// Акценты одинаковы в обеих темах — из docs/10-colors.md.
/// </summary>
public static class FlyoutPalette
{
    /// <summary>Тёмная ли палитра флайаутов сейчас (см. Apply).</summary>
    public static bool Dark { get; private set; } = true;

    /// <summary>Пересчитать тему по настройке: null → тёмная (историческое поведение),
    /// "light" → светлая, "system" → как тема приложений Windows.</summary>
    public static void Apply(string? flyoutTheme) => Dark = flyoutTheme switch
    {
        "light" => false,
        "system" => Theme.IsDark(),
        _ => true,
    };

    public static Color Card => Dark ? Color.FromArgb(28, 28, 30) : Color.FromArgb(243, 243, 245);    // фон карточки
    public static Color Border => Dark ? Color.FromArgb(70, 70, 74) : Color.FromArgb(200, 200, 205);  // рамка по контуру
    public static Color Text => Dark ? Color.FromArgb(238, 238, 238) : Color.FromArgb(26, 26, 26);
    public static Color Dim => Dark ? Color.FromArgb(170, 170, 175) : Color.FromArgb(96, 96, 102);    // вторичный текст (≥4.5:1 к Card — WCAG AA)
    public static Color Cell => Dark ? Color.FromArgb(42, 42, 45) : Color.FromArgb(232, 232, 236);    // ячейка панели (чуть контрастнее карточки)
    public static Color CellHover => Dark ? Color.FromArgb(52, 52, 56) : Color.FromArgb(222, 222, 227);
    public static Color PlotBg => Dark ? Color.FromArgb(38, 38, 41) : Color.FromArgb(233, 233, 237);  // подложка графиков Монитора
    public static Color PlotGrid => Dark ? Color.FromArgb(50, 50, 54) : Color.FromArgb(216, 216, 221);

    // акценты — общие для обеих тем
    public static Color Green => Color.FromArgb(52, 199, 89);   // ок / заряд / тихий
    public static Color Blue => Color.FromArgb(90, 170, 255);   // авто / CPU
    public static Color Orange => Color.FromArgb(255, 149, 0);  // турбо / разряд / «в дорогу»
    public static Color Red => Color.FromArgb(255, 82, 82);     // полная мощность / выключено
}

/// <summary>
/// База флайаутов: borderless tool-window поверх всех окон (не светится в таскбаре и Alt-Tab),
/// скруглённый Region, тёмная карточка с рамкой, Esc прячет. OSD сюда сознательно не переведён —
/// он не активируется и закрывается затуханием, а не действиями пользователя.
/// </summary>
public abstract class FlyoutForm : Form
{
    protected FlyoutForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = FlyoutPalette.Card;
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

    protected float S => DeviceDpi / 96f;
    protected int Sc(float v) => (int)Math.Round(v * S);

    /// <summary>Окно переехало на монитор с другим DPI (PerMonitorV2 в манифесте). WinForms уже
    /// применил предложенные границы; подкласс пересчитывает свою геометрию под новый DeviceDpi —
    /// наши Sc/шрифты берутся от DeviceDpi, но кэшированная раскладка (ячейки, Region, Size)
    /// осталась бы в старом масштабе без перекомпоновки.</summary>
    protected virtual void OnDpiRescaled() { }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        OnDpiRescaled();
        Invalidate();
    }

    /// <summary>
    /// Скруглить окно под текущий Size. Прежний Region освобождаем сами:
    /// присваивание не отдаёт старый GDI-хэндл.
    /// </summary>
    protected void SetRoundedRegion(int corner)
    {
        var old = Region;
        using var path = Draw.Rounded(new Rectangle(0, 0, Width, Height), corner);
        Region = new Region(path);
        old?.Dispose();
    }

    /// <summary>Общий фон кадра: сглаживание, ClearType, заливка карточки и рамка по контуру.</summary>
    protected void PaintChrome(Graphics g, int corner)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(FlyoutPalette.Card);
        using var pen = new Pen(FlyoutPalette.Border);
        using var path = Draw.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), corner);
        g.DrawPath(pen, path);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Hide();
    }
}
