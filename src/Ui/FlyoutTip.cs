namespace XiControl.Ui;

/// <summary>
/// Тултип флайаутов: WinForms ToolTip в OwnerDraw, покрашенный в FlyoutPalette — в тон
/// панели/«Монитору» и уважает настройку темы флайаутов. Отрисовка идёт в окне самого
/// тултипа (двумя обработчиками), наш GDI+-холст не трогается. Показ — с задержкой
/// ~500 мс (dwell), как у системных: ручной ToolTip.Show() сам по себе мгновенный.
/// Ячейки флайаутов — не контролы, поэтому SetToolTip не подходит, только ручной показ.
/// </summary>
public sealed class FlyoutTip : IDisposable
{
    private const int DwellMs = 500;   // системная InitialDelay ≈ 0.5 с
    private const int ShowMs = 4000;

    private readonly Control _owner;
    private readonly ToolTip _tip = new() { OwnerDraw = true };
    private readonly System.Windows.Forms.Timer _dwell = new() { Interval = DwellMs };
    private string? _pending; // текст, ждущий показа (null — ничего)
    private Point _at;        // позиция курсора на момент последнего Update
    private bool _visible;

    private Font TipFont => ScaledFonts.Get(_owner.DeviceDpi, "Segoe UI", 9f);
    private int Sc(float v) => (int)Math.Round(v * _owner.DeviceDpi / 96f);

    public FlyoutTip(Control owner)
    {
        _owner = owner;
        _dwell.Tick += (_, _) =>
        {
            _dwell.Stop();
            if (_pending is null) return;
            _tip.Show(_pending, _owner, _at.X, _at.Y + Sc(22), ShowMs); // чуть ниже курсора
            _visible = true;
        };
        // размер плашки — по нашему шрифту (дефолтный замер OwnerDraw не знает о DPI-шрифте)
        _tip.Popup += (_, e) =>
        {
            var sz = TextRenderer.MeasureText(_pending ?? "", TipFont);
            e.ToolTipSize = new Size(sz.Width + Sc(16), sz.Height + Sc(10));
        };
        _tip.Draw += (_, e) =>
        {
            using var bg = new SolidBrush(FlyoutPalette.Card);
            e.Graphics.FillRectangle(bg, e.Bounds);
            using var pen = new Pen(FlyoutPalette.Border);
            e.Graphics.DrawRectangle(pen, new Rectangle(0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1));
            TextRenderer.DrawText(e.Graphics, e.ToolTipText, TipFont, e.Bounds, FlyoutPalette.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
    }

    /// <summary>Смена hover-ячейки: text — подсказка (null — спрятать). Показ после dwell-паузы;
    /// та же подсказка, что уже видна, повторно не показывается (не мигает).</summary>
    public void Update(string? text, Point at)
    {
        _at = at;
        if (text is null) { Hide(); return; }
        if (_visible && text == _pending) return;
        _pending = text;
        if (_visible) { _tip.Hide(_owner); _visible = false; }
        _dwell.Stop();
        _dwell.Start();
    }

    /// <summary>Спрятать и отменить отложенный показ (уход мыши / скрытие окна).</summary>
    public void Hide()
    {
        _dwell.Stop();
        _pending = null;
        if (_visible) { _tip.Hide(_owner); _visible = false; }
    }

    public void Dispose()
    {
        _dwell.Dispose();
        _tip.Dispose();
    }
}
