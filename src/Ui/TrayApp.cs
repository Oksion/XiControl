using Microsoft.Win32;
using XiControl.Config;
using XiControl.Localization;
using XiControl.SystemIntegration;
using XiControl.Wmi;

namespace XiControl.Ui;

/// <summary>Иконка в трее + всплывающее меню: заряд, режимы, язык, выход.</summary>
public sealed class TrayApp : IDisposable
{
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly MifsClient _mifs;
    private readonly AppConfig _cfg;
    private bool _dark = Theme.IsDark();
    private bool _lightTaskbar = Theme.TaskbarIsLight();
    private readonly ChargeGuard _guard;
    private readonly RefreshRateGuard _hzGuard;
    private readonly PowerProfileGuard _powerGuard;
    private readonly OsdForm _osd = new();
    private readonly MifsEventWatcher _events = new();
    private readonly QuickPanelForm _panel;
    private bool _autoStart;   // кэш состояния автозапуска (не дёргаем schtasks на каждое меню)
    private bool _lastOnline;  // прошлое состояние питания (для OSD только на реальном переходе)

    // редкий опрос: режим извне меняется только сном/EC, свои изменения обновляют значок сразу
    private readonly System.Windows.Forms.Timer _iconTimer = new() { Interval = 30000 };
    private PerfMode? _iconMode;
    private bool _iconInit;

    private readonly System.Windows.Forms.Timer _miHold = new() { Interval = 400 };  // порог «долгого» нажатия Mi
    private readonly System.Windows.Forms.Timer _miClick = new() { Interval = 300 }; // окно ожидания двойного клика
    private bool _miHandled;
    private int _miClicks;

    private static readonly (string key, PerfMode mode)[] Modes =
    [
        ("mode.eco",   PerfMode.Eco),
        ("mode.quiet", PerfMode.Quiet),
        ("mode.auto",  PerfMode.Auto),
        ("mode.turbo", PerfMode.Turbo),
        ("mode.full",  PerfMode.FullSpeed),
    ];

    // видимые режимы (Эко/Полная мощность скрываются в Настройках или config.json)
    private (string key, PerfMode mode)[] _modes = [];
    private PerfMode[] _cycle = []; // порядок цикла Mi-кнопки — по нарастанию мощности

    public TrayApp(MifsClient mifs, AppConfig cfg)
    {
        _mifs = mifs;
        _cfg = cfg;
        ApplyModeVisibility();

        // пока показываем состояние из конфига; реальное уточняем в фоне —
        // schtasks /query может блокировать до 10 с, старту это ни к чему
        _autoStart = cfg.AutoStart;
        Task.Run(() => _autoStart = Safe(AutoStart.IsEnabled, cfg.AutoStart));

        _menu = new ContextMenuStrip { Font = new Font("Segoe UI", 9F) };
        _menu.Opening += (_, _) => BuildMenu();
        ApplyMenuTheme();

        _tray = new NotifyIcon
        {
            Icon = TrayIcons.ForMode(null, _lightTaskbar),
            Visible = true,
            Text = Loc.T("app.name"),
            ContextMenuStrip = _menu,
        };
        // Левый клик тоже открывает меню
        _tray.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) ShowMenu(); };

        // Страж заряда: применяет желаемое состояние на старте и после сна/смены питания
        _guard = new ChargeGuard(_mifs, () => _cfg.ChargeCare);
        _guard.Reapply();

        // Авто-герцовка: частота экрана по питанию (сеть/батарея) на старте и при его смене
        _hzGuard = new RefreshRateGuard(_cfg);
        _hzGuard.Reapply();

        // Профили питания: режим + яркость по питанию, на старте и при его смене
        _powerGuard = new PowerProfileGuard(_mifs, _cfg);
        _powerGuard.ModeApplied = () =>
        {
            if (_osd.IsHandleCreated) _osd.BeginInvoke(new Action(() => UpdateTrayIcon()));
        };

        // Режим при старте (прошивка сбрасывает его на ребуте):
        //  • PowerProfiles → применить профиль текущего питания (режим + яркость);
        //  • иначе RestoreMode → восстановить последний выбранный (если он ещё видим), иначе Auto;
        //  • иначе, если задан ForceStartMode (только правкой конфига) → принудительно его.
        if (_cfg.PowerProfiles)
        {
            _powerGuard.Reapply();
        }
        else if (_cfg.RestoreMode)
        {
            if (_cfg.StartPerfMode is PerfMode saved)
                ApplyStartMode(_modes.Any(m => m.mode == saved) ? saved : PerfMode.Auto);
        }
        else if (_cfg.ForceStartMode is PerfMode forced)
        {
            ApplyStartMode(forced);
        }

        // «Режим совы»: восстановить после сбоя, включить заново, либо погасить, если фичу отключили
        if (_cfg.Awake && !_cfg.OwlMode) { AwakeMode.Disable(_cfg); _cfg.Awake = false; _cfg.Save(); }
        else if (_cfg.Awake) { AwakeMode.Enable(_cfg); _cfg.Save(); }
        else if (_cfg.AwakeSavedLidAc is not null) { AwakeMode.Disable(_cfg); _cfg.Save(); }

        // OSD на смену питания
        _ = _osd.Handle; // форсируем создание хэндла для маршалинга событий в UI-поток
        _lastOnline = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
        SystemEvents.PowerModeChanged += OnPower;

        // Панель по Mi-кнопке + слушатель клавиш прошивки
        _panel = new QuickPanelForm(_mifs, _cfg);
        _panel.Changed = () => UpdateTrayIcon();
        _panel.MonitorRequested = ShowMonitor;
        _miHold.Tick += (_, _) => OnMiHold();
        _miClick.Tick += (_, _) => OnMiClickTimeout();
        _events.KeyPressed += OnKey;
        _events.Start();

        // Значок трея по режиму + реакция на смену темы Windows
        SystemEvents.UserPreferenceChanged += OnUserPref;
        UpdateTrayIcon();

        // Лёгкий опрос: держим значок в соответствии с реальным режимом (любой источник)
        _iconTimer.Tick += (_, _) => UpdateTrayIcon();
        _iconTimer.Start();
    }

    private void UpdateTrayIcon(bool force = false)
    {
        var mode = Safe<PerfMode?>(() => _mifs.GetPerfMode(), null);
        if (!force && _iconInit && mode == _iconMode) return; // без изменений — не трогаем
        _iconInit = true;
        _iconMode = mode;
        try { _tray.Icon = TrayIcons.ForMode(mode, _lightTaskbar); } catch { }
    }

    private void ApplyModeVisibility()
    {
        _modes = Modes.Where(m =>
            (_cfg.EcoMode || m.mode != PerfMode.Eco) &&
            (_cfg.FullSpeedMode || m.mode != PerfMode.FullSpeed)).ToArray();
        _cycle = _modes.Select(m => m.mode).ToArray();
    }

    private void ApplyMenuTheme()
    {
        if (_dark)
        {
            ToolStripManager.Renderer = new DarkMenuRenderer();
            _menu.RenderMode = ToolStripRenderMode.ManagerRenderMode;
            _menu.BackColor = DarkPalette.Bg;
            _menu.ForeColor = DarkPalette.Text;
        }
        else
        {
            _menu.RenderMode = ToolStripRenderMode.System;
            _menu.ResetBackColor();
            _menu.ResetForeColor();
        }
    }

    private void OnUserPref(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        _lightTaskbar = Theme.TaskbarIsLight();
        bool dark = Theme.IsDark();
        if (dark != _dark) { _dark = dark; ApplyMenuTheme(); }
        UpdateTrayIcon(force: true); // тема сменилась — перерисовать даже если режим тот же
    }

    private void OnKey(byte code, byte value)
    {
        if (!_panel.IsHandleCreated) return;
        _panel.BeginInvoke(() => HandleKey(code, value)); // все события — в UI-поток
    }

    private void HandleKey(byte code, byte value)
    {
        switch (code)
        {
            case Mifs.KeyMiDown: OnMiDown(); break;
            case Mifs.KeyMiUp: OnMiUp(); break;
            case Mifs.KeyProjection when value == 0: KeyActions.Projection(); break; // value 2 = слабый зарядник — пока пропуск
            case Mifs.KeySettings: OnSettingsKey(); break; // одиночное событие, удержание не ловится
            case Mifs.KeyAiDown: OnAiKey(); break;                                   // 0x24 (отпускание) игнорируем
            case Mifs.KeyMic: OnMicKey(value); break;
            case Mifs.KeyKbdBacklight: OnBacklightKey(value); break;
            case Mifs.KeyFnLock: OnFnLockKey(value); break;
        }
    }

    // Клавиша «Настройки»: по умолчанию заряд 80↔100; "SettingsKey": "settings" → Параметры Windows.
    // При открытой панели — всегда заряд (переключается пилюля в ней), независимо от ремапа.
    private void OnSettingsKey()
    {
        if (!_panel.Visible && string.Equals(_cfg.SettingsKey, "settings", StringComparison.OrdinalIgnoreCase))
            KeyActions.OpenSettings();
        else
            ToggleCharge();
    }

    // Переключить лимит заряда на противоположный (OSD/панель — внутри ToggleCare)
    private void ToggleCharge()
        => ToggleCare(!Safe(() => _mifs.GetChargeCare(), _cfg.ChargeCare));

    // AI-клавиша: своя программа из config.json (AiKeyProgram/AiKeyArgs), иначе Copilot
    private void OnAiKey()
    {
        if (!string.IsNullOrWhiteSpace(_cfg.AiKeyProgram))
            KeyActions.Launch(_cfg.AiKeyProgram, _cfg.AiKeyArgs);
        else
            KeyActions.Copilot();
    }

    private void OnMicKey(byte value)
    {
        bool mute = value == 0; // лампа горит (0) = замьютить системный микрофон
        using (var mic = new MicControl())
            if (mic.Available) mic.SetMute(mute);
        _osd.Flash(mute ? OsdKind.MicOff : OsdKind.MicOn, Loc.T(mute ? "osd.mic.off" : "osd.mic.on"));
    }

    // Fn-Lock переключает сама прошивка, событие сообщает новое состояние — показываем OSD.
    // value=1 (замок закрыт) = классические F1–F12, мультимедиа отключены (проверено на TM2424).
    private void OnFnLockKey(byte value)
    {
        bool on = value != 0;
        _osd.Flash(on ? OsdKind.FnLockOn : OsdKind.FnLockOff,
                   Loc.T("osd.fnlock"), Loc.T(on ? "osd.fnlock.on" : "osd.fnlock.off"));
    }

    private void OnBacklightKey(byte value)
    {
        string sub = value switch
        {
            0x00 => Loc.T("osd.off"),
            0x80 => Loc.T("osd.auto"),
            _ => Loc.T("osd.backlight.level", value * 10), // уровни 0–10 → проценты (5→50%, 10→100%)
        };
        var kind = value switch
        {
            0x00 => OsdKind.BacklightOff,
            0x80 => OsdKind.BacklightAuto,
            <= 5 => OsdKind.BacklightMid,
            _ => OsdKind.Backlight,
        };
        _osd.Flash(kind, Loc.T("osd.backlight"), sub);
    }

    private void OnMiDown()
    {
        _miHandled = false;
        _miHold.Stop();
        _miHold.Start();
    }

    // Жесты Mi: одинарный клик (после окна двойного) / двойной клик / удержание.
    // По умолчанию: одинарный — цикл режимов, двойной — заряд 80/100;
    // "MiShortPress": "charge" инвертирует одинарный и двойной.
    private bool MiChargeFirst => string.Equals(_cfg.MiShortPress, "charge", StringComparison.OrdinalIgnoreCase);

    private void OnMiUp()
    {
        _miHold.Stop();
        if (_miHandled) { _miClicks = 0; return; }

        // двойной клик отключён — одинарный без задержки
        if (!_cfg.MiDoubleClick)
        {
            if (MiChargeFirst) ToggleCharge(); else CycleMode();
            return;
        }

        _miClicks++;
        _miClick.Stop();
        if (_miClicks >= 2)
        {
            _miClicks = 0;
            if (MiChargeFirst) CycleMode(); else ToggleCharge(); // двойной клик
        }
        else
        {
            _miClick.Start(); // ждём: не начало ли это двойного
        }
    }

    private void OnMiClickTimeout()
    {
        _miClick.Stop();
        if (_miClicks == 1)
        {
            if (MiChargeFirst) ToggleCharge(); else CycleMode(); // одинарный клик
        }
        _miClicks = 0;
    }

    private void OnMiHold()
    {
        _miHold.Stop();
        if (_miHandled) return;
        _miHandled = true;
        _miClick.Stop();
        _miClicks = 0;
        _panel.Toggle();       // удержание → панель
    }

    private void OnPower(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is not (PowerModes.StatusChange or PowerModes.Resume)) return;
        bool online = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
        if (online == _lastOnline) return;   // только реальный переход AC↔батарея
        _lastOnline = online;
        if (_osd.IsHandleCreated) _osd.BeginInvoke(() => ShowPowerOsd(online));
        else ShowPowerOsd(online);
    }

    private void ShowPowerOsd(bool online)
    {
        var ps = SystemInformation.PowerStatus;
        float f = ps.BatteryLifePercent;
        string? sub = (f >= 0f && f <= 1f) ? Loc.T("osd.level", (int)Math.Round(f * 100)) : null;

        // авто-герцовка включена — дописываем фактическую частоту (ближайшую поддерживаемую;
        // сам переход сделает RefreshRateGuard после дебаунса)
        if (_cfg.AutoRefreshRate &&
            RefreshRate.Resolve(online ? _cfg.AcRefreshRate : _cfg.BatteryRefreshRate) is int real)
        {
            string hz = Loc.T("osd.hz", real);
            sub = sub is null ? hz : $"{sub} • {hz}";
        }

        if (online)
        {
            if (_cfg.ChargeCare)
                _osd.Flash(OsdKind.ChargingLimited, Loc.T("osd.charging.limited", Mifs.ChargeThresholdPercent), sub);
            else
                _osd.Flash(OsdKind.Charging, Loc.T("osd.charging"), sub);
        }
        else
        {
            _osd.Flash(OsdKind.OnBattery, Loc.T("osd.onbattery"), sub);
        }
    }

    private void ShowMenu()
    {
        // приватный метод показа меню трея (правильная позиция + авто-закрытие);
        // BuildMenu вызовется сам через событие Opening
        try
        {
            var mi = _tray.GetType().GetMethod("ShowContextMenu",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (mi != null) { mi.Invoke(_tray, null); return; }
        }
        catch (Exception ex) { Log.Ex(nameof(ShowMenu), ex); }
        _menu.Show(Cursor.Position); // запасной путь, если приватный API исчезнет из WinForms
    }

    private void BuildMenu()
    {
        // Clear не диспозит старые пункты — освобождаем сами
        var stale = _menu.Items.Cast<ToolStripItem>().ToArray();
        _menu.Items.Clear();
        foreach (var it in stale) it.Dispose();

        // размер иконок меню под текущий DPI (сейчас иконка только у пункта «Язык»)
        int imgSz = (int)Math.Round(16 * _menu.DeviceDpi / 96.0);
        _menu.ImageScalingSize = new Size(imgSz, imgSz);

        // --- Заряд ---
        bool care = Safe(() => _mifs.GetChargeCare(), _cfg.ChargeCare);
        var charge = new ToolStripMenuItem(Loc.T("menu.charge")) { Checked = care };
        // состояние читаем в момент клика: пока меню висело, его мог сменить ChargeGuard
        charge.Click += (_, _) => ToggleCare(!Safe(() => _mifs.GetChargeCare(), _cfg.ChargeCare));
        _menu.Items.Add(charge);

        // --- Режим совы (не спать) — если фича не скрыта конфигом ---
        if (_cfg.OwlMode)
        {
            var owl = new ToolStripMenuItem(Loc.T("menu.owl")) { Checked = _cfg.Awake };
            owl.Click += (_, _) => ToggleAwake();
            _menu.Items.Add(owl);
        }

        // --- Авто-герцовка (частота экрана по питанию) ---
        var hz = new ToolStripMenuItem(Loc.T("menu.hz", _cfg.AcRefreshRate, _cfg.BatteryRefreshRate))
        { Checked = _cfg.AutoRefreshRate };
        hz.Click += (_, _) => ToggleAutoHz(!_cfg.AutoRefreshRate);
        _menu.Items.Add(hz);

        // --- Монитор (Вт / CPU / RAM) ---
        var monitor = new ToolStripMenuItem(Loc.T("menu.monitor")) { Checked = _monitor?.Visible == true };
        monitor.Click += (_, _) => ShowMonitor();
        _menu.Items.Add(monitor);

        // --- Режим (подменю) ---
        PerfMode? current = Safe<PerfMode?>(() => _mifs.GetPerfMode(), null);
        string currentName = current is PerfMode cm && ModeKey(cm) is string mk ? Loc.T(mk) : "—";
        var perf = new ToolStripMenuItem($"{Loc.T("menu.perf")}:  {currentName}");
        foreach (var (key, mode) in _modes)
        {
            var item = new ToolStripMenuItem(Loc.T(key)) { Checked = current == mode };
            item.Click += (_, _) => SetMode(mode, key);
            perf.DropDownItems.Add(item);
        }
        TintDropDown(perf);
        _menu.Items.Add(perf);

        _menu.Items.Add(new ToolStripSeparator());

        // --- Настройки (подменю: язык, автозапуск) ---
        var settings = new ToolStripMenuItem(Loc.T("menu.settings"));

        var lang = new ToolStripMenuItem(Loc.T("menu.language"))
        {
            // единственная иконка в меню — визуальный якорь: найти переключение языка,
            // не читая подписей (напр. если случайно выбран незнакомый язык)
            Image = SvgIcons.Render(SvgIcons.TrayLanguage, imgSz, _dark ? DarkPalette.Text : SystemColors.MenuText),
        };
        lang.DropDownItems.Add(LangItem("Русский", Lang.Ru));
        lang.DropDownItems.Add(LangItem("English", Lang.En));
        lang.DropDownItems.Add(LangItem("中文", Lang.Zh));
        TintDropDown(lang);
        settings.DropDownItems.Add(lang);

        var autostart = new ToolStripMenuItem(Loc.T("menu.autostart")) { Checked = _autoStart };
        autostart.Click += (_, _) => ToggleAutoStart(!_autoStart);
        settings.DropDownItems.Add(autostart);

        settings.DropDownItems.Add(new ToolStripSeparator());

        // видимость опциональных режимов — применяется сразу, без перезапуска
        var showEco = new ToolStripMenuItem(Loc.T("menu.show.eco")) { Checked = _cfg.EcoMode };
        showEco.Click += (_, _) => ToggleModeVisibility(eco: !_cfg.EcoMode, full: _cfg.FullSpeedMode);
        settings.DropDownItems.Add(showEco);

        var showFull = new ToolStripMenuItem(Loc.T("menu.show.full")) { Checked = _cfg.FullSpeedMode };
        showFull.Click += (_, _) => ToggleModeVisibility(eco: _cfg.EcoMode, full: !_cfg.FullSpeedMode);
        settings.DropDownItems.Add(showFull);

        // режим при старте (подменю): «восстанавливать последний» ИЛИ «закрепить текущий» —
        // взаимоисключающие переключатели (см. SetStartRestore / PinCurrentStartMode)
        var startMode = new ToolStripMenuItem(Loc.T("menu.startmode"));
        var restoreLast = new ToolStripMenuItem(Loc.T("menu.startmode.restore")) { Checked = _cfg.RestoreMode };
        restoreLast.Click += (_, _) => SetStartRestore(!_cfg.RestoreMode);
        startMode.DropDownItems.Add(restoreLast);
        var pinCurrent = new ToolStripMenuItem(Loc.T("menu.startmode.pin")) { Checked = _cfg.ForceStartMode is not null };
        pinCurrent.Click += (_, _) => PinCurrentStartMode();
        startMode.DropDownItems.Add(pinCurrent);
        var powerProf = new ToolStripMenuItem(Loc.T("menu.startmode.power")) { Checked = _cfg.PowerProfiles };
        powerProf.Click += (_, _) => SetPowerProfiles(!_cfg.PowerProfiles);
        startMode.DropDownItems.Add(powerProf);

        // профили активны — показываем выбор режима на зарядке / от батареи и опцию яркости
        if (_cfg.PowerProfiles)
        {
            startMode.DropDownItems.Add(new ToolStripSeparator());
            startMode.DropDownItems.Add(ProfilePicker("menu.profile.ac", ac: true, _cfg.AcPerfMode));
            startMode.DropDownItems.Add(ProfilePicker("menu.profile.battery", ac: false, _cfg.BatteryPerfMode));
            var remBright = new ToolStripMenuItem(Loc.T("menu.profile.brightness")) { Checked = _cfg.RememberBrightness };
            remBright.Click += (_, _) => ToggleRememberBrightness();
            startMode.DropDownItems.Add(remBright);
        }
        TintDropDown(startMode);
        settings.DropDownItems.Add(startMode);

        settings.DropDownItems.Add(new ToolStripSeparator());

        // раскладка Mi-кнопки: галочка = клик переключает режимы (двойной — заряд), снята = наоборот
        var miPerf = new ToolStripMenuItem(Loc.T("menu.mi.perf")) { Checked = !MiChargeFirst };
        miPerf.Click += (_, _) => { _cfg.MiShortPress = MiChargeFirst ? "modes" : "charge"; _cfg.Save(); };
        settings.DropDownItems.Add(miPerf);

        // двойной клик Mi: снята — жест отключён, зато одинарный мгновенный
        var miDouble = new ToolStripMenuItem(Loc.T("menu.mi.double")) { Checked = _cfg.MiDoubleClick };
        miDouble.Click += (_, _) => { _cfg.MiDoubleClick = !_cfg.MiDoubleClick; _cfg.Save(); };
        settings.DropDownItems.Add(miDouble);

        // клавиша «настройки»: галочка — заряд 80/100, снята — Параметры Windows
        bool keyCharge = !string.Equals(_cfg.SettingsKey, "settings", StringComparison.OrdinalIgnoreCase);
        var keyMode = new ToolStripMenuItem(Loc.T("menu.key.charge")) { Checked = keyCharge };
        keyMode.Click += (_, _) => { _cfg.SettingsKey = keyCharge ? "settings" : "charge"; _cfg.Save(); };
        settings.DropDownItems.Add(keyMode);

        // видимость «режима совы» как фичи (сова в панели + пункт меню)
        var owlEnable = new ToolStripMenuItem(Loc.T("menu.owl.enable")) { Checked = _cfg.OwlMode };
        owlEnable.Click += (_, _) => ToggleOwlFeature(!_cfg.OwlMode);
        settings.DropDownItems.Add(owlEnable);

        TintDropDown(settings);
        _menu.Items.Add(settings);

        _menu.Items.Add(new ToolStripSeparator());

        // --- Выход ---
        var exit = new ToolStripMenuItem(Loc.T("menu.exit"));
        exit.Click += (_, _) => { _tray.Visible = false; Application.Exit(); };
        _menu.Items.Add(exit);
    }

    private void TintDropDown(ToolStripMenuItem parent)
    {
        if (!_dark) return;
        parent.DropDown.BackColor = DarkPalette.Bg;
        parent.DropDown.ForeColor = DarkPalette.Text;
    }

    private static string? ModeKey(PerfMode m) => m switch
    {
        PerfMode.Eco => "mode.eco",
        PerfMode.Quiet => "mode.quiet",
        PerfMode.Auto => "mode.auto",
        PerfMode.Turbo => "mode.turbo",
        PerfMode.FullSpeed => "mode.full",
        _ => null,
    };

    private ToolStripMenuItem LangItem(string title, Lang lang)
    {
        var item = new ToolStripMenuItem(title) { Checked = _cfg.Language == lang };
        item.Click += (_, _) =>
        {
            _cfg.Language = lang;
            Loc.Current = lang;
            _cfg.Save();
            _tray.Text = Loc.T("app.name");
        };
        return item;
    }

    private void ToggleCare(bool on)
    {
        Safe(() => { _mifs.SetChargeCare(on); return true; }, false);
        _cfg.ChargeCare = on;
        _cfg.Save();
        if (_panel.Visible)
            _panel.RefreshUi(); // панель открыта: пилюля 80/100 переключается в ней, OSD не нужен
        else
            _osd.Flash(on ? OsdKind.CareOn : OsdKind.CareOff,
                       on ? Loc.T("osd.care.on") : Loc.T("osd.care.off"));
    }

    private void SetMode(PerfMode mode, string key)
    {
        Safe(() => _mifs.SetPerfMode(mode), false);
        _cfg.RememberMode(mode);
        _osd.Flash(ModeKind(mode), Loc.T(key));
        UpdateTrayIcon();
    }

    private static OsdKind ModeKind(PerfMode m) => m switch
    {
        PerfMode.Eco => OsdKind.Eco,
        PerfMode.Quiet => OsdKind.Quiet,
        PerfMode.Auto => OsdKind.Auto,
        PerfMode.Turbo => OsdKind.Turbo,
        PerfMode.FullSpeed => OsdKind.Full,
        _ => OsdKind.Auto,
    };

    /// <summary>Переключить на следующий режим по кругу + OSD (для Fn+Mi / хоткея).</summary>
    private void CycleMode()
    {
        var cur = Safe<PerfMode?>(() => _mifs.GetPerfMode(), null) ?? PerfMode.Auto;
        int idx = Array.IndexOf(_cycle, cur);
        var next = _cycle[(idx < 0 ? 0 : idx + 1) % _cycle.Length];
        Safe(() => _mifs.SetPerfMode(next), false);
        _cfg.RememberMode(next);
        if (_panel.Visible)
            _panel.RefreshUi(); // панель открыта: выбор «перелистывается» в ней, OSD не нужен
        else
            _osd.Flash(ModeKind(next), Loc.T(ModeKey(next) ?? "mode.auto"));
        UpdateTrayIcon();
    }

    private void ToggleModeVisibility(bool eco, bool full)
    {
        _cfg.EcoMode = eco;
        _cfg.FullSpeedMode = full;
        _cfg.Save();
        ApplyModeVisibility();
        _panel.ReloadModes();
    }

    // Применить желаемый стартовый режим; если прошивка не приняла (напр. Full-speed на батарее) — Auto.
    private void ApplyStartMode(PerfMode mode)
    {
        if (!Safe(() => _mifs.SetPerfMode(mode), false))
            Safe(() => _mifs.SetPerfMode(PerfMode.Auto), false);
    }

    // «Восстанавливать последний» (взаимоисключающе с «закрепить»). При первом включении
    // (StartPerfMode ещё пуст) запоминаем текущий режим сразу — чтобы было что восстанавливать;
    // при повторном значение не трогаем, поэтому вернётся всё как было до отключения.
    private void SetStartRestore(bool on)
    {
        _cfg.RestoreMode = on;
        if (on)
        {
            _cfg.ForceStartMode = null;   // включили восстановление — снимаем закреп
            _cfg.PowerProfiles = false;   // …и профили питания (три стратегии взаимоисключающи)
            if (_cfg.StartPerfMode is null)
                _cfg.StartPerfMode = Safe<PerfMode?>(() => _mifs.GetPerfMode(), null);
        }
        _cfg.Save();
    }

    // «Закрепить текущий режим» — переключатель: уже закреплён → снять (обе галки пустые);
    // не закреплён → закрепить текущий (Авто/не прочитался — закреплять нечего). Закрепление
    // взаимоисключающе гасит «восстанавливать последний».
    private void PinCurrentStartMode()
    {
        if (_cfg.ForceStartMode is not null)
        {
            _cfg.ForceStartMode = null; // снять закреп
        }
        else if (Safe<PerfMode?>(() => _mifs.GetPerfMode(), null) is PerfMode m && m != PerfMode.Auto)
        {
            _cfg.ForceStartMode = m;
            _cfg.RestoreMode = false;
            _cfg.PowerProfiles = false; // закрепили режим — профили питания выключаем
        }
        _cfg.Save();
    }

    // «Профили питания» (взаимоисключающе с «восстанавливать»/«закрепить»): включаем —
    // засеваем текущую яркость в слот текущего питания (чтоб было что вспоминать) и применяем.
    private void SetPowerProfiles(bool on)
    {
        _cfg.PowerProfiles = on;
        if (on)
        {
            _cfg.RestoreMode = false;
            _cfg.ForceStartMode = null;
            if (_cfg.RememberBrightness) SeedCurrentBrightness();
        }
        _cfg.Save();
        if (on) _powerGuard.Reapply();
    }

    // Выбор режима профиля (ac=true — сеть, иначе батарея; mode=null — «не менять»).
    // Если это профиль текущего питания — применяем сразу для мгновенной обратной связи.
    private void SetProfileMode(bool ac, PerfMode? mode)
    {
        if (ac) _cfg.AcPerfMode = mode; else _cfg.BatteryPerfMode = mode;
        _cfg.Save();
        bool online = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
        if (mode is PerfMode m && ac == online)
        {
            if (!Safe(() => _mifs.SetPerfMode(m), false))
                Safe(() => _mifs.SetPerfMode(PerfMode.Auto), false);
            UpdateTrayIcon();
        }
    }

    private void ToggleRememberBrightness()
    {
        _cfg.RememberBrightness = !_cfg.RememberBrightness;
        if (_cfg.RememberBrightness) SeedCurrentBrightness();
        _cfg.Save();
    }

    // Запомнить текущую яркость в слот текущего питания (при включении опции — чтобы был старт).
    private void SeedCurrentBrightness()
    {
        if (Brightness.Get() is not int lvl) return;
        if (SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online)
            _cfg.AcBrightness = lvl;
        else
            _cfg.BatteryBrightness = lvl;
    }

    // Подменю выбора режима для одного профиля: «Не менять» + все видимые режимы (radio-галочка).
    private ToolStripMenuItem ProfilePicker(string titleKey, bool ac, PerfMode? current)
    {
        var root = new ToolStripMenuItem(Loc.T(titleKey));
        var none = new ToolStripMenuItem(Loc.T("menu.profile.nochange")) { Checked = current is null };
        none.Click += (_, _) => SetProfileMode(ac, null);
        root.DropDownItems.Add(none);
        root.DropDownItems.Add(new ToolStripSeparator());
        foreach (var (key, mode) in _modes)
        {
            var m = mode;
            var it = new ToolStripMenuItem(Loc.T(key)) { Checked = current == mode };
            it.Click += (_, _) => SetProfileMode(ac, m);
            root.DropDownItems.Add(it);
        }
        TintDropDown(root);
        return root;
    }

    // Авто-герцовка: вкл — сразу применить частоту по текущему питанию, выкл — частоту не трогаем
    private void ToggleAutoHz(bool on)
    {
        _cfg.AutoRefreshRate = on;
        _cfg.Save();
        if (on) _hzGuard.Reapply();
        if (_panel.Visible)
            _panel.RefreshUi(); // панель открыта: ячейка герцовки переключается в ней, OSD не нужен
        else if (on)
            _osd.Flash(OsdKind.RefreshRate, Loc.T("osd.hz.on"),
                       Loc.T("osd.hz.on.sub", _cfg.AcRefreshRate, _cfg.BatteryRefreshRate));
        else
            _osd.Flash(OsdKind.RefreshRateOff, Loc.T("osd.hz.off"));
    }

    // Показ/скрытие «режима совы» как фичи; при скрытии активный режим гасится
    private void ToggleOwlFeature(bool on)
    {
        _cfg.OwlMode = on;
        if (!on && _cfg.Awake) { AwakeMode.Disable(_cfg); _cfg.Awake = false; }
        _cfg.Save();
        _panel.ReloadModes(); // перестроить раскладку панели (сова появляется/уходит)
    }

    private MonitorForm? _monitor;

    private void ShowMonitor()
    {
        _monitor ??= new MonitorForm(_cfg);
        _monitor.Popup();
    }

    // «Режим совы»: включить/выключить «не спать» (панель обновится, если открыта)
    private void ToggleAwake()
    {
        if (_cfg.Awake) { AwakeMode.Disable(_cfg); _cfg.Awake = false; }
        else if (AwakeMode.Enable(_cfg)) { _cfg.Awake = true; }
        _cfg.Save();
        if (_panel.Visible) _panel.RefreshUi();
    }

    private void ToggleAutoStart(bool on)
    {
        Safe(() => { AutoStart.Set(on); return true; }, false);
        _autoStart = Safe(AutoStart.IsEnabled, on);  // перечитать реальное состояние
        _cfg.AutoStart = _autoStart;
        _cfg.Save();
    }

    private static T Safe<T>(Func<T> f, T fallback,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        try { return f(); }
        catch (Exception ex) { Log.Ex($"TrayApp.{caller}", ex); return fallback; }
    }

    public void Dispose()
    {
        // вернуть действие крышки; сам флаг Awake в конфиге не трогаем —
        // при следующем запуске режим включится снова
        if (_cfg.Awake) { AwakeMode.Disable(_cfg); _cfg.Save(); }

        SystemEvents.PowerModeChanged -= OnPower;
        SystemEvents.UserPreferenceChanged -= OnUserPref;
        _iconTimer.Dispose();
        _miHold.Dispose();
        _miClick.Dispose();
        _events.Dispose();
        _guard.Dispose();
        _hzGuard.Dispose();
        _powerGuard.Dispose();
        _osd.Dispose();
        _panel.Dispose();
        _monitor?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        TrayIcons.DisposeAll();
    }
}
