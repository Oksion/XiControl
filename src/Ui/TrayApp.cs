using System.Diagnostics;
using Microsoft.Win32;
using XiControl.Config;
using XiControl.Input;
using XiControl.Localization;
using XiControl.SystemIntegration;
using XiControl.Wmi;

namespace XiControl.Ui;

/// <summary>Иконка в трее + всплывающее меню: заряд, режимы, язык, выход.</summary>
public sealed class TrayApp : IDisposable
{
    private readonly NotifyIcon _tray;
    private readonly TrayMenuBuilder _menu;
    private readonly IMifsClient _mifs;
    private readonly AppConfig _cfg;
    private readonly OsdForm _osd = new();
    private readonly IKeyEventSource _events;
    private readonly IPowerEvents _power;
    private readonly QuickPanelForm _panel;
    private bool _lastOnline;  // прошлое состояние питания (для OSD только на реальном переходе)
    private bool _locked;      // сессия заблокирована (Win+L): OSD не виден — обратная связь звуком/тостом (XIC-11)
    private string? _pendingUpdateUrl; // куда вести по клику на тост об обновлении

    // Кэш отчёта о батарее (BatteryTab + /status API): износ/циклы/ёмкость меняются раз в
    // сутки-недели, а окно настроек пересобирается на каждый Popup/смену языка — держим короткий
    // TTL, чтобы не дёргать WMI+MIFS повторно. Lock: к кэшу ходит и поток API-запросов.
    private const long BatteryReportTtlMs = 60_000;
    private readonly object _apiLock = new();
    private (SystemIntegration.BatteryReport report, long at)? _batteryCache;

    // HTTP API (XIC-13): хост живёт только пока фича включена; PowerDraw — свой (ленивый),
    // чтобы не делить хэндл батареи с «Монитором» между потоками
    private readonly ApiSettings _api;
    private HttpApi? _apiHost;
    private PowerDraw? _apiDraw;

    // командный слой: все Set*/Toggle* и стартовая логика — в AppController
    private readonly AppController _controller;

    // политика обновления значка (опрос/кэш/тема) — вынесена в TrayIconController
    private readonly TrayIconController _icon;

    // Индикатор-метрика в трее (XIC-35): существует только при включённой опции —
    // выключено значит не создан вовсе (ни значка, ни таймера, ни источников)
    private TrayMetricIcon? _metric;

    // «В дорогу»: наблюдение за 100% вынесено в TravelChargeMonitor
    private readonly TravelChargeMonitor _travel;

    // Жесты Mi-кнопки и роутинг клавиш — вынесены в Input/ (MiButtonGesture, KeyRouter)
    private readonly MiButtonGesture _mi;
    private readonly KeyRouter _router;

    // Конструктор только сохраняет зависимости и монтирует UI-каркас (меню, значок,
    // панель, подписки, колбэки контроллера). Стартовая бизнес-логика — в Start().
    public TrayApp(IMifsClient mifs, AppConfig cfg, IKeyEventSource events, IPowerEvents power,
        PowerProfileGuard powerGuard, TouchpadControl touchpad, TouchscreenControl touchscreen,
        TravelChargeMonitor travel, TrayIconController icon, AppController controller, ApiSettings api)
    {
        _mifs = mifs;
        _cfg = cfg;
        _events = events;
        _power = power;
        _travel = travel;
        _icon = icon;
        _controller = controller;
        _api = api;

        // меню трея: построение/тема/показ — в TrayMenuBuilder, окна и выход — наши колбэки
        _menu = new TrayMenuBuilder(cfg, mifs, controller)
        {
            ShowMonitor = ShowMonitor,
            OpenSettings = OpenSettings,
            ExitRequested = () => { _tray!.Visible = false; Application.Exit(); },
            MonitorVisible = () => _monitor?.Visible == true,
        };

        _tray = new NotifyIcon
        {
            Icon = TrayIcons.ForMode(null, Theme.TaskbarIsLight()),
            Visible = true,
            Text = Loc.T("app.name"),
            ContextMenuStrip = _menu.Menu,
        };
        // контроллер решает «когда перерисовать», мы — «как» (рендер + NotifyIcon)
        _icon.Apply = (mode, light) => Safe(() => { _tray.Icon = TrayIcons.ForMode(mode, light); return true; }, false);
        // живой тултип (имя • режим • заряд) — обновляется на каждом опросе значка
        _icon.Polled = mode => Safe(() => { _tray.Text = TrayText(mode); return true; }, false);
        // Левый клик — быстрая панель (богатый UI не прячем за аппаратной кнопкой); правый — меню
        _tray.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) _panel!.Toggle(); };

        // Профили питания: после применения режима обновить значок трея
        powerGuard.ModeApplied = () =>
        {
            if (_osd.IsHandleCreated) _osd.BeginInvoke(new Action(() => _icon.Refresh()));
        };

        // Дозарядились до 100% «в дорогу» → OSD + джингл (сам опрос — в мониторе)
        _travel.Ready = () =>
        {
            _osd.Flash(OsdKind.Travel, Loc.T("osd.travel.ready"));
            if (_cfg.TravelSound) Sound.PlayTravelReady(_cfg.TravelSoundFile);
        };

        // OSD на смену питания. Подписка через IPowerEvents (не SystemEvents напрямую):
        // прод-реализация маршалит Resume/StatusChange в UI-поток — иначе Rearm «в дорогу»
        // стартовал бы таймер с фонового потока SystemEvents, где тот не тикает
        _ = _osd.Handle; // форсируем создание хэндла для маршалинга событий в UI-поток
        _lastOnline = PowerLine.IsOnline();
        _power.PowerModeChanged += OnPower;
        SystemEvents.SessionSwitch += OnSessionSwitch; // блокировка/разблокировка — для «слепой» обратной связи

        // Панель по Mi-кнопке + слушатель клавиш прошивки. Панель — чистый view:
        // команды идут в контроллер, обратная связь — его колбэками ниже.
        _panel = new QuickPanelForm(_mifs, _cfg, touchpad, touchscreen);
        _panel.MonitorRequested = ShowMonitor;
        _panel.SettingsRequested = OpenSettings;
        _panel.SetMode = _controller.SetMode;
        _panel.SetCare = _controller.ToggleCare;
        _panel.SetTravel = _controller.SetTravel;
        _panel.ToggleOwl = _controller.ToggleAwake;
        _panel.SetAutoHz = _controller.ToggleAutoHz;
        _panel.ToggleTouchpad = _controller.ToggleTouchpad;
        _panel.ToggleTouchscreen = _controller.ToggleTouchscreen;
        _panel.SyncCare = () => _controller.SyncCareFromFirmware();

        // Уведомления контроллера → обратная связь UI: панель открыта — обновляется она,
        // иначе OSD; значок обновляем после смены режима. Сама логика — в AppController.
        _controller.CareChanged = on =>
        {
            if (_panel.Visible) _panel.RefreshUi();
            else _osd.Flash(on ? OsdKind.CareOn : OsdKind.CareOff,
                            on ? Loc.T("osd.care.on") : Loc.T("osd.care.off"));
        };
        _controller.TravelChanged = on =>
        {
            // экран заблокирован: OSD под локскрином не виден (secure desktop) — «слепая» обратная
            // связь: различимый джингл вкл/выкл + toast (Windows сама показывает его на локскрине).
            // В обычном режиме — как раньше, без звука и тостов (XIC-11)
            if (_locked)
            {
                if (_cfg.TravelLockSound) Sound.PlayToggle(on);
                if (_cfg.TravelLockToast) Safe(() => { _tray.ShowBalloonTip(4000, Loc.T("app.name"),
                    on ? $"{Loc.T("osd.travel")} — {Loc.T("osd.travel.sub")}" : Loc.T("osd.travel.off"),
                    ToolTipIcon.Info); return true; }, false);
                return;
            }
            if (_panel.Visible) _panel.RefreshUi();
            else if (on) _osd.Flash(OsdKind.Travel, Loc.T("osd.travel"), Loc.T("osd.travel.sub"));
            else _osd.Flash(OsdKind.TravelOff, Loc.T("osd.travel.off"));
        };
        _controller.TravelCancelled = () => { if (_panel.Visible) _panel.RefreshUi(); };
        _controller.ModeSet = m =>
        {
            if (_panel.Visible) _panel.RefreshUi(); // выбор сделан в панели — OSD поверх не нужен
            else _osd.Flash(ModeUi.Kind(m), Loc.T(ModeUi.Key(m) ?? "mode.auto"));
            _icon.Refresh();
        };
        _controller.ModeCycled = m =>
        {
            if (_panel.Visible) _panel.RefreshUi(); // выбор «перелистывается» в панели, OSD не нужен
            else _osd.Flash(ModeUi.Kind(m), Loc.T(ModeUi.Key(m) ?? "mode.auto"));
            _icon.Refresh();
        };
        _controller.ProfileModeApplied = () => _icon.Refresh();
        _controller.ModesReloaded = _panel.ReloadModes;
        _controller.AutoHzChanged = on =>
        {
            if (_panel.Visible) _panel.RefreshUi();
            else if (on) _osd.Flash(OsdKind.RefreshRate, Loc.T("osd.hz.on"),
                                    Loc.T("osd.hz.on.sub", _cfg.AcRefreshRate, _cfg.BatteryRefreshRate));
            else _osd.Flash(OsdKind.RefreshRateOff, Loc.T("osd.hz.off"));
        };
        _controller.RefreshRateFeatureChanged = _panel.ReloadModes; // ячейка герцовки появляется/уходит
        _controller.OwlFeatureChanged = _panel.ReloadModes; // сова появляется/уходит из раскладки
        _controller.AwakeChanged = () => { if (_panel.Visible) _panel.RefreshUi(); };
        _controller.LanguageChanged = () => _icon.Refresh(); // Polled обновит тултип на новом языке
        _controller.FlyoutThemeChanged = RepaintFlyouts;     // палитру уже пересчитал контроллер
        // честная обратная связь: команда прошивке не прошла — говорим прямо, а не «успех»
        _controller.FirmwareFailed = () => _osd.Flash(OsdKind.Error, Loc.T("osd.failed"), Loc.T("osd.failed.sub"));
        _controller.UpdateFound = OnUpdateFound;
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_pendingUpdateUrl is not string url) return; // тост мог быть и не про обновление
            _pendingUpdateUrl = null;
            Safe(() => { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return true; }, false);
        };
        // тачпад/экран: колбэк приходит с фонового потока — маршалим в UI
        _controller.TouchpadToggled = b => _osd.BeginInvoke(new Action(() =>
        {
            if (_panel.Visible) _panel.RefreshUi();
            else _osd.Flash(b ? OsdKind.TouchpadOn : OsdKind.TouchpadOff,
                            Loc.T(b ? "osd.touchpad.on" : "osd.touchpad.off"));
        }));
        _controller.TouchscreenToggled = b => _osd.BeginInvoke(new Action(() =>
        {
            if (_panel.Visible) _panel.RefreshUi();
            else _osd.Flash(b ? OsdKind.TouchscreenOn : OsdKind.TouchscreenOff,
                            Loc.T(b ? "osd.touchscreen.on" : "osd.touchscreen.off"));
        }));
        // Все три жеста Mi настраиваются (MiClickAction/MiDoubleAction/MiHoldAction); "none"
        // гасит жест: двойной — одинарный срабатывает мгновенно, удержание — уходит в клик
        _mi = new MiButtonGesture(holdMs: _cfg.MiHoldMs, doubleClickMs: _cfg.MiDoubleClickMs)
        {
            Click = () => _router!.Run(_cfg.MiClickAction, _cfg.MiClickCommand),
            DoubleClick = () => _router!.Run(_cfg.MiDoubleAction, _cfg.MiDoubleCommand),
            Hold = () => _router!.Run(_cfg.MiHoldAction, _cfg.MiHoldCommand),
            DoubleEnabled = () => !IsNone(_cfg.MiDoubleAction),
            HoldEnabled = () => !IsNone(_cfg.MiHoldAction),
        };
        // Роутинг клавиш: исполнители действий — командный слой (окна/панель — наши)
        _router = new KeyRouter(_cfg, _mi)
        {
            CycleModes = _controller.CycleMode,
            ToggleCharge = _controller.ToggleCharge,
            TogglePanel = _panel.Toggle,
            ToggleOwl = _controller.ToggleAwake,
            ToggleMonitor = ToggleMonitor,
            ToggleTravel = () => _controller.SetTravel(!_cfg.TravelMode),
            ToggleTouchpad = _controller.ToggleTouchpad,
            ToggleTouchscreen = _controller.ToggleTouchscreen,
            Projection = KeyActions.Projection,
            OpenSettings = KeyActions.OpenSettings,
            Copilot = KeyActions.Copilot,
            MediaPlayPause = KeyActions.MediaPlayPause,
            MediaNext = KeyActions.MediaNext,
            MediaPrev = KeyActions.MediaPrev,
            MediaStop = KeyActions.MediaStop,
            Calculator = KeyActions.Calculator,
            Launch = KeyActions.LaunchCommand,
            MicKey = OnMicKey,
            BacklightKey = OnBacklightKey,
            FnLockKey = OnFnLockKey,
            PanelVisible = () => _panel.Visible,
        };
        _events.KeyPressed += OnKey;

        // Реакция на смену темы Windows
        SystemEvents.UserPreferenceChanged += OnUserPref;
    }

    /// <summary>
    /// Старт приложения: бизнес-логика — в контроллере, здесь только запуск слушателей
    /// и прогрев UI. Порядок — как до Application.Run.
    /// </summary>
    public void Start()
    {
        _osd.DurationMs = _cfg.OsdDurationMs; // настраиваемая длительность (config.json) — до первых OSD

        _controller.Startup();

        // слушатель клавиш прошивки + значок по реальному режиму (и его лёгкий опрос)
        _events.Start();
        _icon.Start();

        // индикатор-метрика (XIC-35): только при включённой опции
        if (_cfg.TrayMetricEnabled) StartMetric();

        // HTTP API: поднять хост, если фича включена (api.json); firewall на старте не трогаем —
        // правило создаётся/удаляется только явным тумблером во вкладке (как schtasks у автозапуска)
        if (_api.Enabled) StartApiHost();

        // «Прогрев» трей-меню: без него самый первый клик по значку проглатывается —
        // первый показ ContextMenuStrip после старта инициализирует ленивые ресурсы
        // (хэндл меню, первый WMI-вызов в BuildMenu, передний план приложения), и до
        // этого показ закрывается сразу. Делаем это сами на первом холостом ходу цикла
        // сообщений, за экраном и с мгновенным закрытием — пользователь ничего не видит.
        _osd.BeginInvoke(new Action(_menu.Prime));

        // Одноразовая подсказка первого запуска: как открыть панель и меню — иначе
        // самая богатая поверхность (QuickPanel) остаётся спрятанной за Mi-кнопкой
        if (!_cfg.FirstRunShown)
        {
            _cfg.FirstRunShown = true;
            _cfg.Save();
            _tray.ShowBalloonTip(10000, Loc.T("toast.firstrun.title"), Loc.T("toast.firstrun.text"), ToolTipIcon.None);
        }

        // Проверка обновления — с задержкой и в фоне: старт не замедляем, сеть в UI-поток не тащим
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await _controller.CheckUpdatesAsync(force: false).ConfigureAwait(false);
        });
    }

    // Новая версия: тост показываем ОДИН раз на версию (сразу помечаем её как показанную),
    // клик открывает страницу релиза. Отметка на «О программе» остаётся в любом случае.
    private void OnUpdateFound(ReleaseInfo release)
    {
        // колбэк приходит с фонового потока — в UI через форму OSD (NotifyIcon сам не Control)
        Safe(() =>
        {
            _osd.BeginInvoke(new Action(() =>
            {
                UpdateCheck.MarkNotified(_cfg, release);
                _pendingUpdateUrl = release.Url;
                _tray.ShowBalloonTip(10000, Loc.T("toast.update.title"),
                    Loc.T("toast.update.text", release.Tag), ToolTipIcon.None);
            }));
            return true;
        }, false);
    }

    // Тултип трея: имя • режим • заряд (NotifyIcon.Text ограничен 127 символами).
    private static string TrayText(PerfMode? mode)
    {
        string s = Loc.T("app.name");
        if (mode is PerfMode m && ModeUi.Key(m) is string key) s += " • " + Loc.T(key);
        float f = SystemInformation.PowerStatus.BatteryLifePercent;
        if (f is >= 0f and <= 1f) s += $" • {(int)Math.Round(f * 100)}%";
        return s.Length <= 127 ? s : s[..127];
    }

    private void OnUserPref(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        _menu.ThemeChanged(); // перекрасить меню, если тема реально сменилась
        _icon.ThemeChanged(); // перечитать цвет панели задач и перерисовать значок
        _metric?.ThemeChanged(); // цифра индикатора — тем же контрастом к панели
        // флайауты в режиме «как в Windows» следуют теме тоже; в остальных Apply — no-op
        FlyoutPalette.Apply(_cfg.FlyoutTheme);
        RepaintFlyouts();
        // открытое окно настроек перечитывает тему «на лету» (Фаза 6.4)
        if (_settings is { IsDisposed: false, Visible: true }) _settings.ThemeChanged();
    }

    // Перерисовать видимые флайауты после смены их палитры (закрытые перерисуются при показе)
    private void RepaintFlyouts()
    {
        if (_panel.Visible) _panel.Invalidate();
        if (_monitor is { Visible: true }) _monitor.Invalidate();
        if (_osd.Visible) _osd.Invalidate();
    }

    private void OnKey(byte code, byte value)
    {
        if (!_panel.IsHandleCreated) return;
        _panel.BeginInvoke(() => _router.Handle(code, value)); // все события — в UI-поток
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

    // Флаг «сессия заблокирована»: прочие типы SessionSwitch (logon/logoff/remote) не трогают —
    // logoff и так завершает приложение, а RDP-переключения к видимости OSD отношения не имеют
    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) _locked = true;
        else if (e.Reason == SessionSwitchReason.SessionUnlock) _locked = false;
    }

    // Resume/StatusChange приходят уже в UI-потоке (маршалит SystemEventsSource);
    // Suspend — синхронно с потока SystemEvents, но мы его отфильтровываем первой строкой.
    private void OnPower(PowerModes mode)
    {
        if (mode is not (PowerModes.StatusChange or PowerModes.Resume)) return;
        bool online = PowerLine.IsOnline();
        if (online == _lastOnline) return;   // только реальный переход AC↔батарея
        _lastOnline = online;

        // «В дорогу»: отключили зарядник — режим сбрасывается (следующее подключение снова 80%);
        // подключили при активном режиме — заново ждём 100%.
        if (_cfg.TravelMode)
        {
            if (!online) _controller.DisableTravel();
            else _travel.Rearm(); // подключили при активном режиме — заново ждём 100%
        }

        ShowPowerOsd(online);
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
            // мощность адаптера задаёт бейдж-оверлей качества зарядника и подпись; сам заряд
            // (лимит 80/100 или «В дорогу») — это база иконки. Два независимых измерения.
            // ADPW=0 → не-PD БП, мощность неизвестна (серый «?»); >0 и ниже порога → медленный («!»).
            int watts = Safe(() => _mifs.GetAdapterWatts(), 0);
            var badge = ChargeBadge.None;
            string? note = null;
            if (_cfg.ChargerWattsOsd)
            {
                if (watts == 0) { badge = ChargeBadge.NoPd; note = Loc.T("osd.charger.nopd"); }
                else
                {
                    note = Loc.T("osd.charger.watts", watts);
                    if (_cfg.WeakChargerWatts > 0 && watts < _cfg.WeakChargerWatts) badge = ChargeBadge.Slow;
                }
            }

            if (_cfg.TravelMode)
                _osd.Flash(OsdKind.Travel, Loc.T("osd.travel"), Append(Loc.T("osd.travel.sub"), note), badge);
            else if (_cfg.ChargeCare)
                _osd.Flash(OsdKind.ChargingLimited, Loc.T("osd.charging.limited", _cfg.CarePercent()), Append(sub, note), badge);
            else
                _osd.Flash(OsdKind.Charging, Loc.T("osd.charging"), Append(sub, note), badge);
        }
        else
        {
            _osd.Flash(OsdKind.OnBattery, Loc.T("osd.onbattery"), sub);
        }
    }

    // Склейка строк подписи OSD через « • » (любая часть может быть null).
    private static string? Append(string? a, string? b) => a is null ? b : b is null ? a : $"{a} • {b}";

    // Жест выключен: действие слота — "none" (null = ещё не мигрированный конфиг, жест жив).
    private static bool IsNone(string? action) =>
        string.Equals(action, "none", StringComparison.OrdinalIgnoreCase);

    // ---- HTTP API (XIC-13) ----

    // Поднять хост по текущим настройкам. Команды маршалим в UI-поток через _osd (хэндл форсирован
    // в ctor) — запросы приходят с пула потоков, а контроллер трогает конфиг/OSD/значок.
    private void StartApiHost()
    {
        var router = new ApiRouter(_api)
        {
            SetMode = m => _osd.BeginInvoke(new Action(() => _controller.SetMode(m))),
            SetCare = on => _osd.BeginInvoke(new Action(() => _controller.ToggleCare(on))),
            SetTravel = on => _osd.BeginInvoke(new Action(() => _controller.SetTravel(on))),
            // контроллер умеет только переключать сову — приводим к желаемому состоянию сами
            SetOwl = on => _osd.BeginInvoke(new Action(() => { if (_cfg.Awake != on) _controller.ToggleAwake(); })),
            OwlFeature = () => _cfg.OwlMode,
            Status = ApiStatusSnapshot,
        };
        // занятый порт и т.п. — API просто не поднялся (ошибка в логе), приложение живёт дальше
        _apiHost = Safe<HttpApi?>(() => new HttpApi(_api, router), null);
    }

    // Вкладка «HTTP API» изменила настройки: сохранить api.json (с ACL) и пересоздать хост.
    // Firewall-правило — только здесь, по явному действию пользователя (LAN-режим вкл/выкл).
    private void ApiApplied()
    {
        ApiSettingsStore.Save(_api);
        _apiHost?.Dispose();
        _apiHost = null;
        if (_api.Enabled) StartApiHost();
        bool fw = _api.Enabled && _api.LanAccess;
        int port = _api.Port;
        Task.Run(() => ApiFirewall.Set(fw, port)); // netsh с WaitForExit — не на UI-потоке
    }

    // Снимок для GET /status: конфиг + PowerStatus (мгновенно), режим/здоровье — WMI/MIFS
    // (мы на потоке API, не UI). PowerDraw под замком: запросы могут прийти параллельно.
    private ApiStatus ApiStatusSnapshot()
    {
        var ps = SystemInformation.PowerStatus;
        float f = ps.BatteryLifePercent;
        int? pct = (f >= 0f && f <= 1f) ? (int)Math.Round(f * 100) : null;
        float? watts = null;
        lock (_apiLock)
        {
            _apiDraw ??= new PowerDraw();
            if (_apiDraw.TryReadWatts(out float w) && !float.IsNaN(w)) watts = w;
        }
        var mode = Safe<PerfMode?>(() => _mifs.GetPerfMode(), null);
        return new ApiStatus(
            mode?.ToString() ?? "unknown",
            _cfg.ChargeCare, _cfg.TravelMode, _cfg.Awake,
            pct, PowerLine.IsOnline(ps.PowerLineStatus), watts,
            BatteryReportCached().HealthPercent);
    }

    // Отчёт о батарее с TTL-кэшем — общий для BatteryTab и /status (см. комментарий у кэша).
    private SystemIntegration.BatteryReport BatteryReportCached()
    {
        lock (_apiLock)
        {
            if (_batteryCache is (var cached, var at) && Environment.TickCount64 - at < BatteryReportTtlMs)
                return cached; // свежий кэш — WMI/MIFS не трогаем
            var r = SystemIntegration.BatteryInfo.Read(); // штатные WMI-классы (циклы, ёмкость, износ)
            // здоровья в WMI нет → падаем на SOH1 из прошивки (MIFS), это тоже оценка ёмкости
            if (r.HealthPercent is null && Safe(() => _mifs.GetBatteryHealth(), (int?)null) is int soh && soh > 0)
                r = r with { HealthPercent = soh };
            _batteryCache = (r, Environment.TickCount64);
            return r;
        }
    }

    private SettingsForm? _settings;

    // Открыть окно настроек (лениво создаётся, дальше — из спрятанного состояния).
    private void OpenSettings()
    {
        if (_settings is null || _settings.IsDisposed)
        {
            var act = new SettingsActions
            {
                GetAutoStart = () => _controller.AutoStartEnabled,
                SetAutoStart = _controller.ToggleAutoStart,
                Languages = () => _controller.Languages,
                CurrentLanguage = () => _controller.CurrentLanguage,
                SetLanguage = _controller.SetLanguage,
                SetFlyoutTheme = _controller.SetFlyoutTheme,
                SetModeVisibility = _controller.ToggleModeVisibility,
                GetStartStrategy = () => _controller.CurrentStartStrategy,
                SetStartStrategy = _controller.SetStartStrategy,
                SetProfileMode = _controller.SetProfileMode,
                SetRememberBrightness = _controller.SetRememberBrightness,
                SetBrightnessCap = _controller.SetBrightnessCap,
                SetBrightnessCaps = _controller.SetBrightnessCaps,
                // предупреждаем, если адаптивная яркость включена хоть для одного источника:
                // лимит не сработает ровно тогда, когда питание переключится на него
                IsAdaptiveBrightness = () => AdaptiveBrightness.IsEnabled(true) || AdaptiveBrightness.IsEnabled(false),
                SetAutoBrightness = _controller.SetAutoBrightness,
                SetAutoBrightnessLearning = _controller.SetAutoBrightnessLearning,
                SetAutoBrightnessRevert = _controller.SetAutoBrightnessRevert,
                IsAlsAvailable = () => _controller.AlsAvailable,
                CurrentLux = () => _controller.CurrentLux,
                SetBrightnessMedianSec = _controller.SetBrightnessMedianSec,
                ResetBrightnessCurve = _controller.ResetBrightnessCurve,
                BrightnessCurvePoints = _controller.BrightnessCurvePoints,
                SetAutoHz = _controller.ToggleAutoHz,
                SetRefreshRateFeature = _controller.ToggleRefreshRateFeature,
                SetHoldRefreshRate = _controller.SetHoldRefreshRate,
                SetRefreshRates = _controller.SetRefreshRates,
                SetCheckUpdates = _controller.SetCheckUpdates,
                GetUpdate = () => _controller.Update,
                GetUpdateStatus = () => _controller.LastUpdateCheck,
                CheckUpdatesNow = done => _ = Task.Run(async () =>
                {
                    await _controller.CheckUpdatesAsync(force: true).ConfigureAwait(false);
                    // перерисовать вкладку с найденной версией — обратно в UI-поток
                    _osd.BeginInvoke(done);
                }),
                SetTouchpadDeadZone = _controller.SetTouchpadDeadZone,
                SetTouchpadDeadZoneMm = _controller.SetTouchpadDeadZoneMm,
                SetOwlFeature = _controller.ToggleOwlFeature,
                SetCareLimit = _controller.SetCareLimit,
                GetBatteryReport = BatteryReportCached,
                GetApiSettings = () => _api,
                ApiApplied = ApiApplied,
                TrayMetricApplied = TrayMetricApplied,
            };
            _settings = new SettingsForm(_cfg, act);
        }
        _settings.Popup();
    }

    private MonitorForm? _monitor;

    private void ShowMonitor()
    {
        _monitor ??= new MonitorForm(_cfg, _mifs);
        _monitor.Popup();
    }

    // ---- Индикатор-метрика в трее (XIC-35) ----

    // Маршал-контрол — OSD-форма (её хэндл форсирован в ctor); клик по индикатору открывает «Монитор»
    private void StartMetric()
    {
        _metric = new TrayMetricIcon(_cfg, _osd, ShowMonitor);
        _metric.Start();
    }

    // Вкладка настроек изменила индикатор: вкл/выкл — создание/уничтожение целиком
    // (выключено = объекта не существует, ноль нагрузки), метрика/период — на лету
    private void TrayMetricApplied()
    {
        if (!_cfg.TrayMetricEnabled) { _metric?.Dispose(); _metric = null; return; }
        if (_metric is null) StartMetric();
        else _metric.SettingsChanged();
    }

    // Для действия клавиши "monitor": повторное нажатие прячет виджет
    private void ToggleMonitor()
    {
        if (_monitor?.Visible == true) _monitor.Hide();
        else ShowMonitor();
    }

    private static T Safe<T>(Func<T> f, T fallback,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        try { return f(); }
        catch (Exception ex) { Log.Ex($"TrayApp.{caller}", ex); return fallback; }
    }

    public void Dispose()
    {
        _controller.Shutdown(); // вернуть действие крышки (сова)

        _apiHost?.Dispose();    // остановить слушатель API (firewall-правило не трогаем — фича включена)
        _apiDraw?.Dispose();

        _power.PowerModeChanged -= OnPower;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.UserPreferenceChanged -= OnUserPref;
        _mi.Dispose();
        // _events / guard-ы / IPowerEvents / IMifsClient диспоузит DI-провайдер
        // (в обратном порядке создания), TrayApp ими не владеет
        _metric?.Dispose(); // значок индикатора гасится до основного (Visible=false внутри)
        _osd.Dispose();
        _panel.Dispose();
        _settings?.Dispose();
        _monitor?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        TrayIcons.DisposeAll();
    }
}
