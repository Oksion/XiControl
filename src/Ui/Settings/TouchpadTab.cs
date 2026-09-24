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

        var speed = SwipeCombo(cfg.TouchpadEdgeSwipesPerRange, act.SetTouchpadEdgeSwipes);
        speed.Enabled = cfg.TouchpadEdgeSliders;
        ui.AddRow(this, "settings.touchpad.edges.speed", "settings.touchpad.edges.speed.desc", speed);

        var swap = ui.Toggle(cfg.TouchpadEdgeSwap, act.SetTouchpadEdgeSwap);
        swap.Enabled = cfg.TouchpadEdgeSliders;
        ui.AddRow(this, "settings.touchpad.edges.swap", "settings.touchpad.edges.swap.desc", swap);

        // Щелчок на шаге (XIC-73) — только там, где тачпад ответил по вендорскому каналу:
        // на другой модели тумблер ничего бы не делал, а молча неработающая опция хуже никакой
        var haptics = act.GetTouchpadHaptics();
        if (haptics is not null)
        {
            // rebuild: тумблер гасит/зажигает силу и частоту щелчка
            var click = ui.Toggle(cfg.TouchpadEdgeHaptics, on => { act.SetTouchpadEdgeHaptics(on); rebuild(); });
            click.Enabled = cfg.TouchpadEdgeSliders;
            ui.AddRow(this, "settings.touchpad.edges.haptics", "settings.touchpad.edges.haptics.desc", click);

            bool clicking = cfg.TouchpadEdgeSliders && cfg.TouchpadEdgeHaptics;
            var strength = StrengthCombo(haptics.Slide, act.SetTouchpadSlideStrength);
            strength.Enabled = clicking;
            ui.AddRow(this, "settings.touchpad.edges.haptics.strength", "settings.touchpad.edges.haptics.strength.desc", strength);

            var step = StepCombo(cfg.TouchpadEdgeHapticsMs, act.SetTouchpadEdgeHapticsMs);
            step.Enabled = clicking;
            ui.AddRow(this, "settings.touchpad.edges.haptics.step", "settings.touchpad.edges.haptics.step.desc", step);
        }

        ui.AddNote(this, "settings.touchpad.edges.note");

        // Тактильный отклик (XIC-77): живёт в самом тачпаде, поэтому показываем прочитанное из
        // него, а не из конфига. Не ответил на старте (другая модель) — раздела нет вовсе.
        if (haptics is not null)
        {
            ui.AddGroup(this, "settings.touchpad.haptics");
            ui.AddRow(this, "settings.touchpad.vibration", "settings.touchpad.vibration.desc",
                VibrationCombo(haptics.Vibration, act.SetTouchpadVibration));
            ui.AddRow(this, "settings.touchpad.pressure", "settings.touchpad.pressure.desc",
                PressureCombo(haptics.Pressure, act.SetTouchpadPressure));
            ui.AddNote(this, "settings.touchpad.haptics.note");
        }
    }

    /// <summary>Сила щелчка краевых ползунков: вниз от заводской, 128 на каждом шаге — перебор.
    /// Чужое значение из тачпада добавляется как есть — по образцу MmCombo.</summary>
    private ComboBox StrengthCombo(int current, Action<int> apply)
    {
        int[] presets = TouchpadHapticsProtocol.SlidePresets;
        int[] values = presets.Contains(current) ? presets : [.. presets.Append(current).Order()];
        string[] names = [.. values.Select(v => presets.Contains(v)
            ? Loc.T($"settings.touchpad.edges.haptics.strength.{v}") : Loc.T("settings.touchpad.pressure.custom", v))];
        return Ui.Combo(names, Array.IndexOf(values, current), i => apply(values[i]), Ui.Sc(170));
    }

    /// <summary>Частота щелчков: не чаще раза за столько мс. 50 — каждая порция шагов (чаще
    /// ползунок не применяет), 250 — как у PC Manager. Значение из config.json вне списка
    /// показывается как есть.</summary>
    private ComboBox StepCombo(int current, Action<int> apply)
    {
        int[] presets = [50, 100, 250];
        int[] values = presets.Contains(current) ? presets : [.. presets.Append(current).Order()];
        string[] names = [.. values.Select(v => presets.Contains(v)
            ? Loc.T($"settings.touchpad.edges.haptics.step.{v}") : Loc.T("settings.touchpad.edges.haptics.step.custom", v))];
        return Ui.Combo(names, Array.IndexOf(values, current), i => apply(values[i]), Ui.Sc(170));
    }

    /// <summary>Сила вибрации — три ступени PC Manager. Пара вне ступеней (выставлена чужим
    /// софтом) показывается пустым выбором: выдумывать ей название честнее не надо.</summary>
    private ComboBox VibrationCombo(HapticsVibration? current, Action<HapticsVibration> apply)
    {
        var levels = Enum.GetValues<HapticsVibration>();
        string[] names = [.. levels.Select(l => Loc.T($"settings.touchpad.vibration.{l.ToString().ToLowerInvariant()}"))];
        return Ui.Combo(names, current is { } c ? Array.IndexOf(levels, c) : -1, i => apply(levels[i]), Ui.Sc(170));
    }

    /// <summary>Порог нажатия: лёгкий / заводской / тугой; нестандартное значение из тачпада
    /// (например, 120 от PC Manager) добавляется в список как есть — по образцу MmCombo.</summary>
    private ComboBox PressureCombo(int current, Action<int> apply)
    {
        int[] presets = TouchpadHapticsProtocol.PressurePresets;
        int[] values = presets.Contains(current) ? presets : [.. presets.Append(current).Order()];
        string[] names = [.. values.Select(v => presets.Contains(v)
            ? Loc.T($"settings.touchpad.pressure.{v}") : Loc.T("settings.touchpad.pressure.custom", v))];
        return Ui.Combo(names, Array.IndexOf(values, current), i => apply(values[i]), Ui.Sc(170));
    }

    /// <summary>Чувствительность: сколько проходов вдоль края покрывают шкалу целиком.
    /// Величина выбрана потому, что её человек чувствует пальцем, а не потому, что её удобно
    /// хранить: «шаг в процентах высоты» ни о чём не говорит, пока не поводишь.</summary>
    private ComboBox SwipeCombo(int current, Action<int> apply)
    {
        int[] presets = EdgeSlideScale.Presets;
        int swipes = presets.Contains(current) ? current : 2;
        string[] names = [.. presets.Select(p => Loc.T($"settings.touchpad.edges.speed.{p}"))];
        return Ui.Combo(names, Array.IndexOf(presets, swipes), i => apply(presets[i]), Ui.Sc(140));
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
