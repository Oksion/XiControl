using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using XiControl.Config;
using XiControl.Localization;
using XiControl.SystemIntegration;
using XiControl.Wmi;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;
using Line = Microsoft.UI.Xaml.Shapes.Line;
using Polyline = Microsoft.UI.Xaml.Shapes.Polyline;

namespace XiControl.Ui;

internal sealed class SettingsWindow : FlyoutWindow
{
    private readonly AppConfig _cfg;
    private readonly SettingsActions _actions;
    private readonly NavigationView _navigation;
    private readonly Border _surface;
    private DispatcherQueueTimer? _displayTimer;
    private Canvas? _brightnessCurveCanvas;
    private string _selected = "general";

    public SettingsWindow(AppConfig cfg, SettingsActions actions)
        : base(alwaysOnTop: false, hideFromTaskbar: false, cornerRadiusDips: WinUiRadii.Overlay)
    {
        _cfg = cfg;
        _actions = actions;
        Title = Loc.T("settings.title");
        _navigation = new NavigationView
        {
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false,
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            OpenPaneLength = 220,
            IsPaneOpen = true,
            IsTitleBarAutoPaddingEnabled = false,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        _navigation.SelectionChanged += (_, e) =>
        {
            if (e.SelectedItem is NavigationViewItem { Tag: string tag })
            {
                _selected = tag;
                BuildPage();
            }
        };
        var root = new Grid();
        root.Children.Add(_navigation);

        // No dedicated title/menu row: the navigation surface starts at y=0. A transparent
        // center strip remains draggable while the hamburger and floating close stay clickable.
        var dragRegion = new Grid
        {
            Height = 36,
            Margin = new Thickness(64, 0, 52, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        Canvas.SetZIndex(dragRegion, 1);
        root.Children.Add(dragRegion);

        var close = WindowChrome.Button(WindowChrome.CloseGlyph, Loc.T("panel.close"), Hide, close: true);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Margin = new Thickness(0, 4, 8, 0);
        Canvas.SetZIndex(close, 2);
        root.Children.Add(close);
        _surface = new Border
        {
            CornerRadius = new CornerRadius(WinUiRadii.Overlay),
            Child = root,
        };
        Content = _surface;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(dragRegion);
        Activated += (_, _) => { if (IsVisible) BuildNavigation(); };
        ApplyWindowTheme();
    }

    public void Popup()
    {
        BuildNavigation();
        Rectangle work = ScreenMetrics.WorkingAreaAtCursor();
        Size physical = PhysicalSizeForDips(work, 980, 760);
        int width = Math.Min(physical.Width, Math.Max(1, work.Width - 80));
        int height = Math.Min(physical.Height, Math.Max(1, work.Height - 80));
        ShowAt(work.Left + (work.Width - width) / 2, work.Top + (work.Height - height) / 2, width, height);
    }

    public void Rebuild()
    {
        if (IsVisible) BuildNavigation();
    }

    protected override void OnHidden() => StopDisplayTimer();

    public override void Dispose()
    {
        StopDisplayTimer();
        base.Dispose();
    }

    private void BuildNavigation()
    {
        Title = Loc.T("settings.title");
        ApplyWindowTheme();
        _navigation.MenuItems.Clear();
        AddNav("general", "settings.tab.general", Symbol.Setting);
        AddNav("features", "settings.tab.features", Symbol.AllApps);
        AddNav("battery", "settings.tab.battery", Symbol.Download);
        AddNav("display", "settings.tab.display", Symbol.FullScreen);
        AddNav("touchpad", "settings.tab.touchpad", Symbol.TouchPointer);
        AddNav("performance", "settings.tab.perf", Symbol.Repair);
        AddNav("keys", "settings.tab.keys", Symbol.Keyboard);
        AddNav("api", "settings.tab.api", Symbol.World);
        AddNav("about", "settings.tab.about", Symbol.Help);
        _navigation.SelectedItem = _navigation.MenuItems.Cast<NavigationViewItem>()
            .FirstOrDefault(item => Equals(item.Tag, _selected)) ?? _navigation.MenuItems[0];
        BuildPage();
    }

    private void ApplyWindowTheme()
    {
        _surface.RequestedTheme = FlyoutPalette.RequestedTheme;
        _surface.Background = new SolidColorBrush(FlyoutPalette.Card);
    }

    private void AddNav(string tag, string key, Symbol symbol) => _navigation.MenuItems.Add(new NavigationViewItem
    {
        Tag = tag,
        Content = Loc.T(key),
        Icon = new SymbolIcon(symbol),
    });

    private void BuildPage()
    {
        StopDisplayTimer();
        _brightnessCurveCanvas = null;
        var builder = new SettingsBuilder();
        switch (_selected)
        {
            case "features": BuildFeatures(builder); break;
            case "battery": BuildBattery(builder); break;
            case "display": BuildDisplay(builder); break;
            case "touchpad": BuildTouchpad(builder); break;
            case "performance": BuildPerformance(builder); break;
            case "keys": BuildKeys(builder); break;
            case "api": BuildApi(builder); break;
            case "about": BuildAbout(builder); break;
            default: BuildGeneral(builder); break;
        }
        _navigation.Content = new ScrollViewer
        {
            Content = builder.Root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    private void BuildGeneral(SettingsBuilder b)
    {
        b.Header("settings.tab.general", "settings.general.sub");
        var languages = _actions.Languages();
        b.Row("settings.language", "settings.language.desc",
            b.Combo(languages.Select(x => x.Name), IndexOf(languages.Select(x => x.Culture), _actions.CurrentLanguage()), i =>
            {
                _actions.SetLanguage(languages[i].Culture);
                Rebuild();
            }));
        b.Row("settings.autostart", "settings.autostart.desc", b.Toggle(_actions.GetAutoStart(), _actions.SetAutoStart));
        string?[] themes = [null, "light", "system"];
        b.Row("settings.flyout.theme", "settings.flyout.theme.desc",
            b.Combo([Loc.T("theme.dark"), Loc.T("theme.light"), Loc.T("theme.system")],
                Math.Max(0, Array.IndexOf(themes, _cfg.FlyoutTheme)), i => _actions.SetFlyoutTheme(themes[i])));
        b.Group("settings.general.comfort");
        b.Row("settings.updates.check", "settings.updates.check.desc", b.Toggle(_cfg.CheckUpdates, _actions.SetCheckUpdates));
        b.Row("settings.log", "settings.log.desc", b.Toggle(_cfg.LogEnabled, on =>
        {
            _cfg.LogEnabled = on;
            _cfg.Save();
            Log.Enabled = on;
        }));
        b.Group("settings.traymetric.group");
        b.Row("settings.traymetric", "settings.traymetric.desc", b.Toggle(_cfg.TrayMetricEnabled, on =>
        {
            _cfg.TrayMetricEnabled = on; _cfg.Save(); _actions.TrayMetricApplied(); Rebuild();
        }));
        string[] metrics = ["power", "cpu", "gpu", "ram", "temp"];
        b.Row("settings.traymetric.metric", "settings.traymetric.metric.desc",
            b.Combo(metrics.Select(x => Loc.T("traymetric." + x)), Math.Max(0, Array.IndexOf(metrics, _cfg.TrayMetricKind ?? "power")), i =>
            {
                _cfg.TrayMetricKind = metrics[i]; _cfg.Save(); _actions.TrayMetricApplied();
            }, _cfg.TrayMetricEnabled));
        int[] periods = [1, 2, 5, 10];
        b.Row("settings.traymetric.period", "settings.traymetric.period.desc",
            b.Combo(periods.Select(x => Loc.T("settings.traymetric.sec", x)), Math.Max(0, Array.IndexOf(periods, _cfg.TrayMetricPeriodSec)), i =>
            {
                _cfg.TrayMetricPeriodSec = periods[i]; _cfg.Save(); _actions.TrayMetricApplied();
            }, _cfg.TrayMetricEnabled));
    }

    private void BuildFeatures(SettingsBuilder b)
    {
        b.Header("settings.tab.features", "settings.features.sub");
        b.Row("settings.owl.feature", "settings.owl.feature.desc", b.Toggle(_cfg.OwlMode, _actions.SetOwlFeature));
        b.Row("settings.touchpad.feature", "settings.touchpad.feature.desc", b.Toggle(_cfg.TouchpadFeature, on =>
        { _cfg.TouchpadFeature = on; _cfg.Save(); }));
        b.Row("settings.touchscreen.feature", "settings.touchscreen.feature.desc", b.Toggle(_cfg.TouchscreenFeature, on =>
        { _cfg.TouchscreenFeature = on; _cfg.Save(); }));
        b.Row("settings.refresh.feature", "settings.refresh.feature.desc", b.Toggle(_cfg.RefreshRateFeature, on =>
        { _actions.SetRefreshRateFeature(on); Rebuild(); }));
    }

    private void BuildBattery(SettingsBuilder b)
    {
        b.Header("settings.tab.battery", "settings.battery.sub");
        b.Group("settings.battery.care");
        int[] limits = Mifs.ChargeCarePresets;
        b.Row("settings.battery.care.limit", "settings.battery.care.limit.desc",
            b.Combo(limits.Select(x => $"{x}%"), Math.Max(0, Array.IndexOf(limits, _cfg.CarePercent())), i =>
            { _actions.SetCareLimit(limits[i]); Rebuild(); }));
        b.Note(CareHintKey(_cfg.CarePercent()));
        b.Note("settings.battery.note");
        b.Group("settings.battery.travel");
        b.Row("settings.travel.sound", "settings.travel.sound.desc", b.Toggle(_cfg.TravelSound, on =>
        { _cfg.TravelSound = on; _cfg.Save(); }));
        var sound = b.Text(_cfg.TravelSoundFile ?? string.Empty, value =>
        { _cfg.TravelSoundFile = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); _cfg.Save(); });
        sound.PlaceholderText = "%USERPROFILE%\\Music\\ready.wav";
        b.Row("settings.travel.file", "settings.travel.file.desc", new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { sound, b.Button("settings.browse", () => _ = BrowseTravelSoundAsync(sound)) },
        });
        b.Row("settings.travel.lock.sound", "settings.travel.lock.sound.desc", b.Toggle(_cfg.TravelLockSound, on =>
        { _cfg.TravelLockSound = on; _cfg.Save(); }));
        b.Row("settings.travel.lock.toast", "settings.travel.lock.toast.desc", b.Toggle(_cfg.TravelLockToast, on =>
        { _cfg.TravelLockToast = on; _cfg.Save(); }));
        b.Note("settings.travel.lock.note");
        b.Group("settings.charger");
        b.Row("settings.charger.watts", "settings.charger.watts.desc", b.Toggle(_cfg.ChargerWattsOsd, on =>
        { _cfg.ChargerWattsOsd = on; _cfg.Save(); }));
        int[] weak = [0, 30, 45, 60, 90];
        b.Row("settings.charger.weak", "settings.charger.weak.desc",
            b.Combo(weak.Select(x => x == 0 ? Loc.T("settings.act.none") : $"{x} W"),
                Math.Max(0, Array.IndexOf(weak, _cfg.WeakChargerWatts)), i =>
                { _cfg.WeakChargerWatts = weak[i]; _cfg.Save(); }));
        BatteryReport report = _actions.GetBatteryReport();
        b.Group("settings.battery.state");
        if (report.HealthPercent is int health)
            b.Value("settings.battery.health", $"{health}%", "settings.battery.health.desc");
        if (report.Cycles is int cycles)
            b.Value("settings.battery.cycles", cycles.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "settings.battery.cycles.desc");
        if (report.DesignWh > 0 && report.FullWh > 0)
            b.Value("settings.battery.capacity", Loc.T("settings.battery.capacity.val", report.FullWh, report.DesignWh),
                "settings.battery.capacity.desc");
    }

    private void BuildDisplay(SettingsBuilder b)
    {
        b.Header("settings.tab.display", "settings.display.sub");
        b.Group("settings.bright.group");
        b.Row("settings.bright.cap", "settings.bright.cap.desc", b.Toggle(_cfg.BrightnessCapEnabled, on =>
        { _actions.SetBrightnessCap(on); Rebuild(); }));
        int[] percentages = ValuesWithCurrent([.. Enumerable.Range(0, 15).Select(i => 100 - i * 5)],
            _cfg.BrightnessCapAc, _cfg.BrightnessCapBattery);
        b.Row("settings.bright.cap.ac", "settings.bright.cap.ac.desc",
            b.Combo(percentages.Select(x => $"{x}%"), Nearest(percentages, _cfg.BrightnessCapAc), i =>
            _actions.SetBrightnessCaps(percentages[i], _cfg.BrightnessCapBattery), _cfg.BrightnessCapEnabled));
        b.Row("settings.bright.cap.battery", "settings.bright.cap.battery.desc",
            b.Combo(percentages.Select(x => $"{x}%"), Nearest(percentages, _cfg.BrightnessCapBattery), i =>
            _actions.SetBrightnessCaps(_cfg.BrightnessCapAc, percentages[i]), _cfg.BrightnessCapEnabled));
        if (_actions.IsAlsAvailable())
        {
            b.Row("settings.bright.auto", "settings.bright.auto.desc", b.Toggle(_cfg.AutoBrightness, on =>
            { _actions.SetAutoBrightness(on); Rebuild(); }));
            var luxValue = new TextBlock
            {
                FontSize = 14,
                TextAlignment = TextAlignment.Right,
                MinWidth = 90,
            };
            UpdateLux(luxValue);
            b.Row("settings.bright.lux", "settings.bright.lux.desc", luxValue);
            if (_cfg.AutoBrightness)
            {
                b.Row("settings.bright.learn", "settings.bright.learn.desc", b.Toggle(_cfg.AutoBrightnessLearning, on =>
                { _actions.SetAutoBrightnessLearning(on); Rebuild(); }));
                string?[] reverts = [null, "battery", "off"];
                b.Row("settings.bright.revert", "settings.bright.revert.desc",
                    b.Combo([Loc.T("settings.bright.revert.always"), Loc.T("settings.bright.revert.battery"), Loc.T("settings.bright.revert.off")],
                        Math.Max(0, Array.IndexOf(reverts, _cfg.AutoBrightnessRevert)),
                        i => _actions.SetAutoBrightnessRevert(reverts[i]), !_cfg.AutoBrightnessLearning));
                int[] medians = ValuesWithCurrent([0, 5, 10, 20, 30, 60], _cfg.AutoBrightnessMedianSec);
                b.Row("settings.bright.median", "settings.bright.median.desc",
                    b.Combo(medians.Select(x => x == 0 ? Loc.T("settings.bright.median.off") : Loc.T("settings.bright.median.val", x)),
                        Math.Max(0, Array.IndexOf(medians, _cfg.AutoBrightnessMedianSec)), i => _actions.SetBrightnessMedianSec(medians[i])));
                b.Root.Children.Add(BrightnessCurvePreview());
                b.Row("settings.bright.curve.reset", "settings.bright.curve.reset.desc",
                    b.Button("settings.bright.curve.reset.btn", () =>
                    {
                        _actions.ResetBrightnessCurve();
                        Rebuild();
                    }));
            }
            StartDisplayTimer(luxValue);
        }
        if ((_cfg.BrightnessCapEnabled || _cfg.AutoBrightness) && _actions.IsAdaptiveBrightness())
            b.Note("settings.bright.adaptive");
        b.Row("settings.profile.brightness", "settings.brightness.desc",
            b.Toggle(_cfg.RememberBrightness, _actions.SetRememberBrightness, !_cfg.AutoBrightness));
        if (_cfg.RefreshRateFeature)
        {
            b.Group("settings.hz.group");
            b.Row("settings.hz.auto", "settings.hz.auto.desc", b.Toggle(_cfg.AutoRefreshRate, on =>
            { _actions.SetAutoHz(on); Rebuild(); }));
            b.Row("settings.hz.hold", "settings.hz.hold.desc",
                b.Toggle(_cfg.HoldRefreshRate, _actions.SetHoldRefreshRate, _cfg.AutoRefreshRate));
            b.Group("settings.hz.rates");
            int[] rates = ValuesWithCurrent([144, 120, 90, 60, 48], _cfg.AcRefreshRate, _cfg.BatteryRefreshRate);
            b.Row("settings.hz.ac", "settings.hz.ac.desc", b.Combo(rates.Select(x => $"{x} {Loc.T("settings.hz.unit")}"), Nearest(rates, _cfg.AcRefreshRate), i =>
                _actions.SetRefreshRates(rates[i], _cfg.BatteryRefreshRate)));
            b.Row("settings.hz.battery", "settings.hz.battery.desc", b.Combo(rates.Select(x => $"{x} {Loc.T("settings.hz.unit")}"), Nearest(rates, _cfg.BatteryRefreshRate), i =>
                _actions.SetRefreshRates(_cfg.AcRefreshRate, rates[i])));
            b.Note("settings.hz.note");
        }
    }

    private Border BrightnessCurvePreview()
    {
        var acColor = Windows.UI.Color.FromArgb(255, 78, 161, 255);
        var batteryColor = Windows.UI.Color.FromArgb(255, 255, 177, 66);

        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        legend.Children.Add(CurveLegend(acColor, Loc.T("settings.bright.curve.ac")));
        legend.Children.Add(CurveLegend(batteryColor, Loc.T("settings.bright.curve.battery")));

        var canvas = new Canvas { Height = 170, MinWidth = 520 };
        _brightnessCurveCanvas = canvas;
        canvas.SizeChanged += (_, _) => DrawBrightnessCurves(canvas, acColor, batteryColor);

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = Loc.T("settings.bright.curve.preview"),
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        content.Children.Add(legend);
        content.Children.Add(canvas);

        return new Border
        {
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(18, 15, 18, 12),
            CornerRadius = new CornerRadius(WinUiRadii.Overlay),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(25, 128, 128, 128)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(25, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            Child = content,
        };
    }

    private static StackPanel CurveLegend(Windows.UI.Color color, string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        row.Children.Add(new Border
        {
            Width = 18,
            Height = 3,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock { Text = label, FontSize = 12, Opacity = 0.72 });
        return row;
    }

    private void DrawBrightnessCurves(
        Canvas canvas,
        Windows.UI.Color acColor,
        Windows.UI.Color batteryColor)
    {
        double width = canvas.ActualWidth;
        double height = canvas.ActualHeight;
        if (width <= 1 || height <= 1) return;
        canvas.Children.Clear();

        var gridBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 128, 128, 128));
        for (int i = 0; i <= 4; i++)
        {
            double y = i * (height - 1) / 4d;
            canvas.Children.Add(new Line { X1 = 0, X2 = width, Y1 = y, Y2 = y, Stroke = gridBrush, StrokeThickness = 1 });
            double x = i * (width - 1) / 4d;
            canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = height, Stroke = gridBrush, StrokeThickness = 1 });
        }

        int acCap = _cfg.BrightnessCapEnabled ? Math.Clamp(_cfg.BrightnessCapAc, 10, 100) : 100;
        int batteryCap = _cfg.BrightnessCapEnabled ? Math.Clamp(_cfg.BrightnessCapBattery, 10, 100) : 100;
        BrightnessCurve? ac = AddBrightnessCurve(canvas, _actions.BrightnessCurvePoints(true), acColor, acCap, width, height);
        BrightnessCurve? battery = AddBrightnessCurve(canvas, _actions.BrightnessCurvePoints(false), batteryColor, batteryCap, width, height);

        float lux = _actions.CurrentLux();
        if (!float.IsNaN(lux))
        {
            double x = LuxX(lux, width);
            canvas.Children.Add(new Line
            {
                X1 = x,
                X2 = x,
                Y1 = 0,
                Y2 = height,
                Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(150, 160, 160, 164)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 },
            });
            bool online = PowerLine.IsOnline();
            BrightnessCurve? active = online ? ac : battery;
            int cap = online ? acCap : batteryCap;
            if (active is not null)
            {
                var marker = new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = new SolidColorBrush(FlyoutPalette.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black),
                };
                Canvas.SetLeft(marker, x - 4);
                Canvas.SetTop(marker, PercentY(Math.Min(active.Predict(lux), cap), height) - 4);
                canvas.Children.Add(marker);
            }
        }
    }

    private static BrightnessCurve? AddBrightnessCurve(
        Canvas canvas,
        IReadOnlyList<Config.BrightnessPoint> source,
        Windows.UI.Color color,
        int cap,
        double width,
        double height)
    {
        var points = source.OrderBy(point => point.Lux).ToArray();
        if (points.Length == 0) return null;
        var curve = new BrightnessCurve([.. points]);
        var brush = new SolidColorBrush(color);
        var line = new Polyline
        {
            Stroke = brush,
            StrokeThickness = 2.5,
            StrokeLineJoin = PenLineJoin.Round,
        };
        const int sampleCount = 64;
        double maxLog = Math.Log10(10001d);
        for (int i = 0; i <= sampleCount; i++)
        {
            float lux = (float)(Math.Pow(10, maxLog * i / sampleCount) - 1);
            line.Points.Add(new Windows.Foundation.Point(LuxX(lux, width), PercentY(Math.Min(curve.Predict(lux), cap), height)));
        }
        canvas.Children.Add(line);

        foreach (Config.BrightnessPoint point in points)
        {
            var dot = new Ellipse { Width = 7, Height = 7, Fill = brush };
            Canvas.SetLeft(dot, LuxX(point.Lux, width) - 3.5);
            Canvas.SetTop(dot, PercentY(Math.Min(point.Percent, cap), height) - 3.5);
            canvas.Children.Add(dot);
        }
        return curve;
    }

    private static double LuxX(float lux, double width) =>
        Math.Log10(1d + Math.Clamp(lux, 0f, 10_000f)) / Math.Log10(10001d) * (width - 1);

    private static double PercentY(int percent, double height) =>
        (1d - Math.Clamp(percent, 0, 100) / 100d) * (height - 1);

    private void StartDisplayTimer(TextBlock luxValue)
    {
        _displayTimer = DispatcherQueue.CreateTimer();
        _displayTimer.Interval = TimeSpan.FromSeconds(1);
        _displayTimer.IsRepeating = true;
        _displayTimer.Tick += (_, _) =>
        {
            if (!IsVisible || _selected != "display") return;
            UpdateLux(luxValue);
            if (_brightnessCurveCanvas is { } canvas)
                DrawBrightnessCurves(canvas,
                    Windows.UI.Color.FromArgb(255, 78, 161, 255),
                    Windows.UI.Color.FromArgb(255, 255, 177, 66));
        };
        _displayTimer.Start();
    }

    private void UpdateLux(TextBlock value)
    {
        float lux = _actions.CurrentLux();
        value.Text = float.IsNaN(lux) ? "—" : Loc.T("settings.bright.lux.val", Math.Round(lux));
    }

    private void StopDisplayTimer()
    {
        _displayTimer?.Stop();
        _displayTimer = null;
    }

    private void BuildTouchpad(SettingsBuilder b)
    {
        b.Header("settings.tab.touchpad", "settings.touchpad.sub");
        b.Row("settings.touchpad.deadzone", "settings.touchpad.deadzone.desc", b.Toggle(_cfg.TouchpadDeadZone, on =>
        { _actions.SetTouchpadDeadZone(on); Rebuild(); }));
        var deadZone = b.Number(_cfg.TouchpadDeadZoneMm, 4, 30, _actions.SetTouchpadDeadZoneMm, _cfg.TouchpadDeadZone);
        b.Row("settings.touchpad.deadzone.size", "settings.touchpad.deadzone.size.desc", new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                deadZone,
                new TextBlock
                {
                    Text = Loc.T("settings.touchpad.deadzone.unit"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.7,
                },
            },
        });
        b.Note("settings.touchpad.deadzone.note");
        if (TouchpadDeadZone.AapDisabled == true) b.Note("settings.touchpad.deadzone.aap");
    }

    private void BuildPerformance(SettingsBuilder b)
    {
        b.Header("settings.tab.perf", "settings.perf.sub");
        b.Group("settings.perf.modes");
        b.Row("settings.show.eco", "settings.show.eco.desc", b.Toggle(_cfg.EcoMode, on => _actions.SetModeVisibility(on, _cfg.FullSpeedMode)));
        b.Row("settings.show.full", "settings.show.full.desc", b.Toggle(_cfg.FullSpeedMode, on => _actions.SetModeVisibility(_cfg.EcoMode, on)));
        b.Group("settings.startmode");
        StartStrategy[] strategies = [StartStrategy.None, StartStrategy.Restore, StartStrategy.Pin, StartStrategy.Profiles];
        string[] names = ["settings.start.none", "settings.start.restore", "settings.start.pin", "settings.start.profiles"];
        int selectedStrategy = Math.Max(0, Array.IndexOf(strategies, _actions.GetStartStrategy()));
        b.Row("settings.startmode", names[selectedStrategy] + ".desc",
            b.Combo(names.Select(Loc.T), selectedStrategy, i =>
            { _actions.SetStartStrategy(strategies[i]); Rebuild(); }));
        if (_actions.GetStartStrategy() == StartStrategy.Profiles)
        {
            PerfMode?[] choices = [null, .. AppController.AllModes.Where(x => x != PerfMode.Eco || _cfg.EcoMode).Where(x => x != PerfMode.FullSpeed || _cfg.FullSpeedMode).Cast<PerfMode?>()];
            string[] labels = choices.Select(x => x is PerfMode mode ? Loc.T(ModeUi.Key(mode)!) : Loc.T("settings.profile.nochange")).ToArray();
            b.Row("settings.profile.ac", string.Empty, b.Combo(labels, Array.IndexOf(choices, _cfg.AcPerfMode), i => _actions.SetProfileMode(true, choices[i])));
            b.Row("settings.profile.battery", string.Empty, b.Combo(labels, Array.IndexOf(choices, _cfg.BatteryPerfMode), i => _actions.SetProfileMode(false, choices[i])));
        }
    }

    private void BuildKeys(SettingsBuilder b)
    {
        b.Header("settings.tab.keys", "settings.keys.sub");
        b.Group("settings.keys.mi");
        AddKey(b, "settings.key.mi.click", "settings.key.mi.click.desc", () => _cfg.MiClickAction, x => _cfg.MiClickAction = x,
            () => _cfg.MiClickCommand, x => _cfg.MiClickCommand = x);
        AddKey(b, "settings.key.mi.double", "settings.key.mi.double.desc", () => _cfg.MiDoubleAction, x => _cfg.MiDoubleAction = x,
            () => _cfg.MiDoubleCommand, x => _cfg.MiDoubleCommand = x);
        AddKey(b, "settings.key.mi.hold", "settings.key.mi.hold.desc", () => _cfg.MiHoldAction, x => _cfg.MiHoldAction = x,
            () => _cfg.MiHoldCommand, x => _cfg.MiHoldCommand = x);
        b.Group("settings.keys.other");
        AddKey(b, "settings.key.settings", "settings.key.settings.desc", () => _cfg.SettingsKeyAction, x => _cfg.SettingsKeyAction = x,
            () => _cfg.SettingsKeyCommand, x => _cfg.SettingsKeyCommand = x);
        AddKey(b, "settings.key.ai", "settings.key.ai.desc", () => _cfg.AiKeyAction, x => _cfg.AiKeyAction = x,
            () => _cfg.AiKeyCommand, x => _cfg.AiKeyCommand = x);
        AddKey(b, "settings.key.proj", "settings.key.proj.desc", () => _cfg.ProjKeyAction, x => _cfg.ProjKeyAction = x,
            () => _cfg.ProjKeyCommand, x => _cfg.ProjKeyCommand = x);
    }

    private void AddKey(SettingsBuilder b, string title, string desc, Func<string?> getAction, Action<string> setAction,
        Func<string?> getCommand, Action<string?> setCommand)
    {
        string[] actions = ["modes", "charge", "panel", "owl", "monitor", "travel", "touchpad", "touchscreen",
            "projection", "settings", "copilot", "play", "next", "prev", "stop", "calc", "launch", "none"];
        string current = getAction() ?? "none";
        b.Row(title, desc, b.Combo(actions.Select(x => Loc.T("settings.act." + x)), Math.Max(0, Array.IndexOf(actions, current)), i =>
        {
            setAction(actions[i]); _cfg.Save(); Rebuild();
        }));
        if (current == "launch")
            b.Row("settings.key.command", string.Empty, b.Text(getCommand() ?? string.Empty, value =>
            { setCommand(string.IsNullOrWhiteSpace(value) ? null : value.Trim()); _cfg.Save(); }));
    }

    private void BuildApi(SettingsBuilder b)
    {
        ApiSettings api = _actions.GetApiSettings();
        b.Header("settings.tab.api", "settings.api.sub");
        b.Row("settings.api.enable", "settings.api.enable.desc", b.Toggle(api.Enabled, on =>
        { api.Enabled = on; _actions.ApiApplied(); Rebuild(); }));
        b.Row("settings.api.port", "settings.api.port.desc", b.Number(api.Port, 1024, 65535, value =>
        { api.Port = value; _actions.ApiApplied(); }, api.Enabled));
        b.Row("settings.api.lan", "settings.api.lan.desc", b.Toggle(api.LanAccess, on =>
        { api.LanAccess = on; _actions.ApiApplied(); }, api.Enabled));
        var token = new TextBox { IsReadOnly = true, MinWidth = 280, PlaceholderText = "••••••••" };
        b.Row("settings.api.token", "settings.api.token.desc", new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { token, b.Button("settings.api.token.generate", () =>
            {
                string plain = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                api.TokenSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plain))).ToLowerInvariant();
                token.Text = plain;
                token.SelectAll();
                _actions.ApiApplied();
            }, api.Enabled) },
        });
        b.Group("settings.api.cmds");
        b.Row("settings.api.cmd.mode", "settings.api.cmd.mode.desc", b.Toggle(api.AllowMode, x => { api.AllowMode = x; _actions.ApiApplied(); }, api.Enabled));
        b.Row("settings.api.cmd.care", "settings.api.cmd.care.desc", b.Toggle(api.AllowCare, x => { api.AllowCare = x; _actions.ApiApplied(); }, api.Enabled));
        b.Row("settings.api.cmd.travel", "settings.api.cmd.travel.desc", b.Toggle(api.AllowTravel, x => { api.AllowTravel = x; _actions.ApiApplied(); }, api.Enabled));
        b.Row("settings.api.cmd.owl", "settings.api.cmd.owl.desc", b.Toggle(api.AllowOwl, x => { api.AllowOwl = x; _actions.ApiApplied(); }, api.Enabled && _cfg.OwlMode));
    }

    private void BuildAbout(SettingsBuilder b)
    {
        b.Header("settings.tab.about", "settings.about.sub");
        b.Value("settings.version", AppVersion());
        ReleaseInfo? update = _actions.GetUpdate();
        UpdateStatus status = _actions.GetUpdateStatus();
        if (status == UpdateStatus.Available && update is not null)
            b.Row(Loc.T("settings.updates.available", update.Tag), string.Empty,
                b.Button("settings.updates.open", () => Open(update.Url)));
        else
        {
            b.Row("settings.updates.now", string.Empty,
                b.Button("settings.updates.now", () => _actions.CheckUpdatesNow(Rebuild)));
            string? state = status switch
            {
                UpdateStatus.DevBuild when update is not null => Loc.T("settings.updates.dev", update.Tag),
                UpdateStatus.UpToDate => Loc.T("settings.updates.uptodate"),
                UpdateStatus.Failed => Loc.T("settings.updates.failed"),
                _ => null,
            };
            if (state is not null) b.NoteText(state);
        }
        b.Group("settings.tab.about");
        var info = SystemInfo.Current;
        if (info.ModelLine is { } model) b.Value("settings.about.model", model);
        if (info.BiosLine is { } bios) b.Value("settings.about.bios", bios);
        if (info.SerialMasked is { } masked)
        {
            var serialText = new TextBlock { Text = masked, VerticalAlignment = VerticalAlignment.Center };
            var serialRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { serialText },
            };
            if (info.Serial is { } serial)
                serialRow.Children.Add(b.Button("settings.about.serial.show", () => serialText.Text = serial));
            b.Row("settings.about.serial", string.Empty, serialRow);
        }
        b.Value("settings.about.iface", "MiCommonInterface (MIFS)");
        b.Value("settings.about.config", Pretty(Path.Combine(AppPaths.DataDir, "config.json")));
        b.Value("settings.about.log", Pretty(Path.Combine(AppPaths.DataDir, "log.txt")));
        if (AppPaths.Portable) b.Value("settings.about.portable", Loc.T("settings.about.portable.on"));
        b.Note("settings.about.note");
        b.Row("XiControl", string.Empty, new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                b.Button("GitHub", () => Open("https://github.com/Oksion/XiControl"), rawText: true),
                b.Button("settings.about.forum", () => Open("https://4pda.to/forum/index.php?showtopic=1122287")),
                b.Button("settings.about.license", () => Open("https://github.com/Oksion/XiControl/blob/main/LICENSE")),
                b.Button("settings.about.updates", () => Open("https://github.com/Oksion/XiControl/releases")),
            },
        });
        b.Row("settings.about.support", "settings.about.support.desc",
            b.Button("settings.about.support", () => Open("https://buymeacoffee.com/3CLiAI1")));
    }

    internal static string CareHintKey(int percent) => percent switch
    {
        <= 50 => "settings.battery.care.hint.low",
        <= 70 => "settings.battery.care.hint.mid",
        _ => "settings.battery.care.hint.high",
    };

    private static int IndexOf(IEnumerable<string> values, string target) =>
        Math.Max(0, values.Select((value, index) => (value, index))
            .FirstOrDefault(x => x.Item1.Equals(target, StringComparison.OrdinalIgnoreCase), (string.Empty, -1)).Item2);

    private static int Nearest(int[] values, int target) =>
        Array.IndexOf(values, values.MinBy(x => Math.Abs(x - target)));

    private static int[] ValuesWithCurrent(int[] presets, params int[] current) =>
        [.. presets.Concat(current).Distinct().OrderByDescending(x => x)];

    private async Task BrowseTravelSoundAsync(TextBox target)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary,
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
            };
            picker.FileTypeFilter.Add(".wav");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Handle);
            Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null) return;
            target.Text = file.Path;
            _cfg.TravelSoundFile = file.Path;
            _cfg.Save();
        }
        catch (Exception ex) { Log.Ex("Settings.BrowseSound", ex); }
    }

    private static string AppVersion()
    {
        try { return FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).ProductVersion?.Split('+')[0] ?? "—"; }
        catch { return "—"; }
    }

    private static string Pretty(string path)
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return path.StartsWith(appData, StringComparison.OrdinalIgnoreCase)
            ? "%APPDATA%" + path[appData.Length..]
            : path;
    }

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Ex("OpenUrl", ex); }
    }
}

internal sealed class SettingsBuilder
{
    public StackPanel Root { get; } = new()
    {
        Spacing = 6,
        Padding = new Thickness(34, 28, 38, 40),
        MaxWidth = 760,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    public void Header(string titleKey, string subtitleKey)
    {
        Root.Children.Add(new TextBlock
        {
            Text = Loc.T(titleKey),
            FontSize = 28,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        Root.Children.Add(new TextBlock
        {
            Text = Loc.T(subtitleKey),
            FontSize = 14,
            Opacity = 0.62,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 18),
        });
    }

    public void Group(string key) => Root.Children.Add(new TextBlock
    {
        Text = Loc.T(key),
        FontSize = 13,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Opacity = 0.7,
        Margin = new Thickness(2, 18, 0, 5),
    });

    public void Row(string titleKey, string descriptionKey, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 18 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var copy = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(new TextBlock
        {
            Text = titleKey.StartsWith("settings.", StringComparison.Ordinal) ? Loc.T(titleKey) : titleKey,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(descriptionKey)) copy.Children.Add(new TextBlock
        {
            Text = descriptionKey.StartsWith("settings.", StringComparison.Ordinal) ? Loc.T(descriptionKey) : descriptionKey,
            FontSize = 12,
            Opacity = 0.62,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 430,
        });
        grid.Children.Add(copy);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        Root.Children.Add(new Border
        {
            Padding = new Thickness(18, 14, 18, 14),
            CornerRadius = new CornerRadius(WinUiRadii.Overlay),
            Background = ResourceBrush("CardBackgroundFillColorDefaultBrush", Windows.UI.Color.FromArgb(25, 128, 128, 128)),
            BorderBrush = ResourceBrush("CardStrokeColorDefaultBrush", Windows.UI.Color.FromArgb(25, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            Child = grid,
        });
    }

    public void Note(string key) => NoteText(Loc.T(key));

    public void NoteText(string text) => Root.Children.Add(new Border
    {
        Padding = new Thickness(14, 11, 14, 11),
        CornerRadius = new CornerRadius(WinUiRadii.Overlay),
        Background = ResourceBrush("SubtleFillColorSecondaryBrush", Windows.UI.Color.FromArgb(18, 128, 128, 128)),
        Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.72 },
    });

    public void Value(string key, string value, string descriptionKey = "") => Row(key, descriptionKey, new TextBlock
    {
        Text = value,
        FontSize = 14,
        TextAlignment = TextAlignment.Right,
        MaxWidth = 330,
        TextWrapping = TextWrapping.Wrap,
    });

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Фабрики сохраняют единый instance API построителя страниц.")]
    public ToggleSwitch Toggle(bool value, Action<bool> changed, bool enabled = true)
    {
        var toggle = new ToggleSwitch { IsOn = value, IsEnabled = enabled };
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        return toggle;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Фабрики сохраняют единый instance API построителя страниц.")]
    public ComboBox Combo(IEnumerable<string> values, int selected, Action<int> changed, bool enabled = true)
    {
        var combo = new ComboBox { MinWidth = 170, IsEnabled = enabled };
        foreach (string value in values) combo.Items.Add(value);
        if (combo.Items.Count > 0) combo.SelectedIndex = Math.Clamp(selected, 0, combo.Items.Count - 1);
        combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) changed(combo.SelectedIndex); };
        return combo;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Фабрики сохраняют единый instance API построителя страниц.")]
    public TextBox Text(string value, Action<string> changed)
    {
        var text = new TextBox { Text = value, MinWidth = 260 };
        text.LostFocus += (_, _) => changed(text.Text);
        return text;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Фабрики сохраняют единый instance API построителя страниц.")]
    public NumberBox Number(int value, int min, int max, Action<int> changed, bool enabled = true)
    {
        var number = new NumberBox
        {
            Value = value,
            Minimum = min,
            Maximum = max,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            IsEnabled = enabled,
            Width = 130,
        };
        number.ValueChanged += (_, e) =>
        {
            if (!double.IsNaN(e.NewValue)) changed((int)Math.Round(e.NewValue));
        };
        return number;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Фабрики сохраняют единый instance API построителя страниц.")]
    public Button Button(string key, Action action, bool enabled = true, bool rawText = false)
    {
        var button = new Button { Content = rawText ? key : Loc.T(key), IsEnabled = enabled };
        button.Click += (_, _) => action();
        return button;
    }

    private static Microsoft.UI.Xaml.Media.Brush ResourceBrush(string key, Windows.UI.Color fallback) =>
        Application.Current.Resources.TryGetValue(key, out object? value) && value is Microsoft.UI.Xaml.Media.Brush brush
            ? brush
            : new SolidColorBrush(fallback);
}
