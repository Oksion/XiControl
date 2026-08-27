using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using Windows.UI.ViewManagement;
using XiControl.Config;
using XiControl.Input;
using XiControl.Localization;
using XiControl.SystemIntegration;
using XiControl.Wmi;

namespace XiControl.Ui;

/// <summary>Компоновщик приложения: нативный tray + окна WinUI; команды по-прежнему идут через AppController.</summary>
public sealed class TrayApp : IDisposable
{
    private readonly IMifsClient _mifs;
    private readonly AppConfig _cfg;
    private readonly IKeyEventSource _events;
    private readonly IPowerEvents _power;
    private readonly TravelChargeMonitor _travel;
    private readonly TrayIconController _iconController;
    private readonly AppController _controller;
    private readonly ApiSettings _api;
    private readonly NativeTrayIcon _tray = new();
    private readonly TrayMenuWindow _trayMenu = new();
    private readonly OsdWindow _osd = new();
    private readonly OemOsdWindow _oemOsd = new();
    private readonly QuickPanelWindow _panel;
    private readonly DispatcherQueue _queue;
    private readonly UISettings _uiSettings = new();
    private readonly TrayCallbackGate _primaryTrayGate = new();
    private readonly TrayCallbackGate _contextTrayGate = new();
    private readonly MiButtonGesture _mi;
    private readonly KeyRouter _router;
    private readonly object _apiLock = new();
    private const long BatteryReportTtlMs = 60_000;

    private MonitorWindow? _monitor;
    private SettingsWindow? _settings;
    private TrayMetricIcon? _metric;
    private HttpApi? _apiHost;
    private PowerDraw? _apiDraw;
    private (BatteryReport report, long at)? _batteryCache;
    private bool _lastOnline;
    private bool _locked;
    private string? _pendingUpdateUrl;
    private bool _disposed;
    private int _themeChangeGeneration;

    public TrayApp(IMifsClient mifs, AppConfig cfg, IKeyEventSource events, IPowerEvents power,
        PowerProfileGuard powerGuard, TouchpadControl touchpad, TouchscreenControl touchscreen,
        TravelChargeMonitor travel, TrayIconController icon, AppController controller, ApiSettings api)
    {
        _mifs = mifs;
        _cfg = cfg;
        _events = events;
        _power = power;
        _travel = travel;
        _iconController = icon;
        _controller = controller;
        _api = api;
        _queue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("TrayApp должен создаваться в WinUI-потоке.");
        FlyoutPalette.Apply(cfg.FlyoutTheme);

        _panel = new QuickPanelWindow(mifs, cfg, controller, touchpad, touchscreen)
        {
            SettingsRequested = OpenSettings,
            MonitorRequested = ToggleMonitor,
        };
        // Leave the shell callback stack before creating/activating XAML windows. This also
        // guarantees that the very first context click is processed after the dispatcher starts.
        _tray.Activated += () => QueueTrayAction(_primaryTrayGate, _panel.Toggle);
        _tray.ContextRequested += () => QueueTrayAction(_contextTrayGate, ShowTrayMenu);
        _tray.BalloonActivated += () => _queue.TryEnqueue(OpenPendingUpdate);

        _iconController.Apply = (mode, light) => _tray.Icon = TrayIcons.ForMode(mode, light);
        _iconController.Polled = mode => _tray.Tooltip = TrayText(mode);
        powerGuard.ModeApplied = () => _queue.TryEnqueue(() => _iconController.Refresh());

        _travel.Ready = () => _queue.TryEnqueue(() =>
        {
            _osd.Flash(OsdKind.Travel, Loc.T("osd.travel.ready"));
            if (_cfg.TravelSound) Sound.PlayTravelReady(_cfg.TravelSoundFile);
        });

        _lastOnline = PowerLine.IsOnline();
        _power.PowerModeChanged += OnPower;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _uiSettings.ColorValuesChanged += OnColorValuesChanged;

        MountControllerCallbacks();
        _mi = new MiButtonGesture(holdMs: cfg.MiHoldMs, doubleClickMs: cfg.MiDoubleClickMs)
        {
            Click = () => _router!.Run(cfg.MiClickAction, cfg.MiClickCommand),
            DoubleClick = () => _router!.Run(cfg.MiDoubleAction, cfg.MiDoubleCommand),
            Hold = () => _router!.Run(cfg.MiHoldAction, cfg.MiHoldCommand),
            DoubleEnabled = () => !IsNone(cfg.MiDoubleAction),
            HoldEnabled = () => !IsNone(cfg.MiHoldAction),
        };
        _router = BuildKeyRouter();
        _events.KeyPressed += OnKey;
    }

    public Action? ExitRequested { get; set; }

    private void QueueTrayAction(TrayCallbackGate gate, Action action)
    {
        if (!gate.TryEnter()) return;
        _queue.TryEnqueue(() =>
        {
            if (!_disposed) action();
        });
    }

    public void Start()
    {
        _osd.DurationMs = _cfg.OsdDurationMs;
        _oemOsd.DurationMs = _cfg.OsdDurationMs;
        _controller.Startup();

        _tray.Icon = TrayIcons.ForMode(null, Theme.TaskbarIsLight());
        _tray.Tooltip = Loc.T("app.name");
        _tray.Show();
        _events.Start();
        _iconController.Start();
        if (_cfg.TrayMetricEnabled) StartMetric();
        if (_api.Enabled) StartApiHost();

        if (!_cfg.FirstRunShown)
        {
            _cfg.FirstRunShown = true;
            _cfg.Save();
            _tray.ShowBalloon(Loc.T("toast.firstrun.title"), Loc.T("toast.firstrun.text"));
        }

        _panel.Popup();

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await _controller.CheckUpdatesAsync(force: false).ConfigureAwait(false);
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _controller.Shutdown();
        _apiHost?.Dispose();
        _apiDraw?.Dispose();
        _power.PowerModeChanged -= OnPower;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _uiSettings.ColorValuesChanged -= OnColorValuesChanged;
        _events.KeyPressed -= OnKey;
        _mi.Dispose();
        _metric?.Dispose();
        _settings?.Dispose();
        _monitor?.Dispose();
        _panel.Dispose();
        _trayMenu.Dispose();
        _oemOsd.Dispose();
        _osd.Dispose();
        _tray.Dispose();
        TrayIcons.DisposeAll();
    }

    private void MountControllerCallbacks()
    {
        _controller.CareChanged = on =>
        {
            if (_panel.IsVisible) _panel.RefreshUi();
            else _osd.Flash(on ? OsdKind.CareOn : OsdKind.CareOff,
                Loc.T(on ? "osd.care.on" : "osd.care.off"));
        };
        _controller.TravelChanged = on =>
        {
            if (_locked)
            {
                if (_cfg.TravelLockSound) Sound.PlayToggle(on);
                if (_cfg.TravelLockToast) _tray.ShowBalloon(Loc.T("app.name"),
                    on ? $"{Loc.T("osd.travel")} — {Loc.T("osd.travel.sub")}" : Loc.T("osd.travel.off"));
                return;
            }
            if (_panel.IsVisible) _panel.RefreshUi();
            else if (on) _osd.Flash(OsdKind.Travel, Loc.T("osd.travel"), Loc.T("osd.travel.sub"));
            else _osd.Flash(OsdKind.TravelOff, Loc.T("osd.travel.off"));
        };
        _controller.TravelCancelled = () => _panel.RefreshUi();
        _controller.ModeSet = ModeChanged;
        _controller.ModeCycled = ModeChanged;
        _controller.ProfileModeApplied = () => _iconController.Refresh();
        _controller.ModesReloaded = _panel.ReloadModes;
        _controller.AutoHzChanged = on =>
        {
            if (_panel.IsVisible) _panel.RefreshUi();
            else if (on) _osd.Flash(OsdKind.RefreshRate, Loc.T("osd.hz.on"),
                Loc.T("osd.hz.on.sub", _cfg.AcRefreshRate, _cfg.BatteryRefreshRate));
            else _osd.Flash(OsdKind.RefreshRateOff, Loc.T("osd.hz.off"));
        };
        _controller.RefreshRateFeatureChanged = _panel.ReloadModes;
        _controller.OwlFeatureChanged = _panel.ReloadModes;
        _controller.AwakeChanged = _panel.RefreshUi;
        _controller.LanguageChanged = () =>
        {
            _iconController.Refresh(force: true);
            _settings?.Rebuild();
        };
        _controller.FlyoutThemeChanged = () =>
        {
            _panel.ThemeChanged();
            _monitor?.ThemeChanged();
            _settings?.Rebuild();
        };
        _controller.FirmwareFailed = () => _osd.Flash(OsdKind.Error, Loc.T("osd.failed"), Loc.T("osd.failed.sub"));
        _controller.UpdateFound = OnUpdateFound;
        _controller.TouchpadToggled = enabled => _queue.TryEnqueue(() =>
        {
            if (_panel.IsVisible) _panel.RefreshUi();
            else _oemOsd.Flash(enabled ? "TouchpadOn" : "TouchpadOff");
        });
        _controller.TouchscreenToggled = enabled => _queue.TryEnqueue(() =>
        {
            if (_panel.IsVisible) _panel.RefreshUi();
            else _oemOsd.Flash(enabled ? "TouchScreenOn" : "TouchScreenOff");
        });
    }

    private KeyRouter BuildKeyRouter() => new(_cfg, _mi)
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
        Screenshot = KeyActions.Screenshot,
        TaskView = KeyActions.TaskView,
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
        ProjectionWarningKey = OnProjectionWarningKey,
        TouchpadStateKey = value => _oemOsd.Flash(value == 1 ? "TouchpadOn" : "TouchpadOff"),
        LowPowerKey = _ => _oemOsd.Flash("ChargeLowPower"),
        NumLockKey = value => _oemOsd.Flash(value != 0 ? "NumLock" : "NumUnlock"),
        RefreshRateKey = OnRefreshRateKey,
        WinKeyLockKey = _ => _oemOsd.Flash("WinKeyDisabled"),
        CameraPrivacyKey = value => Log.Write($"Key: camera/privacy 0xA0 value=0x{value:X2}"),
        FnLockKey = value => _oemOsd.Flash(value != 0 ? "FnLock" : "FnUnlock"),
        CapsLockKey = value => _oemOsd.Flash(value != 0 ? "CapsLock" : "CapsUnlock"),
        PerformanceKey = OnPerformanceKey,
        PanelVisible = () => _panel.IsVisible,
    };

    private void ShowTrayMenu()
    {
        // Never put a synchronous firmware/WMI call in the tray click path. The icon controller
        // already keeps this value fresh, and a busy firmware lock must not make the menu vanish.
        PerfMode? current = _iconController.CurrentMode;
        var commands = new Dictionary<uint, Action>
        {
            [1] = _controller.ToggleCharge,
            [2] = () => _controller.SetTravel(!_cfg.TravelMode),
            [5] = ToggleMonitor,
            [10] = OpenSettings,
            [11] = () => ExitRequested?.Invoke(),
        };
        var items = new List<TrayMenuEntry>
        {
            new(1, Loc.T("menu.charge", _cfg.CarePercent()), SvgIcons.MenuBattery, _cfg.ChargeCare),
            new(2, Loc.T("menu.travel"), SvgIcons.MenuTravel, _cfg.TravelMode, _cfg.ChargeCare),
        };
        var toolItems = new List<TrayMenuEntry>();
        if (_cfg.OwlMode)
        {
            toolItems.Add(new TrayMenuEntry(3, Loc.T("panel.awake"), SvgIcons.MenuOwl, _cfg.Awake));
            commands[3] = _controller.ToggleAwake;
        }
        if (_cfg.RefreshRateFeature)
        {
            toolItems.Add(new TrayMenuEntry(4, Loc.T("panel.hz"),
                SvgIcons.MenuRefreshRate, _cfg.AutoRefreshRate));
            commands[4] = () => _controller.ToggleAutoHz(!_cfg.AutoRefreshRate);
        }
        toolItems.Add(new TrayMenuEntry(5, Loc.T("menu.monitor"), SvgIcons.MenuMonitor, _monitor?.IsVisible == true));
        toolItems.Add(new TrayMenuEntry(10, Loc.T("menu.settings") + "…", SvgIcons.MenuSettings));
        var modeItems = new List<TrayMenuEntry>();
        for (int i = 0; i < _controller.VisibleModes.Count; i++)
        {
            PerfMode mode = _controller.VisibleModes[i];
            uint id = (uint)(100 + i);
            modeItems.Add(new TrayMenuEntry(id, Loc.T(ModeUi.Key(mode)!), ModeUi.MenuSvgIcon(mode), current == mode));
            commands[id] = () => _controller.SetMode(mode);
        }
        string currentName = current is PerfMode selected && ModeUi.Key(selected) is string key ? Loc.T(key) : "—";
        items.Add(TrayMenuEntry.Separator());
        items.Add(new TrayMenuEntry(20, $"{Loc.T("menu.perf")} · {currentName}", SvgIcons.MenuPerformance, Children: modeItems));
        items.Add(TrayMenuEntry.Separator());
        items.Add(TrayMenuEntry.Group(toolItems));
        items.Add(TrayMenuEntry.Separator());
        items.Add(new TrayMenuEntry(11, Loc.T("menu.exit"), SvgIcons.MenuExit));
        _trayMenu.Popup(items, command =>
        {
            if (commands.TryGetValue(command, out Action? action)) action();
        });
    }

    private void ModeChanged(PerfMode mode)
    {
        if (_panel.IsVisible) _panel.RefreshUi();
        else _oemOsd.Flash(PerformanceFamily(mode));
        _iconController.Refresh();
    }

    private void OnKey(byte code, byte value) => _queue.TryEnqueue(() => _router.Handle(code, value));

    private void OnMicKey(byte value)
    {
        bool mute = value == 0;
        using (var mic = new MicControl()) if (mic.Available) mic.SetMute(mute);
        _oemOsd.Flash(mute ? "MuteOn" : "MuteOff");
    }

    private void OnBacklightKey(byte value)
    {
        if (value == 0x80) _oemOsd.Flash("KeyboardLightAuto");
        else if (value <= 10) _oemOsd.Flash($"KeyboardLight{value}");
        else Log.Write($"Key: unknown keyboard backlight value=0x{value:X2}");
    }

    private void OnProjectionWarningKey(byte value)
    {
        if (value == 2) _oemOsd.Flash("ChargeWrongSocket");
        else Log.Write($"Key: unknown projection/power value=0x{value:X2}");
    }

    private void OnPerformanceKey(byte value)
    {
        if (ModeUi.FromHotkeyValue(value) is not PerfMode mode) return;
        _cfg.RememberMode(mode);
        if (_panel.IsVisible) _panel.RefreshUi(); else _oemOsd.Flash(PerformanceFamily(value));
        _iconController.Refresh();
    }

    private void OnRefreshRateKey(byte value)
    {
        _ = Task.Run(() =>
        {
            int? hz = RefreshRate.Cycle();
            _queue.TryEnqueue(() => _oemOsd.Flash(hz switch
            {
                48 => "DisFre48",
                60 => "DisFre60",
                72 => "DisFre72",
                90 => "DisFre90",
                120 => "DisFre120",
                144 => "DisFre144",
                165 => "DisFre165",
                240 => "DisFre240",
                null => "DisFreErr",
                _ => "DisFreErrAuto",
            }));
        });
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) _locked = true;
        else if (e.Reason == SessionSwitchReason.SessionUnlock) _locked = false;
    }

    private void OnPower(PowerModes mode)
    {
        if (mode is not (PowerModes.StatusChange or PowerModes.Resume)) return;
        bool online = PowerLine.IsOnline();
        if (online == _lastOnline) return;
        _lastOnline = online;
        if (_cfg.TravelMode)
        {
            if (!online) _controller.DisableTravel(); else _travel.Rearm();
        }
        ShowPowerOsd(online);
    }

    private void ShowPowerOsd(bool online)
    {
        PowerSnapshot power = PowerStatus.Read();
        string? subtitle = power.BatteryPercent is int percent ? Loc.T("osd.level", percent) : null;
        if (_cfg.AutoRefreshRate && RefreshRate.Resolve(online ? _cfg.AcRefreshRate : _cfg.BatteryRefreshRate) is int rate)
            subtitle = Append(subtitle, Loc.T("osd.hz", rate));
        if (!online)
        {
            _osd.Flash(OsdKind.OnBattery, Loc.T("osd.onbattery"), subtitle);
            return;
        }
        int watts = Safe(() => _mifs.GetAdapterWatts(), 0);
        ChargeBadge badge = ChargeBadge.None;
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
        if (_cfg.TravelMode) _osd.Flash(OsdKind.Travel, Loc.T("osd.travel"), Append(Loc.T("osd.travel.sub"), note), badge);
        else if (_cfg.ChargeCare) _osd.Flash(OsdKind.ChargingLimited,
            Loc.T("osd.charging.limited", _cfg.CarePercent()), Append(subtitle, note), badge);
        else _osd.Flash(OsdKind.Charging, Loc.T("osd.charging"), Append(subtitle, note), badge);
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
        => ScheduleThemeChange();

    private void OnColorValuesChanged(UISettings sender, object args)
        => ScheduleThemeChange();

    private void ScheduleThemeChange()
    {
        int generation = Interlocked.Increment(ref _themeChangeGeneration);
        _queue.TryEnqueue(() =>
        {
            if (!_disposed) ApplyThemeChange();
        });
        _ = RecheckThemeAfterWindowsCommitAsync(generation);
    }

    private async Task RecheckThemeAfterWindowsCommitAsync(int generation)
    {
        await Task.Delay(400).ConfigureAwait(false);
        if (_disposed || generation != Volatile.Read(ref _themeChangeGeneration)) return;
        _queue.TryEnqueue(() =>
        {
            if (!_disposed && generation == Volatile.Read(ref _themeChangeGeneration))
                ApplyThemeChange();
        });
    }

    private void ApplyThemeChange()
    {
        _iconController.ThemeChanged();
        _metric?.ThemeChanged();
        FlyoutPalette.Apply(_cfg.FlyoutTheme);
        _trayMenu.Hide();
        _panel.ThemeChanged();
        _monitor?.ThemeChanged();
        _settings?.Rebuild();
    }

    private void OnUpdateFound(ReleaseInfo release) => _queue.TryEnqueue(() =>
    {
        UpdateCheck.MarkNotified(_cfg, release);
        _pendingUpdateUrl = release.Url;
        _tray.ShowBalloon(Loc.T("toast.update.title"), Loc.T("toast.update.text", release.Tag));
    });

    private void OpenPendingUpdate()
    {
        if (_pendingUpdateUrl is not string url) return;
        _pendingUpdateUrl = null;
        Safe(() => { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return true; }, false);
    }

    private void OpenSettings()
    {
        _settings ??= new SettingsWindow(_cfg, CreateSettingsActions());
        _settings.Popup();
    }

    private SettingsActions CreateSettingsActions() => new()
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
        SetHoldRefreshRate = _controller.SetHoldRefreshRate,
        SetRefreshRateFeature = _controller.ToggleRefreshRateFeature,
        SetRefreshRates = _controller.SetRefreshRates,
        SetCheckUpdates = _controller.SetCheckUpdates,
        GetUpdate = () => _controller.Update,
        GetUpdateStatus = () => _controller.LastUpdateCheck,
        CheckUpdatesNow = done => _ = Task.Run(async () =>
        {
            await _controller.CheckUpdatesAsync(force: true).ConfigureAwait(false);
            _queue.TryEnqueue(() => done());
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

    private void ToggleMonitor()
    {
        _monitor ??= new MonitorWindow(_cfg);
        _monitor.Toggle();
    }

    private void StartMetric()
    {
        _metric = new TrayMetricIcon(_cfg, ToggleMonitor);
        _metric.Start();
    }

    private void TrayMetricApplied()
    {
        if (!_cfg.TrayMetricEnabled) { _metric?.Dispose(); _metric = null; return; }
        if (_metric is null) StartMetric(); else _metric.SettingsChanged();
    }

    private void StartApiHost()
    {
        var router = new ApiRouter(_api)
        {
            SetMode = mode => _queue.TryEnqueue(() => _controller.SetMode(mode)),
            SetCare = on => _queue.TryEnqueue(() => _controller.ToggleCare(on)),
            SetTravel = on => _queue.TryEnqueue(() => _controller.SetTravel(on)),
            SetOwl = on => _queue.TryEnqueue(() => { if (_cfg.Awake != on) _controller.ToggleAwake(); }),
            OwlFeature = () => _cfg.OwlMode,
            Status = ApiStatusSnapshot,
        };
        _apiHost = Safe<HttpApi?>(() => new HttpApi(_api, router), null);
    }

    private void ApiApplied()
    {
        ApiSettingsStore.Save(_api);
        _apiHost?.Dispose();
        _apiHost = null;
        if (_api.Enabled) StartApiHost();
        _ = Task.Run(() => ApiFirewall.Set(_api.Enabled && _api.LanAccess, _api.Port));
    }

    private ApiStatus ApiStatusSnapshot()
    {
        PowerSnapshot power = PowerStatus.Read();
        float? watts = null;
        lock (_apiLock)
        {
            _apiDraw ??= new PowerDraw();
            if (_apiDraw.TryReadWatts(out float value) && !float.IsNaN(value)) watts = value;
        }
        PerfMode? mode = Safe<PerfMode?>(() => _mifs.GetPerfMode(), null);
        return new ApiStatus(mode?.ToString() ?? "unknown", _cfg.ChargeCare, _cfg.TravelMode, _cfg.Awake,
            power.BatteryPercent, PowerLine.IsOnline(power.LineStatus), watts, BatteryReportCached().HealthPercent);
    }

    private BatteryReport BatteryReportCached()
    {
        lock (_apiLock)
        {
            if (_batteryCache is (var cached, var at) && Environment.TickCount64 - at < BatteryReportTtlMs) return cached;
            BatteryReport report = BatteryInfo.Read();
            if (report.HealthPercent is null && Safe(() => _mifs.GetBatteryHealth(), (int?)null) is int health && health > 0)
                report = report with { HealthPercent = health };
            _batteryCache = (report, Environment.TickCount64);
            return report;
        }
    }

    private static string TrayText(PerfMode? mode)
    {
        string text = Loc.T("app.name");
        if (mode is PerfMode value && ModeUi.Key(value) is string key) text += " • " + Loc.T(key);
        if (PowerStatus.Read().BatteryPercent is int percent) text += $" • {percent}%";
        return text.Length <= 127 ? text : text[..127];
    }

    private static string PerformanceFamily(byte value) => value switch
    {
        0 => "WorkloadTurbo",
        1 => "WorkloadBalance",
        2 => "WorkloadSilent",
        3 => "WorkloadSpeed",
        4 => "WorkloadDeception",
        9 => "NewIntelligentMode",
        10 => "NewLongBatteryMode",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string PerformanceFamily(PerfMode mode) => mode switch
    {
        PerfMode.Balance => "WorkloadBalance",
        PerfMode.Quiet => "WorkloadSilent",
        PerfMode.Turbo => "WorkloadSpeed",
        PerfMode.FullSpeed => "WorkloadDeception",
        PerfMode.Auto => "NewIntelligentMode",
        PerfMode.Eco => "NewLongBatteryMode",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static string? Append(string? first, string? second) =>
        first is null ? second : second is null ? first : $"{first} • {second}";

    private static bool IsNone(string? action) => string.Equals(action, "none", StringComparison.OrdinalIgnoreCase);

    private static T Safe<T>(Func<T> operation, T fallback,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        try { return operation(); }
        catch (Exception ex) { Log.Ex($"TrayApp.{caller}", ex); return fallback; }
    }
}
