using System.Drawing.Drawing2D;
using XiControl.Localization;

namespace XiControl.Ui.Settings;

/// <summary>
/// Фабрика виджетов окна настроек: карточки-строки, тумблеры, комбо, поля, заметки.
/// Держит палитру, геометрию (Sc от DPI формы-владельца) и шрифты. Живёт одну пересборку
/// окна — на следующую создаётся заново со свежей темой. Фаза 2.3 переиспользует его
/// для QuickPanel/Monitor.
/// </summary>
public sealed class SettingsToolkit
{
    private readonly Form _owner; // только ради DeviceDpi и шрифтов
    public readonly SettingsTheme T;

    public SettingsToolkit(Form owner, SettingsTheme theme)
    {
        _owner = owner;
        T = theme;
    }

    // ---- Геометрия ----

    public float S => _owner.DeviceDpi / 96f;
    public int Sc(float v) => (int)Math.Round(v * S);

    /// <summary>Ширина строки-карточки: окно минус навигация, поля и полоса прокрутки.</summary>
    public int RowW => Sc(824) - Sc(212) - Sc(52) - Sc(16);

    // ---- Шрифты — из кэша ScaledFonts под текущий DPI (Label шрифтом не владеет, в OnPaint
    // не создаём): пропорции с геометрией Sc держатся и после смены разрешения/масштаба ----

    private const string Ui = "Segoe UI";
    private const string UiSemibold = "Segoe UI Semibold";

    public Font HeadFont => ScaledFonts.Get(_owner.DeviceDpi, UiSemibold, 15f);
    public Font NameFont => ScaledFonts.Get(_owner.DeviceDpi, UiSemibold, 14f);
    public Font GroupFont => ScaledFonts.Get(_owner.DeviceDpi, UiSemibold, 9.5f);
    public Font TitleFont => ScaledFonts.Get(_owner.DeviceDpi, Ui, 10f);
    public Font DescFont => ScaledFonts.Get(_owner.DeviceDpi, Ui, 8.5f);
    public Font NoteFont => ScaledFonts.Get(_owner.DeviceDpi, Ui, 9f);
    public Font CtlFont => ScaledFonts.Get(_owner.DeviceDpi, Ui, 9.5f);

    // ---- Строки ----

    public void AddHeader(Panel p, string titleKey, string subKey)
    {
        p.Controls.Add(new Label
        {
            Text = Loc.T(titleKey),
            Font = HeadFont,
            AutoSize = true,
            ForeColor = T.Text,
            BackColor = Color.Transparent,
            Margin = new Padding(2, 0, 0, Sc(2)),
        });
        p.Controls.Add(new Label
        {
            Text = Loc.T(subKey),
            Tag = "dim",
            AutoSize = true,
            MaximumSize = new Size(RowW, 0),
            ForeColor = T.Text2,
            BackColor = Color.Transparent,
            Margin = new Padding(2, 0, 0, Sc(14)),
        });
    }

    public void AddGroup(Panel p, string key) => p.Controls.Add(new Label
    {
        Text = Loc.T(key),
        Font = GroupFont,
        AutoSize = true,
        ForeColor = T.Text2,
        BackColor = Color.Transparent,
        Margin = new Padding(2, Sc(14), 0, Sc(6)),
    });

    public void AddRow(Panel p, string titleKey, string descKey, Control ctl)
    {
        // экранный диктор: контрол называется как заголовок своей строки
        if (string.IsNullOrEmpty(ctl.AccessibleName)) ctl.AccessibleName = Loc.T(titleKey);
        p.Controls.Add(Row(Loc.T(titleKey), Loc.T(descKey), ctl));
    }

    public Panel Row(string title, string desc, Control ctl)
    {
        // ширина под текст, чтобы не залезть под контрол; описание меряем и растим карточку по факту
        int textW = Math.Max(Sc(120), RowW - ctl.Width - Sc(48));
        int descH = string.IsNullOrEmpty(desc)
            ? 0
            : TextRenderer.MeasureText(desc, DescFont, new Size(textW, 0), TextFormatFlags.WordBreak).Height;
        int h = string.IsNullOrEmpty(desc) ? Sc(52) : Sc(29) + descH + Sc(14);

        var card = new Panel { Width = RowW, Height = h, BackColor = T.Card, Margin = new Padding(0, 0, 0, Sc(4)), Tag = "cardrow" };
        card.Region = new Region(Draw.Rounded(new RectangleF(0, 0, RowW, h), Sc(6)));
        card.Paint += (_, e) => PaintCardBorder(e.Graphics, RowW, h);

        var t = new Label { Text = title, AutoSize = false, Width = textW, Height = Sc(20), ForeColor = T.Text, BackColor = Color.Transparent, Font = TitleFont, Location = new Point(Sc(16), Sc(9)), AutoEllipsis = true };
        card.Controls.Add(t);
        if (!string.IsNullOrEmpty(desc))
        {
            var d = new Label { Text = desc, Tag = "dim", AutoSize = false, Width = textW, Height = descH + Sc(2), ForeColor = T.Text2, BackColor = Color.Transparent, Font = DescFont, Location = new Point(Sc(16), Sc(29)) };
            card.Controls.Add(d);
        }
        card.Controls.Add(ctl);
        ctl.Location = new Point(RowW - ctl.Width - Sc(16), (h - ctl.Height) / 2);
        ctl.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        return card;
    }

    // Инфо-плашка: сглаженная скруглённая заливка + текст рисуем сами (без Region — иначе рваные
    // углы, и без дочернего Label — иначе прозрачность даёт «ореол» неверного фона).
    public void AddNote(Panel p, string key)
    {
        string text = Loc.T(key);
        int textW = RowW - Sc(28);
        int textH = TextRenderer.MeasureText(text, NoteFont, new Size(textW, 0), TextFormatFlags.WordBreak).Height;

        var note = new Panel { Width = RowW, Height = textH + Sc(26), BackColor = T.WinBg, Margin = new Padding(0, Sc(2), 0, Sc(4)) };
        note.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Draw.Rounded(new RectangleF(0.5f, 0.5f, RowW - 1.5f, note.Height - 1.5f), Sc(6)))
            using (var fill = new SolidBrush(T.Sel))
                g.FillPath(fill, path);
            TextRenderer.DrawText(g, text, NoteFont, new Rectangle(Sc(14), Sc(13), textW, note.Height - Sc(26)),
                T.Text2, TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.Top);
        };
        p.Controls.Add(note);
    }

    public void AddKv(Panel p, string key, string val)
    {
        var row = new Panel { Width = RowW, Height = Sc(34), BackColor = T.WinBg, Margin = new Padding(0) };
        row.Paint += (_, e) => { using var pen = new Pen(T.Border); e.Graphics.DrawLine(pen, 0, row.Height - 1, RowW, row.Height - 1); };
        row.Controls.Add(new Label { Text = Loc.T(key), Tag = "dim", AutoSize = true, ForeColor = T.Text2, BackColor = Color.Transparent, Location = new Point(Sc(2), Sc(9)) });
        row.Controls.Add(new Label { Text = val, AutoSize = true, ForeColor = T.Text, BackColor = Color.Transparent, Location = new Point(Sc(180), Sc(9)) });
        p.Controls.Add(row);
    }

    public Panel SubRow(string titleKey, Control ctl)
    {
        if (string.IsNullOrEmpty(ctl.AccessibleName)) ctl.AccessibleName = Loc.T(titleKey);
        // ширина минус отступ: Margin.Left + RowW вылезал за клиентскую область — FlowLayoutPanel
        // добавлял горизонтальную полосу прокрутки («странный скролл» на Производительности)
        int w = RowW - Sc(30);
        var row = new Panel { Width = w, Height = Sc(42), BackColor = T.WinBg, Margin = new Padding(Sc(30), 0, 0, 0) };
        row.Controls.Add(new Label { Text = Loc.T(titleKey), AutoSize = true, ForeColor = T.Text, BackColor = Color.Transparent, Font = CtlFont, Location = new Point(Sc(2), Sc(11)) });
        ctl.Location = new Point(w - ctl.Width - Sc(16), (Sc(42) - ctl.Height) / 2);
        ctl.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        row.Controls.Add(ctl);
        return row;
    }

    // Правый read-only индикатор для инфо-строк (как контрол в AddRow, но без интерактива).
    public Label ValueLabel(string text)
    {
        int w = TextRenderer.MeasureText(text, CtlFont).Width + Sc(4);
        return new Label
        {
            Text = text,
            AutoSize = false,
            Width = w,
            Height = Sc(22),
            TextAlign = ContentAlignment.MiddleRight,
            Font = CtlFont,
            ForeColor = T.Text,
            BackColor = Color.Transparent,
        };
    }

    // ---- Контролы ----

    public ToggleSwitch Toggle(bool on, Action<bool> changed)
    {
        var t = new ToggleSwitch
        {
            Size = new Size(Sc(40), Sc(20)),
            Checked = on,
            BackColor = T.Card,
            Accent = T.Accent,
            OnKnob = T.Dark ? Color.FromArgb(0, 45, 74) : Color.White,
            OffLine = T.Dark ? Color.FromArgb(160, 160, 160) : Color.FromArgb(120, 120, 120),
        };
        t.CheckedChanged += (_, _) => changed(t.Checked);
        return t;
    }

    public ComboBox Combo(string[] items, int index, Action<int> changed, int width)
    {
        var cb = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = Sc(22),
            Width = width,
            BackColor = T.Field,
            ForeColor = T.Text,
            Font = CtlFont,
        };
        cb.Items.AddRange(items);
        // тёмная тема: нативный combo игнорирует BackColor — рисуем сами (и закрытый бокс, и список)
        cb.DrawItem += (_, e) =>
        {
            // закрытое «поле» (ComboBoxEdit) всегда нейтральное; акцент — только для элементов списка
            bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
            bool sel = !edit && (e.State & DrawItemState.Selected) != 0;
            using var bg = new SolidBrush(sel ? T.Accent : T.Field);
            e.Graphics.FillRectangle(bg, e.Bounds);
            if (e.Index >= 0)
                TextRenderer.DrawText(e.Graphics, cb.Items[e.Index]?.ToString() ?? "", cb.Font, e.Bounds,
                    sel ? (T.Dark ? Color.FromArgb(0, 45, 74) : Color.White) : T.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.LeftAndRightPadding);
        };
        if (index >= 0 && index < items.Length) cb.SelectedIndex = index;
        cb.SelectedIndexChanged += (_, _) => { if (cb.SelectedIndex >= 0) changed(cb.SelectedIndex); };
        // колесо над ЗАКРЫТЫМ комбо — скроллим вкладку, а не крутим значение: иначе прокрутка
        // страницы случайно меняла настройку (язык, частоту), стоило курсору попасть на комбо.
        // Раскрытый список листается колесом как обычно.
        cb.MouseWheel += (_, e) =>
        {
            if (cb.DroppedDown) return;
            ((HandledMouseEventArgs)e).Handled = true;
            for (Control? p = cb.Parent; p is not null; p = p.Parent)
                if (p is ScrollableControl { AutoScroll: true } pane)
                {
                    pane.AutoScrollPosition = new Point(0, -pane.AutoScrollPosition.Y - e.Delta);
                    break;
                }
        };
        return cb;
    }

    public TextBox TextField(string val, int width, Action<string> changed)
    {
        var tb = new TextBox
        {
            Text = val,
            Width = width,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = T.Field,
            ForeColor = T.Text,
            Font = CtlFont,
        };
        tb.Leave += (_, _) => changed(tb.Text);
        return tb;
    }

    public Panel Pair(Control a, Control b)
    {
        var host = new Panel { Width = a.Width + b.Width + Sc(8), Height = Math.Max(a.Height, b.Height), BackColor = Color.Transparent };
        a.Location = new Point(0, (host.Height - a.Height) / 2);
        b.Location = new Point(a.Width + Sc(8), (host.Height - b.Height) / 2);
        host.Controls.Add(a); host.Controls.Add(b);
        return host;
    }

    public Button LinkButton(string keyOrText, Action click)
    {
        var b = new Button
        {
            Text = keyOrText.StartsWith("settings.", StringComparison.Ordinal) || keyOrText.StartsWith("app.", StringComparison.Ordinal) ? Loc.T(keyOrText) : keyOrText,
            AutoSize = false,
            Height = Sc(30),
            Width = Sc(0),
            Padding = new Padding(Sc(6), 0, Sc(6), 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = T.Card,
            ForeColor = T.Text,
            Font = CtlFont,
            Margin = new Padding(0, 0, Sc(8), 0),
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = T.Border;
        b.AutoSize = true;
        b.Click += (_, _) => click();
        return b;
    }

    // карточка обрезана по Region (скруглённая), фон = BackColor(Card); здесь только рамка
    public void PaintCardBorder(Graphics g, int w, int h)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Draw.Rounded(new RectangleF(0.5f, 0.5f, w - 1.5f, h - 1.5f), Sc(6));
        using var pen = new Pen(T.Border);
        g.DrawPath(pen, path);
    }
}
