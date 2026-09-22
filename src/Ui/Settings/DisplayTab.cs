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

    /// <summary>Кривую какого источника сейчас правим. Статика по той же причине, что и в
    /// PerfTab: окно пересобирает вкладки на каждое изменение, и выбор «от батареи» иначе
    /// слетал бы после первой же переставленной точки. Первый показ открывает кривую
    /// текущего питания — ту, что сейчас рулит экраном.</summary>
    private static bool? _editingAc;

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
                // обучение кривой (XIC-37): выкл — правки временные, кривая заморожена;
                // rebuild зажигает/гасит комбо возврата ниже
                ui.AddRow(this, "settings.bright.learn", "settings.bright.learn.desc",
                    ui.Toggle(cfg.AutoBrightnessLearning, on => { act.SetAutoBrightnessLearning(on); rebuild(); }));

                // возврат к выученному: всегда / только на батарее / выключен
                string?[] revertValues = [null, "battery", "off"];
                int curRevert = Math.Max(0, Array.IndexOf(revertValues, cfg.AutoBrightnessRevert?.ToLowerInvariant()));
                var revert = ui.Combo(
                    [Loc.T("settings.bright.revert.always"), Loc.T("settings.bright.revert.battery"), Loc.T("settings.bright.revert.off")],
                    curRevert, i => act.SetAutoBrightnessRevert(revertValues[i]), ui.Sc(170));
                revert.Enabled = !cfg.AutoBrightnessLearning; // при включённом обучении возврат не участвует
                ui.AddRow(this, "settings.bright.revert", "settings.bright.revert.desc", revert);

                // «инерция»: медиана люксов за окно — случайные блики не дёргают яркость
                ui.AddRow(this, "settings.bright.median", "settings.bright.median.desc",
                    MedianCombo(cfg.AutoBrightnessMedianSec, act.SetBrightnessMedianSec));

                // Правим кривую одного источника: какого — выбирает сегмент, как во вкладке
                // «Производительность» (XIC-65). Тащить точки у обеих кривых сразу нельзя —
                // они пересекаются, и мышь всё время хватала бы не ту.
                ui.AddGroup(this, "settings.bright.curve.group");
                bool editAc = _editingAc ?? PowerLine.IsOnline();
                var editor = new CurveEditor(ui, cfg, act, editAc);
                // от выбора источника здесь меняется ОДИН контрол — график. Пересобирать ради
                // этого вкладку (как делает PerfTab, где меняется весь список тумблеров) нельзя:
                // пересборка уносит фокус и прокрутку, и следующий клик уходит в никуда.
                Controls.Add(ui.SegmentPicker("settings.bright.curve.ac", "settings.bright.curve.battery",
                    editAc, picked => { _editingAc = picked; editor.SetSource(picked); }));
                graph = editor;
                Controls.Add(graph);
                ui.AddNote(this, "settings.bright.curve.edit");

                // сброс обучения — только явной кнопкой: выключение фичи кривую не трогает.
                // Ширину меряем сами: AutoSize у кнопки срабатывает позже, чем карточка
                // считает раскладку, — кнопка выходила микроскопической
                var reset = ui.LinkButton("settings.bright.curve.reset.btn", act.ResetBrightnessCurve);
                reset.AutoSize = false;
                reset.Width = TextRenderer.MeasureText(Loc.T("settings.bright.curve.reset.btn"), ui.CtlFont).Width + ui.Sc(28);
                reset.Height = ui.Sc(30);
                ui.AddRow(this, "settings.bright.curve.reset", "settings.bright.curve.reset.desc", reset);
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

        // Какие частоты перебирать клавишей (XIC-69). Показываем ровно то, что отдала панель:
        // список пресетов здесь был бы враньём — у каждой модели свой набор.
        //
        // Порог — ТРИ режима, а не два. На панели с 60/120 (наш TM2424) выбирать нечего:
        // выключение одного тумблера оставляет одну частоту, а это вырожденный случай, и
        // перебор возвращается ко всем. Тумблер выглядел бы выключенным, не меняя ничего —
        // настройка, которая делает вид, что работает, хуже отсутствующей.
        int[] supported = SystemIntegration.RefreshRate.Supported();
        if (supported.Length >= 3)
        {
            ui.AddGroup(this, "settings.hz.cycle");
            ui.AddNote(this, "settings.hz.cycle.note");
            var chosen = cfg.CycleRefreshRates;
            foreach (int hz in supported)
            {
                int rate = hz;   // замыкание на переменную цикла
                bool on = chosen is null || chosen.Count == 0 || chosen.Contains(rate);
                string title = Loc.T("settings.hz.cycle.rate", rate);
                var toggle = ui.Toggle(on, v => act.SetCycleRate(rate, v));
                toggle.AccessibleName = title;   // AddRow делает это сам, Row — нет
                Controls.Add(ui.Row(title, Loc.T("settings.hz.cycle.rate.desc"), toggle));
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _live.Dispose(); // вкладки пересоздаются на каждый показ окна — не течём
        base.Dispose(disposing);
    }

    private static string LuxText(float lux) =>
        float.IsNaN(lux) ? "—" : Loc.T("settings.bright.lux.val", Math.Round(lux));

    // Комбо частоты: пресеты + текущее значение из config.json, если оно нестандартное
    // (вручную вписанные 165 Гц не должны отображаться как «144»)
    private ComboBox HzCombo(int current, Action<int> apply)
    {
        int[] presets = [144, 120, 90, 60, 48];
        int[] rates = presets.Contains(current) ? presets : [current, .. presets];
        return Ui.Combo([.. rates.Select(r => $"{r} " + Loc.T("settings.hz.unit"))],
            Array.IndexOf(rates, current), i => apply(rates[i]), Ui.Sc(110));
    }

    // Комбо «инерции» датчика: медианное окно в секундах; 0 — фильтр выключен (мгновенно)
    private ComboBox MedianCombo(int current, Action<int> apply)
    {
        int[] presets = [0, 5, 10, 20, 30, 60];
        int[] secs = presets.Contains(current) ? presets : [current, .. presets];
        return Ui.Combo(
            [.. secs.Select(s => s == 0 ? Loc.T("settings.bright.median.off") : Loc.T("settings.bright.median.val", s))],
            Array.IndexOf(secs, current), i => apply(secs[i]), Ui.Sc(110));
    }

    // Комбо лимита яркости: та же механика — рукописное значение из config.json не подменяем
    // пресетом. 100% = «здесь не ограничивать»: типовой сценарий — от сети максимум,
    // от батареи лимит (просьба пользователей). Шаг 5% — тоже просьба с форума (XIC-36);
    // совсем тонкое (1%) остаётся правкой config.json.
    private ComboBox PercentCombo(int current, Action<int> apply)
    {
        int[] presets = [.. Enumerable.Range(0, 15).Select(i => 100 - i * 5)]; // 100..30 через 5
        int[] caps = presets.Contains(current) ? presets : [current, .. presets];
        return Ui.Combo([.. caps.Select(c => $"{c}%")],
            Array.IndexOf(caps, current), i => apply(caps[i]), Ui.Sc(110));
    }
}
