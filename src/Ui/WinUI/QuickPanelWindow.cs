using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using XiControl.Config;
using XiControl.Localization;
using XiControl.SystemIntegration;
using XiControl.Wmi;
using WinUIImage = Microsoft.UI.Xaml.Controls.Image;

namespace XiControl.Ui;

internal sealed class QuickPanelWindow : FlyoutWindow
{
    private const int PanelWidthDips = 640;
    private const int PanelHeightDips = 270;

    private readonly IMifsClient _mifs;
    private readonly AppConfig _cfg;
    private readonly AppController _controller;
    private readonly TouchpadControl _touchpad;
    private readonly TouchscreenControl _touchscreen;
    private readonly StackPanel _root;
    private readonly Border _card;
    private readonly Dictionary<PerfMode, ToggleButton> _modeButtons = new();
    private readonly Dictionary<PerfMode, WinUiPerformanceIcon> _modeIcons = new();

    private ToggleButton? _travelButton;
    private ToggleButton? _careButton;
    private ToggleButton? _fullButton;
    private ToggleButton? _touchpadButton;
    private ToggleButton? _refreshButton;
    private ToggleButton? _owlButton;
    private ToggleButton? _touchscreenButton;
    private TextBlock? _careText;
    private WinUIImage? _travelIcon;
    private WinUIImage? _touchpadIcon;
    private WinUIImage? _refreshIcon;
    private WinUIImage? _owlIcon;
    private WinUIImage? _touchscreenIcon;

    public QuickPanelWindow(IMifsClient mifs, AppConfig cfg, AppController controller,
        TouchpadControl touchpad, TouchscreenControl touchscreen)
        : base(cornerRadiusDips: WinUiRadii.Overlay)
    {
        _mifs = mifs;
        _cfg = cfg;
        _controller = controller;
        _touchpad = touchpad;
        _touchscreen = touchscreen;

        Title = Loc.T("panel.title");
        _root = new StackPanel { Spacing = 8 };
        _card = new Border
        {
            Padding = new Thickness(22, 16, 22, 18),
            CornerRadius = new CornerRadius(WinUiRadii.Overlay),
            BorderThickness = new Thickness(0),
            Child = _root,
        };
        Content = _card;
        ApplyTheme();
    }

    public Action? SettingsRequested { get; set; }
    public Action? MonitorRequested { get; set; }

    public void Toggle()
    {
        if (IsVisible) { Hide(); return; }
        Popup();
    }

    public void Popup()
    {
        Build();
        Rectangle work = ScreenMetrics.WorkingAreaAtCursor();
        Size physical = PhysicalSizeForDips(work, PanelWidthDips, PanelHeightDips);
        int width = Math.Min(physical.Width, Math.Max(1, work.Width - 32));
        int height = Math.Min(physical.Height, Math.Max(1, work.Height - 32));
        Point location = QuickPanelPlacement.ForWorkArea(work, new Size(width, height));
        ShowAt(location.X, location.Y, width, height);
    }

    public void RefreshUi()
    {
        if (!IsVisible) return;
        ApplyTheme();
        try { _controller.SyncCareFromFirmware(); }
        catch (Exception ex) { Log.Ex("QuickPanel.SyncCare", ex); }
        UpdateState();
    }

    /// <summary>
    /// Theme colors are captured in button resources and rendered SVG sources when the panel is
    /// built. Recreate the visible tree so no control keeps brushes from the previous theme.
    /// </summary>
    public void ThemeChanged()
    {
        if (IsVisible) Build();
        else ApplyTheme();
    }

    public void ReloadModes()
    {
        if (IsVisible) Build();
    }

    private void Build()
    {
        ApplyTheme();
        _root.Children.Clear();
        ResetControlReferences();
        try { _controller.SyncCareFromFirmware(); }
        catch (Exception ex) { Log.Ex("QuickPanel.SyncCare", ex); }

        _root.Children.Add(Header());
        _root.Children.Add(ModeGrid());
        _root.Children.Add(BottomLabels());
        _root.Children.Add(BottomActions());
        UpdateState();
    }

    private Grid Header()
    {
        var header = new Grid { Height = 34, Margin = new Thickness(2, 0, 0, 0), ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = Loc.T("panel.title"),
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        actions.Children.Add(IconButton(WinUiSvgIcon.Create(SvgIcons.MenuSettings, 18, 64, UiIconColor()),
            Loc.T("settings.title"), () =>
            {
                Hide();
                SettingsRequested?.Invoke();
            }));
        actions.Children.Add(IconButton(WinUiSvgIcon.Create(SvgIcons.MenuMonitor, 18, 64, UiIconColor()),
            Loc.T("monitor.title"), () =>
            {
                Hide();
                MonitorRequested?.Invoke();
            }));
        actions.Children.Add(IconButton(new FontIcon { Glyph = "\uE711", FontSize = 14 },
            Loc.T("panel.close"), Hide));
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        return header;
    }

    private Grid ModeGrid()
    {
        var modes = new Grid { ColumnSpacing = 8 };
        for (int i = 0; i < _controller.VisibleModes.Count; i++)
            modes.ColumnDefinitions.Add(new ColumnDefinition());

        for (int i = 0; i < _controller.VisibleModes.Count; i++)
        {
            PerfMode mode = _controller.VisibleModes[i];
            var animatedIcon = new WinUiPerformanceIcon(mode, 36);
            var button = new ToggleButton
            {
                Content = new StackPanel
                {
                    Spacing = 7,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        animatedIcon,
                        new TextBlock
                        {
                            Text = Loc.T(ModeUi.Key(mode)!),
                            FontSize = 13,
                            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                            TextAlignment = TextAlignment.Center,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                    },
                },
                Height = 110,
                Padding = new Thickness(8, 11, 8, 9),
                CornerRadius = new CornerRadius(WinUiRadii.Overlay),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                AllowFocusOnInteraction = false,
            };
            ApplySelectedStyle(button);
            ToolTipService.SetToolTip(button, Loc.T(ModeUi.Key(mode)!));
            button.Click += (_, _) =>
            {
                SetModeSelection(mode);
                _controller.SetMode(mode);
            };
            button.PointerEntered += (_, _) => animatedIcon.SetPointerOver(true);
            button.PointerExited += (_, _) => animatedIcon.SetPointerOver(false);
            _modeButtons[mode] = button;
            _modeIcons[mode] = animatedIcon;
            Grid.SetColumn(button, i);
            modes.Children.Add(button);
        }
        return modes;
    }

    private static Grid BottomLabels()
    {
        // The lower controls previously crowded the performance cards while leaving noticeably
        // more empty space below them. Six DIPs restores an even visual rhythm at every DPI.
        var labels = new Grid { Height = 18, Margin = new Thickness(2, 6, 2, -2) };
        labels.ColumnDefinitions.Add(new ColumnDefinition());
        labels.ColumnDefinitions.Add(new ColumnDefinition());
        labels.Children.Add(new TextBlock
        {
            Text = Loc.T("panel.charge"),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            Opacity = 0.62,
        });
        var right = new TextBlock
        {
            Text = Loc.T("panel.awake"),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            Opacity = 0.62,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(right, 1);
        labels.Children.Add(right);
        return labels;
    }

    private Grid BottomActions()
    {
        var actions = new Grid { ColumnSpacing = 7, Height = 36 };
        int column = 0;

        void Add(FrameworkElement element, GridLength width)
        {
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            Grid.SetColumn(element, column++);
            actions.Children.Add(element);
        }

        _travelButton = IconToggle(Loc.T("panel.travel"),
            () => _controller.SetTravel(!_cfg.TravelMode),
            SvgIcons.Travel, out _travelIcon);
        Add(_travelButton, Pixels(36));

        _careButton = TextToggle($"{_cfg.CarePercent()}%", () => _controller.ToggleCare(true), out _careText);
        Add(_careButton, Star());
        _fullButton = TextToggle("100%", () => _controller.ToggleCare(false), out _);
        Add(_fullButton, Star());

        if (_cfg.TouchpadFeature && Safe(() => _touchpad.Available, false))
        {
            _touchpadButton = IconToggle(Loc.T("panel.touchpad"), _controller.ToggleTouchpad,
                SvgIcons.Touchpad, out _touchpadIcon);
            Add(_touchpadButton, Pixels(36));
        }
        if (_cfg.RefreshRateFeature)
        {
            _refreshButton = IconToggle(Loc.T("panel.hz"),
                () => _controller.ToggleAutoHz(!_cfg.AutoRefreshRate), SvgIcons.RefreshRate, out _refreshIcon);
            Add(_refreshButton, Pixels(36));
        }
        if (_cfg.OwlMode)
        {
            _owlButton = IconToggle(Loc.T("panel.awake"), _controller.ToggleAwake,
                SvgIcons.OwlAwake, out _owlIcon);
            Add(_owlButton, Pixels(36));
        }
        if (_cfg.TouchscreenFeature && Safe(() => _touchscreen.Available, false))
        {
            _touchscreenButton = IconToggle(Loc.T("panel.touchscreen"), _controller.ToggleTouchscreen,
                SvgIcons.Touchscreen, out _touchscreenIcon);
            Add(_touchscreenButton, Pixels(36));
        }

        return actions;
    }

    private void UpdateState()
    {
        PerfMode? current = Safe(() => _mifs.GetPerfMode(), (PerfMode?)null);
        if (current is PerfMode selected) SetModeSelection(selected);
        else ClearModeSelection();

        bool travel = _cfg.TravelMode;
        bool care = _cfg.ChargeCare;
        bool touchpad = Safe(() => _touchpad.IsEnabled(), null) == true;
        bool touchscreen = Safe(() => _touchscreen.IsEnabled(), null) == true;

        SetChecked(_travelButton, travel);
        SetChecked(_careButton, care);
        SetChecked(_fullButton, !care && !travel);
        SetChecked(_touchpadButton, touchpad);
        SetChecked(_refreshButton, _cfg.AutoRefreshRate);
        SetChecked(_owlButton, _cfg.Awake);
        SetChecked(_touchscreenButton, touchscreen);

        if (_careText is not null) _careText.Text = $"{_cfg.CarePercent()}%";
        SetIcon(_travelIcon, travel ? SvgIcons.Travel : SvgIcons.TravelOff);
        SetIcon(_touchpadIcon, touchpad ? SvgIcons.Touchpad : SvgIcons.TouchpadOff);
        SetIcon(_refreshIcon, _cfg.AutoRefreshRate ? SvgIcons.RefreshRate : SvgIcons.RefreshRateOff);
        SetIcon(_owlIcon, _cfg.Awake ? SvgIcons.OwlAwake : SvgIcons.OwlAsleep);
        SetIcon(_touchscreenIcon, touchscreen ? SvgIcons.Touchscreen : SvgIcons.TouchscreenOff);
    }

    private void SetModeSelection(PerfMode selected)
    {
        foreach ((PerfMode mode, ToggleButton button) in _modeButtons)
        {
            SetChecked(button, mode == selected);
            if (_modeIcons.TryGetValue(mode, out WinUiPerformanceIcon? icon))
                icon.SetSelected(mode == selected);
        }
    }

    private void ClearModeSelection()
    {
        foreach ((PerfMode mode, ToggleButton button) in _modeButtons)
        {
            SetChecked(button, false);
            if (_modeIcons.TryGetValue(mode, out WinUiPerformanceIcon? icon))
                icon.SetSelected(false);
        }
    }

    private void ApplyTheme()
    {
        _card.RequestedTheme = FlyoutPalette.RequestedTheme;
        _card.Background = new SolidColorBrush(FlyoutPalette.Card);
    }

    private static Button IconButton(FrameworkElement icon, string tip, Action action)
    {
        var button = new Button
        {
            Content = icon,
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(WinUiRadii.Control),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            AllowFocusOnInteraction = false,
        };
        ToolTipService.SetToolTip(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    private static ToggleButton TextToggle(string text, Action action, out TextBlock label)
    {
        label = new TextBlock
        {
            Text = text,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        var button = new ToggleButton
        {
            Content = label,
            Height = 36,
            Padding = new Thickness(12, 4, 12, 4),
            CornerRadius = new CornerRadius(WinUiRadii.Control),
            BorderThickness = new Thickness(0.75),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            AllowFocusOnInteraction = false,
        };
        ApplySelectedStyle(button);
        ToolTipService.SetToolTip(button, text);
        button.Click += (_, _) => action();
        return button;
    }

    private static ToggleButton IconToggle(string tip, Action action, string iconName, out WinUIImage icon)
    {
        icon = WinUiSvgIcon.Create(iconName, 36);
        var button = new ToggleButton
        {
            Content = icon,
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(WinUiRadii.Control),
            BorderThickness = new Thickness(0.75),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            AllowFocusOnInteraction = false,
        };
        ApplySelectedStyle(button);
        ToolTipService.SetToolTip(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    private static void ApplySelectedStyle(ToggleButton button)
    {
        bool dark = FlyoutPalette.Dark;
        var background = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(255, 34, 82, 126)
            : Windows.UI.Color.FromArgb(255, 216, 233, 249));
        var hover = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(255, 40, 94, 143)
            : Windows.UI.Color.FromArgb(255, 203, 225, 246));
        var pressed = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(255, 28, 70, 108)
            : Windows.UI.Color.FromArgb(255, 190, 217, 243));
        var border = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(170, 96, 205, 255)
            : Windows.UI.Color.FromArgb(150, 0, 95, 184));
        var foreground = new SolidColorBrush(dark
            ? Microsoft.UI.Colors.White
            : Windows.UI.Color.FromArgb(255, 20, 45, 68));
        button.Resources["ToggleButtonBackgroundChecked"] = background;
        button.Resources["ToggleButtonBackgroundCheckedPointerOver"] = hover;
        button.Resources["ToggleButtonBackgroundCheckedPressed"] = pressed;
        button.Resources["ToggleButtonBorderBrushChecked"] = border;
        button.Resources["ToggleButtonBorderBrushCheckedPointerOver"] = border;
        button.Resources["ToggleButtonBorderBrushCheckedPressed"] = border;
        button.Resources["ToggleButtonForegroundChecked"] = foreground;
        button.Resources["ToggleButtonForegroundCheckedPointerOver"] = foreground;
        button.Resources["ToggleButtonForegroundCheckedPressed"] = foreground;
    }

    private static Color UiIconColor() => FlyoutPalette.Dark
        ? Color.FromArgb(232, 240, 244, 248)
        : Color.FromArgb(230, 35, 42, 50);

    private static void SetChecked(ToggleButton? button, bool value)
    {
        if (button is null) return;
        if (button.IsChecked != value) button.IsChecked = value;
        // The stock ToggleButton template cross-fades checked backgrounds. When changing mode,
        // that makes the previous card look selected for another frame and reads as a flash.
        // Keep the state switch immediate; motion belongs to the XiControl artwork itself.
        _ = VisualStateManager.GoToState(button, value ? "Checked" : "Unchecked", useTransitions: false);
    }

    private static void SetIcon(WinUIImage? image, string name)
    {
        if (image is null) return;
        ImageSource source = WinUiSvgIcon.Source(name);
        if (!ReferenceEquals(image.Source, source)) image.Source = source;
    }

    private void ResetControlReferences()
    {
        _modeButtons.Clear();
        _modeIcons.Clear();
        _travelButton = _careButton = _fullButton = null;
        _touchpadButton = _refreshButton = _owlButton = _touchscreenButton = null;
        _careText = null;
        _travelIcon = _touchpadIcon = _refreshIcon = _owlIcon = _touchscreenIcon = null;
    }

    private static GridLength Pixels(double value) => new(value, GridUnitType.Pixel);
    private static GridLength Star() => new(1, GridUnitType.Star);

    private static T Safe<T>(Func<T> operation, T fallback)
    {
        try { return operation(); }
        catch (Exception ex) { Log.Ex("QuickPanel", ex); return fallback; }
    }
}

internal static class QuickPanelPlacement
{
    internal static Point ForWorkArea(Rectangle work, Size panel)
    {
        int x = work.Left + (work.Width - panel.Width) / 2;
        int targetBottom = work.Top + (int)Math.Round(work.Height * 0.80);
        int y = Math.Clamp(targetBottom - panel.Height, work.Top + 16,
            Math.Max(work.Top + 16, work.Bottom - panel.Height - 16));
        return new Point(x, y);
    }
}
