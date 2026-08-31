using XiControl.Config;
using XiControl.Localization;
using XiControl.SystemIntegration;

namespace XiControl.Ui.Settings;

/// <summary>
/// Вкладка «Тачпад»: поведение панели, а не её видимость в меню (это «Функции»).
/// Пока здесь одна опция — мёртвая зона у нижнего края (штатная curtain-зона Windows).
/// Высота выбирается только при включённой зоне; если пользователь выкрутил чувствительность
/// тачпада в параметрах Windows на максимум, зона не работает — говорим об этом прямо,
/// молча врать нельзя.
/// </summary>
public sealed class TouchpadTab : SettingsPane
{
    public TouchpadTab(SettingsToolkit ui, AppConfig cfg, SettingsActions act, Action rebuild) : base(ui)
    {
        ui.AddHeader(this, "settings.tab.touchpad", "settings.touchpad.sub");

        // мастер-тумблер: rebuild гасит/зажигает выбор высоты
        ui.AddRow(this, "settings.touchpad.deadzone", "settings.touchpad.deadzone.desc",
            ui.Toggle(cfg.TouchpadDeadZone, on => { act.SetTouchpadDeadZone(on); rebuild(); }));

        var height = MmCombo(cfg.TouchpadDeadZoneMm, act.SetTouchpadDeadZoneMm);
        height.Enabled = cfg.TouchpadDeadZone;
        ui.AddRow(this, "settings.touchpad.deadzone.size", "settings.touchpad.deadzone.size.desc", height);

        ui.AddNote(this, "settings.touchpad.deadzone.note");
        // предупреждаем только когда это правда мешает — иначе строка была бы шумом
        if (TouchpadDeadZone.AapDisabled == true) ui.AddNote(this, "settings.touchpad.deadzone.aap");

        // Краевые ползунки (XIC-61). Механика та же, что у зоны снизу: подавление курсора —
        // штатной curtain-зоной, а сам жест читается сырым вводом.
        ui.AddGroup(this, "settings.touchpad.edges");
        ui.AddRow(this, "settings.touchpad.edges", "settings.touchpad.edges.desc",
            ui.Toggle(cfg.TouchpadEdgeSliders, on => { act.SetTouchpadEdgeSliders(on); rebuild(); }));

        var width = EdgeCombo(cfg.TouchpadEdgeWidthMm, act.SetTouchpadEdgeWidthMm);
        width.Enabled = cfg.TouchpadEdgeSliders;
        ui.AddRow(this, "settings.touchpad.edges.size", "settings.touchpad.edges.size.desc", width);

        var swap = ui.Toggle(cfg.TouchpadEdgeSwap, act.SetTouchpadEdgeSwap);
        swap.Enabled = cfg.TouchpadEdgeSliders;
        ui.AddRow(this, "settings.touchpad.edges.swap", "settings.touchpad.edges.swap.desc", swap);

        ui.AddNote(this, "settings.touchpad.edges.note");
    }

    /// <summary>Ширина краевой полосы. Верх списка ограничен намеренно: измерено, что заведомо
    /// большое значение PTP-маппер игнорирует вовсе и зона молча перестаёт работать.</summary>
    private ComboBox EdgeCombo(int current, Action<int> apply)
    {
        int mm = TouchpadEdgeSliders.NormalizeWidthMm(current);
        int[] presets = [8, 10, 12, 15, 20];
        int[] sizes = presets.Contains(mm) ? presets : [.. presets.Append(mm).Order()];
        return Ui.Combo([.. sizes.Select(x => $"{x} " + Loc.T("settings.touchpad.deadzone.unit"))],
            Array.IndexOf(sizes, mm), i => apply(sizes[i]), Ui.Sc(110));
    }

    // Высота зоны: пресеты + текущее значение из config.json, если оно нестандартное
    // (вписанные руками 25 мм не должны отображаться как «20») — как HzCombo на «Экране»
    private ComboBox MmCombo(int current, Action<int> apply)
    {
        int mm = TouchpadDeadZone.NormalizeMm(current);
        int[] presets = TouchpadDeadZone.PresetsMm;
        int[] sizes = presets.Contains(mm) ? presets : [.. presets.Append(mm).Order()];
        return Ui.Combo([.. sizes.Select(s => $"{s} " + Loc.T("settings.touchpad.deadzone.unit"))],
            Array.IndexOf(sizes, mm), i => apply(sizes[i]), Ui.Sc(110));
    }
}
