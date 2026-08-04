using System.Drawing.Drawing2D;
using XiControl.Config;
using XiControl.Localization;
using XiControl.SystemIntegration;

namespace XiControl.Ui.Settings;

/// <summary>
/// Вкладка «Экран»: яркость (лимит XIC-29, авто-яркость по датчику XIC-30, запоминание)
/// и авто-герцовка. Вкладка видна всегда; при выключенной фиче «управление частотой»
/// скрывается только раздел частоты. Живой блок авто-яркости (люксы + график кривой)
/// обновляется секундным таймером, пока вкладка существует.
/// </summary>
public sealed class DisplayTab : SettingsPane
{
    private readonly UiTimer _live = new() { Interval = 1000 };
    private readonly AppConfig _cfg; // графику нужны живые лимиты (XIC-29) при перерисовке

    public DisplayTab(SettingsToolkit ui, AppConfig cfg, SettingsActions act, Action rebuild) : base(ui)
    {
        _cfg = cfg;
        ui.AddHeader(this, "settings.tab.display", "settings.display.sub");

        // ---- Яркость ----
        ui.AddGroup(this, "settings.bright.group");
        // rebuild — зажечь/погасить комбо лимитов и плашку про адаптивную яркость
        ui.AddRow(this, "settings.bright.cap", "settings.bright.cap.desc",
            ui.Toggle(cfg.BrightnessCapEnabled, on => { act.SetBrightnessCap(on); rebuild(); }));
        var capAc = PercentCombo(cfg.BrightnessCapAc, v => act.SetBrightnessCaps(v, cfg.BrightnessCapBattery));
        capAc.Enabled = cfg.BrightnessCapEnabled;
        ui.AddRow(this, "settings.bright.cap.ac", "settings.bright.cap.ac.desc", capAc);
        var capBatt = PercentCombo(cfg.BrightnessCapBattery, v => act.SetBrightnessCaps(cfg.BrightnessCapAc, v));
        capBatt.Enabled = cfg.BrightnessCapEnabled;
        ui.AddRow(this, "settings.bright.cap.battery", "settings.bright.cap.battery.desc", capBatt);

        // авто-яркость по датчику (XIC-30) — только на машинах с датчиком; Available
        // выясняется в фоне на старте, к открытию окна ответ обычно уже есть
        Label? luxValue = null;
        if (act.IsAlsAvailable())
        {
            ui.AddRow(this, "settings.bright.auto", "settings.bright.auto.desc",
                ui.Toggle(cfg.AutoBrightness, on => { act.SetAutoBrightness(on); rebuild(); }));

            // живые люксы: пользователю видно, что датчик и фича работают
            luxValue = new Label
            {
                AutoSize = false,
                Width = ui.Sc(90),
                Height = ui.Sc(22),
                TextAlign = ContentAlignment.MiddleRight,
                Font = ui.CtlFont,
                ForeColor = ui.T.Text,
                BackColor = Color.Transparent,
                Text = LuxText(act.CurrentLux()),
            };
            ui.AddRow(this, "settings.bright.lux", "settings.bright.lux.desc", luxValue);

            Panel? graph = null;
            if (cfg.AutoBrightness)
            {
                graph = CurveGraph(act);
                Controls.Add(graph);
                // сброс обучения — только явной кнопкой: выключение фичи кривую не трогает
                ui.AddRow(this, "settings.bright.curve.reset", "settings.bright.curve.reset.desc",
                    ui.LinkButton("settings.bright.curve.reset.btn", act.ResetBrightnessCurve));
            }

            _live.Tick += () =>
            {
                luxValue.Text = LuxText(act.CurrentLux());
                graph?.Invalidate(); // выученные точки и маркер света подтянутся сами
            };
            _live.Start();
        }

        // честная плашка: с адаптивной яркостью Windows ни лимит, ни авто-яркость не работают
        if ((cfg.BrightnessCapEnabled || cfg.AutoBrightness) && act.IsAdaptiveBrightness())
            ui.AddNote(this, "settings.bright.adaptive");
        var remember = ui.Toggle(cfg.RememberBrightness, act.SetRememberBrightness);
        remember.Enabled = !cfg.AutoBrightness; // кривая заменяет слоты — два хозяина не нужны
        ui.AddRow(this, "settings.profile.brightness", "settings.brightness.desc", remember);

        // ---- Частота — только пока «управление частотой» включено во вкладке «Функции» ----
        if (!cfg.RefreshRateFeature) return;
        ui.AddGroup(this, "settings.hz.group");
        // мастер-тумблер: rebuild гасит/зажигает «удерживать» — без авто-частоты возвращать нечего
        ui.AddRow(this, "settings.hz.auto", "settings.hz.auto.desc",
            ui.Toggle(cfg.AutoRefreshRate, on => { act.SetAutoHz(on); rebuild(); }));
        var hold = ui.Toggle(cfg.HoldRefreshRate, act.SetHoldRefreshRate);
        hold.Enabled = cfg.AutoRefreshRate;
        ui.AddRow(this, "settings.hz.hold", "settings.hz.hold.desc", hold);
        ui.AddGroup(this, "settings.hz.rates");
        ui.AddRow(this, "settings.hz.ac", "settings.hz.ac.desc",
            HzCombo(cfg.AcRefreshRate, hz => act.SetRefreshRates(hz, cfg.BatteryRefreshRate)));
        ui.AddRow(this, "settings.hz.battery", "settings.hz.battery.desc",
            HzCombo(cfg.BatteryRefreshRate, hz => act.SetRefreshRates(cfg.AcRefreshRate, hz)));
        ui.AddNote(this, "settings.hz.note");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _live.Dispose(); // вкладки пересоздаются на каждый показ окна — не течём
        base.Dispose(disposing);
    }

    private static string LuxText(float lux) =>
        float.IsNaN(lux) ? "—" : Loc.T("settings.bright.lux.val", Math.Round(lux));

    /// <summary>
    /// Карточка-график кривой lux → % ТЕКУЩЕГО источника питания (кривых две): ось X — люксы в
    /// лог-шкале, Y — яркость. Линия — предсказание, точки — якоря (в т.ч. выученные), пунктир —
    /// текущая освещённость; лимит яркости «срезает» кривую сверху — график показывает
    /// эффективное поведение. Перерисовывается секундным таймером — обучение видно живьём.
    /// </summary>
    private BufferedPanel CurveGraph(SettingsActions act)
    {
        int w = Ui.RowW, h = Ui.Sc(130);
        var card = new BufferedPanel { Width = w, Height = h, BackColor = Ui.T.Card, Margin = new Padding(0, 0, 0, Ui.Sc(4)) };
        card.Region = new Region(Draw.Rounded(new RectangleF(0, 0, w, h), Ui.Sc(6)));
        card.Paint += (_, e) =>
        {
            PaintCurve(e.Graphics, w, h, act);
            Ui.PaintCardBorder(e.Graphics, w, h);
        };
        card.AccessibleName = Loc.T("settings.bright.auto");
        return card;
    }

    private void PaintCurve(Graphics g, int w, int h, SettingsActions act)
    {
        var pts = act.BrightnessCurvePoints();
        if (pts.Length == 0) return;
        var curve = new BrightnessCurve([.. pts]); // копия-снимок: обучение может идти параллельно

        g.SmoothingMode = SmoothingMode.AntiAlias;
        int padL = Ui.Sc(30), padR = Ui.Sc(14), padT = Ui.Sc(10), padB = Ui.Sc(20);
        var plot = new Rectangle(padL, padT, w - padL - padR, h - padT - padB);
        double maxLog = Math.Log10(1 + 10_000); // шкала до 10к лк — дальше только прямое солнце

        // лимит текущего источника питания (XIC-29) «срезает» кривую сверху прямо на графике:
        // рисуем эффективное поведение, а не намерение — линия и точки клампятся к лимиту
        bool online = PowerLine.IsOnline();
        int cap = _cfg.BrightnessCapEnabled
            ? Math.Clamp(online ? _cfg.BrightnessCapAc : _cfg.BrightnessCapBattery, 10, 100)
            : 100;

        float X(float lux) => plot.Left + (float)(Math.Log10(1 + Math.Max(0, lux)) / maxLog) * plot.Width;
        float Y(int pct) => plot.Bottom - Math.Min(pct, cap) / 100f * plot.Height;

        // сетка: декады люксов и 0/50/100% яркости
        using var grid = new Pen(Ui.T.Border);
        using var dim = new SolidBrush(Ui.T.Text2);
        foreach (float d in (float[])[1, 10, 100, 1000, 10_000])
        {
            float x = X(d);
            g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
            string label = d >= 1000 ? $"{d / 1000:0}k" : $"{d:0}";
            g.DrawString(label, Ui.DescFont, dim, x - Ui.Sc(7), plot.Bottom + Ui.Sc(3));
        }
        foreach (int p in (int[])[0, 50, 100])
        {
            float y = Y(p);
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            g.DrawString($"{p}", Ui.DescFont, dim, Ui.Sc(4), y - Ui.Sc(7));
        }

        // линия предсказания — семплируем кривую по лог-оси
        const int Samples = 64;
        var line = new PointF[Samples + 1];
        for (int i = 0; i <= Samples; i++)
        {
            float lux = (float)(Math.Pow(10, maxLog * i / Samples) - 1);
            line[i] = new PointF(X(lux), Y(curve.Predict(lux)));
        }
        using var pen = new Pen(Ui.T.Accent, Ui.Sc(2));
        g.DrawLines(pen, line);

        // якорные точки (дефолтные и выученные — все они теперь «кривая пользователя»)
        using var dot = new SolidBrush(Ui.T.Accent);
        foreach (var p in pts)
            g.FillEllipse(dot, X(p.Lux) - Ui.Sc(3), Y(p.Percent) - Ui.Sc(3), Ui.Sc(6), Ui.Sc(6));

        // маркер текущей освещённости: пунктир + точка на кривой
        float now = act.CurrentLux();
        if (float.IsNaN(now)) return;
        using var cur = new Pen(Ui.T.Text2) { DashStyle = DashStyle.Dash };
        float cx = X(now);
        g.DrawLine(cur, cx, plot.Top, cx, plot.Bottom);
        using var mark = new SolidBrush(Ui.T.Text);
        g.FillEllipse(mark, cx - Ui.Sc(3), Y(curve.Predict(now)) - Ui.Sc(3), Ui.Sc(6), Ui.Sc(6));
    }

    // FlowLayoutPanel перерисовывает карточку раз в секунду — без буфера она бы мигала
    private sealed class BufferedPanel : Panel
    {
        public BufferedPanel() => DoubleBuffered = true;
    }

    // Комбо частоты: пресеты + текущее значение из config.json, если оно нестандартное
    // (вручную вписанные 165 Гц не должны отображаться как «144»)
    private ComboBox HzCombo(int current, Action<int> apply)
    {
        int[] presets = [144, 120, 90, 60, 48];
        int[] rates = presets.Contains(current) ? presets : [current, .. presets];
        return Ui.Combo([.. rates.Select(r => $"{r} " + Loc.T("settings.hz.unit"))],
            Array.IndexOf(rates, current), i => apply(rates[i]), Ui.Sc(110));
    }

    // Комбо лимита яркости: та же механика — рукописное значение из config.json не подменяем
    // пресетом. 100% = «здесь не ограничивать»: типовой сценарий — от сети максимум,
    // от батареи лимит (просьба пользователей).
    private ComboBox PercentCombo(int current, Action<int> apply)
    {
        int[] presets = [100, 90, 80, 70, 60, 50, 40, 30];
        int[] caps = presets.Contains(current) ? presets : [current, .. presets];
        return Ui.Combo([.. caps.Select(c => $"{c}%")],
            Array.IndexOf(caps, current), i => apply(caps[i]), Ui.Sc(110));
    }
}
