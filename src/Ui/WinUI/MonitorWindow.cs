using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using XiControl.Config;
using XiControl.Localization;
using XiControl.SystemIntegration;

namespace XiControl.Ui;

/// <summary>
/// WinUI 3 system monitor with a detailed chart view, a compact four-metric strip, and a
/// power-only widget. Sampling stops while hidden and every view reuses the same live history.
/// </summary>
internal sealed class MonitorWindow : FlyoutWindow
{
    private const int HistoryCapacity = 180;
    private static readonly TrayMetric[] DetailedMetrics =
        [TrayMetric.Power, TrayMetric.Cpu, TrayMetric.Gpu, TrayMetric.Ram, TrayMetric.Temp];
    private static readonly TrayMetric[] CompactMetrics =
        [TrayMetric.Power, TrayMetric.Cpu, TrayMetric.Gpu, TrayMetric.Ram];

    private readonly AppConfig _cfg;
    private readonly DispatcherQueue _queue;
    private readonly Grid _root = new();
    private readonly Border _card;
    private readonly Dictionary<TrayMetric, TrayMetricSource> _sources = new();
    private readonly Dictionary<TrayMetric, List<float>> _history = new();
    private readonly Dictionary<TrayMetric, TrayMetricReading> _lastReading = new();
    private readonly Dictionary<TrayMetric, TextBlock> _valueViews = new();
    private readonly Dictionary<TrayMetric, TextBlock> _detailViews = new();
    private readonly Dictionary<TrayMetric, GraphVisual> _graphs = new();
    private readonly object _samplingLock = new();
    private CancellationTokenSource? _sampling;
    private int _samplingVersion;
    private MonitorViewKind _view;

    private sealed record GraphVisual(
        Canvas Canvas,
        IReadOnlyList<Line> Guides,
        Polygon Area,
        Polyline Line,
        TextBlock? Scale);

    public MonitorWindow(AppConfig cfg)
        : base()
    {
        _cfg = cfg;
        _queue = DispatcherQueue;
        _view = MonitorLayout.Parse(cfg.MonitorView);

        foreach (TrayMetric kind in DetailedMetrics) _history[kind] = [];

        Title = Loc.T("monitor.title");
        _card = new Border
        {
            CornerRadius = new CornerRadius(WinUiRadii.Overlay),
            BorderThickness = new Thickness(0),
            Child = _root,
        };
        Content = _card;
        ExtendsContentIntoTitleBar = true;
        ApplyTheme();
        BuildView();
    }

    public void Toggle()
    {
        if (IsVisible) { Hide(); return; }
        Popup();
    }

    public void Popup()
    {
        ApplyTheme();
        ResetSamplingState();
        BuildView();
        Size logical = MonitorLayout.ViewSize(_view);
        System.Drawing.Rectangle work = ScreenMetrics.WorkingAreaAtCursor();
        Size physical = PhysicalSizeForDips(work, logical.Width, logical.Height);
        int width = Math.Min(physical.Width, work.Width);
        int height = Math.Min(physical.Height, work.Height);
        int x = _cfg.MonitorX is int savedX ? savedX : work.Left + (work.Width - width) / 2;
        int y = _cfg.MonitorY is int savedY ? savedY : work.Top + (work.Height - height) / 2;
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));
        ShowAt(x, y, width, height);
        StartSampling();
    }

    public void ThemeChanged()
    {
        ApplyTheme();
        if (IsVisible) BuildView();
    }

    protected override void OnHidden()
    {
        Interlocked.Increment(ref _samplingVersion);
        _sampling?.Cancel();
        _sampling?.Dispose();
        _sampling = null;
        _cfg.MonitorX = NativeAppWindow.Position.X;
        _cfg.MonitorY = NativeAppWindow.Position.Y;
        _cfg.Save();
    }

    public override void Dispose()
    {
        Interlocked.Increment(ref _samplingVersion);
        _sampling?.Cancel();
        _sampling?.Dispose();
        lock (_samplingLock)
        {
            foreach (TrayMetricSource source in _sources.Values) source.Dispose();
            _sources.Clear();
        }
        base.Dispose();
    }

    private void SetView(MonitorViewKind view)
    {
        _view = view;
        _cfg.MonitorView = MonitorLayout.ConfigValue(view);
        _cfg.Save();
        BuildView();
        if (!IsVisible) return;

        Size logical = MonitorLayout.ViewSize(view);
        System.Drawing.Rectangle work = ScreenMetrics.WorkingAreaForWindow(Handle);
        Size physical = PhysicalSizeForDips(work, logical.Width, logical.Height);
        int width = Math.Min(physical.Width, work.Width);
        int height = Math.Min(physical.Height, work.Height);
        Point location = MonitorPlacement.Center(work, new Size(width, height));
        MoveAndResize(location.X, location.Y, width, height);
    }

    private void BuildView()
    {
        _root.Children.Clear();
        _root.RowDefinitions.Clear();
        _root.ColumnDefinitions.Clear();
        _valueViews.Clear();
        _detailViews.Clear();
        _graphs.Clear();

        switch (_view)
        {
            case MonitorViewKind.Mini:
            {
                Grid strip = BuildMiniView();
                _root.Children.Add(strip);
                SetTitleBar(strip);
                break;
            }
            case MonitorViewKind.Power:
            {
                Grid widget = BuildPowerView(out FrameworkElement dragRegion);
                _root.Children.Add(widget);
                SetTitleBar(dragRegion);
                break;
            }
            default:
            {
                _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                _root.RowDefinitions.Add(new RowDefinition());
                Grid header = BuildFullHeader();
                _root.Children.Add(header);
                FrameworkElement body = BuildFullView();
                Grid.SetRow(body, 1);
                _root.Children.Add(body);
                SetTitleBar(header);
                break;
            }
        }
        RefreshVisibleValues();
    }

    private Grid BuildFullHeader()
    {
        var header = new Grid
        {
            Height = 52,
            Padding = new Thickness(18, 8, 10, 4),
            ColumnSpacing = 4,
        };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = Loc.T("monitor.title"),
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });

        Button compact = HeaderButton(WindowChrome.RestoreGlyph, Loc.T("monitor.view"));
        compact.Click += (_, _) => SetView(MonitorViewKind.Mini);
        Grid.SetColumn(compact, 1);
        header.Children.Add(compact);

        Button close = HeaderButton(WindowChrome.CloseGlyph, Loc.T("panel.close"));
        close.Click += (_, _) => Hide();
        Grid.SetColumn(close, 2);
        header.Children.Add(close);
        return header;
    }

    private StackPanel BuildFullView()
    {
        var stats = new StackPanel { Spacing = 8, Padding = new Thickness(16, 3, 16, 14) };
        foreach (TrayMetric kind in DetailedMetrics) stats.Children.Add(GraphRow(kind));
        return stats;
    }

    private Grid GraphRow(TrayMetric kind)
    {
        var row = new Grid { Height = 98, ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        row.ColumnDefinitions.Add(new ColumnDefinition());

        var summary = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(4, 7, 0, 4),
            VerticalAlignment = VerticalAlignment.Top,
        };
        summary.Children.Add(new TextBlock
        {
            Text = MetricLabel(kind),
            FontSize = 12.5,
            Opacity = 0.70,
        });
        TextBlock value = ValueText(20, TextAlignment.Left);
        summary.Children.Add(value);
        _valueViews[kind] = value;

        if (kind is TrayMetric.Gpu or TrayMetric.Ram)
        {
            var detail = new TextBlock
            {
                Text = "—",
                FontSize = 11.5,
                Opacity = 0.72,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            summary.Children.Add(detail);
            _detailViews[kind] = detail;
        }
        row.Children.Add(summary);

        GraphVisual graph = CreateGraph(kind);
        var plot = new Grid();
        plot.Children.Add(graph.Canvas);
        if (graph.Scale is not null) plot.Children.Add(graph.Scale);
        var graphCard = new Border
        {
            CornerRadius = new CornerRadius(WinUiRadii.Overlay),
            Background = GraphBackgroundBrush(),
            Child = plot,
        };
        Grid.SetColumn(graphCard, 1);
        row.Children.Add(graphCard);
        _graphs[kind] = graph;
        return row;
    }

    private GraphVisual CreateGraph(TrayMetric kind)
    {
        var canvas = new Canvas { Height = 98, MinWidth = 220 };
        var guides = new List<Line>();
        for (int i = 0; i < 9; i++)
        {
            Line guide = GuideLine();
            guides.Add(guide);
            canvas.Children.Add(guide);
        }

        Windows.UI.Color color = MetricColor(kind, float.NaN);
        var area = new Polygon
        {
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(48, color.R, color.G, color.B)),
        };
        var line = new Polyline
        {
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
        };
        canvas.Children.Add(area);
        canvas.Children.Add(line);

        TextBlock? scale = kind is TrayMetric.Power or TrayMetric.Temp
            ? new TextBlock
            {
                FontSize = 11,
                Opacity = 0.70,
                Margin = new Thickness(0, 5, 9, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
            }
            : null;

        var visual = new GraphVisual(canvas, guides, area, line, scale);
        canvas.SizeChanged += (_, _) => UpdateGraph(kind, visual);
        return visual;
    }

    private Grid BuildMiniView()
    {
        var strip = new Grid
        {
            Padding = new Thickness(18, 6, 9, 7),
            ColumnSpacing = 10,
        };
        foreach (TrayMetric _ in CompactMetrics)
            strip.ColumnDefinitions.Add(new ColumnDefinition());
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        for (int i = 0; i < CompactMetrics.Length; i++)
        {
            TrayMetric kind = CompactMetrics[i];
            var cell = new StackPanel
            {
                Spacing = 2,
                MinWidth = 70,
                VerticalAlignment = VerticalAlignment.Center,
            };
            cell.Children.Add(new TextBlock
            {
                Text = MetricLabel(kind),
                FontSize = 11.5,
                Opacity = 0.70,
            });
            TextBlock value = ValueText(18, TextAlignment.Left);
            cell.Children.Add(value);
            _valueViews[kind] = value;
            Grid.SetColumn(cell, i);
            strip.Children.Add(cell);
        }

        Button full = CompactButton("\uE740", Loc.T("monitor.view"));
        full.Click += (_, _) => SetView(MonitorViewKind.Full);
        Grid.SetColumn(full, 4);
        strip.Children.Add(full);

        Button power = CompactButton(WindowChrome.RestoreGlyph, Loc.T("monitor.power"));
        power.Click += (_, _) => SetView(MonitorViewKind.Power);
        Grid.SetColumn(power, 5);
        strip.Children.Add(power);

        Button close = CompactButton(WindowChrome.CloseGlyph, Loc.T("panel.close"));
        close.Click += (_, _) => Hide();
        Grid.SetColumn(close, 6);
        strip.Children.Add(close);
        return strip;
    }

    private Grid BuildPowerView(out FrameworkElement dragRegion)
    {
        TextBlock value = ValueText(24, TextAlignment.Center);
        value.HorizontalAlignment = HorizontalAlignment.Center;
        value.VerticalAlignment = VerticalAlignment.Center;
        _valueViews[TrayMetric.Power] = value;

        var widget = new Grid();
        widget.RowDefinitions.Add(new RowDefinition { Height = new GridLength(7) });
        widget.RowDefinitions.Add(new RowDefinition());

        dragRegion = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        widget.Children.Add(dragRegion);

        var valueButton = new Button
        {
            Content = value,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(WinUiRadii.Control),
            AllowFocusOnInteraction = false,
        };
        valueButton.Click += (_, _) => SetView(MonitorViewKind.Full);
        ToolTipService.SetToolTip(valueButton,
            $"{Loc.T("monitor.view")} · {Loc.T("panel.close")}");
        Grid.SetRow(valueButton, 1);
        widget.Children.Add(valueButton);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Margin = new Thickness(0, 0, 5, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        Button expand = ChromeButton("\uE740", Loc.T("monitor.view"), 22, 22, 10);
        expand.Click += (_, _) => SetView(MonitorViewKind.Full);
        actions.Children.Add(expand);
        Button close = ChromeButton(WindowChrome.CloseGlyph, Loc.T("panel.close"), 22, 22, 10);
        close.Click += (_, _) => Hide();
        actions.Children.Add(close);
        Grid.SetRow(actions, 1);
        widget.Children.Add(actions);

        widget.PointerEntered += (_, _) =>
        {
            actions.Visibility = Visibility.Visible;
            valueButton.HorizontalContentAlignment = HorizontalAlignment.Left;
            valueButton.Padding = new Thickness(15, 0, 52, 0);
        };
        widget.PointerExited += (_, _) =>
        {
            actions.Visibility = Visibility.Collapsed;
            valueButton.HorizontalContentAlignment = HorizontalAlignment.Center;
            valueButton.Padding = new Thickness(0);
        };
        widget.RightTapped += (_, e) =>
        {
            e.Handled = true;
            Hide();
        };
        return widget;
    }

    private static Button HeaderButton(string glyph, string tooltip) => ChromeButton(glyph, tooltip, 34, 32, 14);
    private static Button CompactButton(string glyph, string tooltip) => ChromeButton(glyph, tooltip, 28, 28, 12);

    private static Button ChromeButton(string glyph, string tooltip, double width, double height, double iconSize)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = iconSize },
            Width = width,
            Height = height,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(WinUiRadii.Control),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            AllowFocusOnInteraction = false,
        };
        ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    private static Line GuideLine() => new()
    {
        Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(28, 128, 128, 128)),
        StrokeThickness = 1,
        IsHitTestVisible = false,
    };

    private static TextBlock ValueText(double fontSize, TextAlignment alignment) => new()
    {
        Text = "—",
        FontSize = fontSize,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        TextAlignment = alignment,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private void ResetSamplingState()
    {
        foreach (List<float> points in _history.Values) points.Clear();
        _lastReading.Clear();
        lock (_samplingLock)
        {
            foreach (TrayMetricSource source in _sources.Values) source.Dispose();
            _sources.Clear();
            foreach (TrayMetric kind in DetailedMetrics) _sources[kind] = new TrayMetricSource(kind);
        }
    }

    private void StartSampling()
    {
        _sampling?.Cancel();
        _sampling?.Dispose();
        _sampling = new CancellationTokenSource();
        CancellationToken token = _sampling.Token;
        int version = Interlocked.Increment(ref _samplingVersion);
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                var sample = new Dictionary<TrayMetric, TrayMetricReading>();
                lock (_samplingLock)
                    foreach ((TrayMetric kind, TrayMetricSource source) in _sources)
                        sample[kind] = source.ReadDetailed();
                _queue.TryEnqueue(() =>
                {
                    if (version == Volatile.Read(ref _samplingVersion)) ApplySample(sample);
                });
                try { await Task.Delay(1000, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }, token);
    }

    private void ApplySample(IReadOnlyDictionary<TrayMetric, TrayMetricReading> sample)
    {
        if (!IsVisible) return;
        foreach ((TrayMetric kind, TrayMetricReading reading) in sample)
        {
            _lastReading[kind] = reading;
            List<float> points = _history[kind];
            points.Add(reading.Value);
            if (points.Count > HistoryCapacity) points.RemoveAt(0);
        }
        RefreshVisibleValues();
    }

    private void RefreshVisibleValues()
    {
        foreach ((TrayMetric kind, TextBlock text) in _valueViews)
        {
            TrayMetricReading reading = _lastReading.GetValueOrDefault(kind, TrayMetricReading.Missing);
            text.Text = _view == MonitorViewKind.Power && kind == TrayMetric.Power
                ? MonitorMetricFormat.PowerWidgetValue(reading.Value)
                : MonitorMetricFormat.Value(kind, reading.Value);
            text.Foreground = new SolidColorBrush(MetricColor(kind, reading.Value));
        }
        foreach ((TrayMetric kind, TextBlock text) in _detailViews)
        {
            TrayMetricReading reading = _lastReading.GetValueOrDefault(kind, TrayMetricReading.Missing);
            text.Text = MonitorMetricFormat.Detail(kind, reading);
        }
        foreach ((TrayMetric kind, GraphVisual graph) in _graphs)
        {
            Windows.UI.Color color = MetricColor(kind,
                _lastReading.GetValueOrDefault(kind, TrayMetricReading.Missing).Value);
            graph.Line.Stroke = new SolidColorBrush(color);
            graph.Area.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(48, color.R, color.G, color.B));
            UpdateGraph(kind, graph);
        }
    }

    private void UpdateGraph(TrayMetric kind, GraphVisual graph)
    {
        double width = graph.Canvas.ActualWidth;
        double height = graph.Canvas.ActualHeight;
        if (width <= 1 || height <= 1) return;

        for (int i = 0; i < 4; i++)
        {
            double y = height * (i + 1) / 5d;
            Line guide = graph.Guides[i];
            (guide.X1, guide.X2, guide.Y1, guide.Y2) = (0, width, y, y);
        }
        for (int i = 0; i < 5; i++)
        {
            double x = width * (i + 1) / 6d;
            Line guide = graph.Guides[4 + i];
            (guide.X1, guide.X2, guide.Y1, guide.Y2) = (x, x, 0, height);
        }

        graph.Line.Points.Clear();
        graph.Area.Points.Clear();
        List<float> samples = _history[kind];
        float maximum = MonitorMetricFormat.GraphMaximum(kind, samples);
        if (graph.Scale is not null) graph.Scale.Text = MonitorMetricFormat.Scale(kind, maximum);

        double xStep = width / Math.Max(1, HistoryCapacity - 1);
        double firstX = 0;
        double lastX = 0;
        bool any = false;
        for (int i = 0; i < samples.Count; i++)
        {
            float sample = samples[i];
            if (float.IsNaN(sample)) continue;
            double x = i * xStep;
            double normalized = Math.Clamp(
                kind == TrayMetric.Power ? Math.Abs(sample) / maximum : sample / maximum, 0d, 1d);
            var point = new Windows.Foundation.Point(x, (1d - normalized) * (height - 1));
            graph.Line.Points.Add(point);
            graph.Area.Points.Add(point);
            if (!any) firstX = x;
            lastX = x;
            any = true;
        }
        if (!any) return;
        graph.Area.Points.Insert(0, new Windows.Foundation.Point(firstX, height));
        graph.Area.Points.Add(new Windows.Foundation.Point(lastX, height));
    }

    private static string MetricLabel(TrayMetric kind) => kind switch
    {
        TrayMetric.Power => Loc.T("monitor.power"),
        TrayMetric.Temp => Loc.T("monitor.temp"),
        _ => kind.ToString().ToUpperInvariant(),
    };

    private static Windows.UI.Color MetricColor(TrayMetric kind, float value) => kind switch
    {
        TrayMetric.Power when !float.IsNaN(value) && value >= 0 => Windows.UI.Color.FromArgb(255, 64, 201, 125),
        TrayMetric.Power => Windows.UI.Color.FromArgb(255, 255, 153, 0),
        TrayMetric.Cpu => Windows.UI.Color.FromArgb(255, 78, 161, 255),
        TrayMetric.Gpu => Windows.UI.Color.FromArgb(255, 255, 213, 79),
        TrayMetric.Ram => Windows.UI.Color.FromArgb(255, 179, 157, 219),
        TrayMetric.Temp when !float.IsNaN(value) && value >= 88 => Windows.UI.Color.FromArgb(255, 206, 32, 62),
        TrayMetric.Temp => Windows.UI.Color.FromArgb(255, 255, 111, 97),
        _ => Windows.UI.Color.FromArgb(255, 160, 160, 164),
    };

    private static SolidColorBrush GraphBackgroundBrush() => new(FlyoutPalette.Dark
        ? Windows.UI.Color.FromArgb(82, 62, 64, 70)
        : Windows.UI.Color.FromArgb(134, 232, 235, 240));

    private void ApplyTheme()
    {
        _card.RequestedTheme = FlyoutPalette.RequestedTheme;
        _card.Background = new SolidColorBrush(FlyoutPalette.Card);
    }
}

internal enum MonitorViewKind { Full, Mini, Power }

internal static class MonitorLayout
{
    internal static MonitorViewKind Parse(string? value) => value?.ToLowerInvariant() switch
    {
        "mini" => MonitorViewKind.Mini,
        "power" => MonitorViewKind.Power,
        _ => MonitorViewKind.Full,
    };

    internal static string? ConfigValue(MonitorViewKind view) => view switch
    {
        MonitorViewKind.Mini => "mini",
        MonitorViewKind.Power => "power",
        _ => null,
    };

    internal static Size ViewSize(MonitorViewKind view) => view switch
    {
        MonitorViewKind.Mini => new Size(510, 64),
        MonitorViewKind.Power => new Size(136, 54),
        _ => new Size(460, 600),
    };
}

internal static class MonitorMetricFormat
{
    internal static string PowerWidgetValue(float value) =>
        float.IsNaN(value) ? "—" : $"{Math.Abs(value):0} W";

    internal static string Value(TrayMetric kind, float value)
    {
        if (float.IsNaN(value)) return "—";
        return kind switch
        {
            TrayMetric.Power => $"{Math.Abs(value):0.0} W",
            TrayMetric.Temp => $"{value:0} °C",
            _ => $"{value:0}%",
        };
    }

    internal static string Detail(TrayMetric kind, TrayMetricReading reading) => kind switch
    {
        TrayMetric.Gpu when !float.IsNaN(reading.Tertiary) && !float.IsNaN(reading.Secondary) =>
            $"{reading.Tertiary:0} MHz · {reading.Secondary:0.0} W",
        TrayMetric.Ram when !float.IsNaN(reading.Secondary) && !float.IsNaN(reading.Tertiary) =>
            $"{reading.Secondary:0.0} / {reading.Tertiary:0.0} GB",
        _ => "—",
    };

    internal static float GraphMaximum(TrayMetric kind, IEnumerable<float> samples)
    {
        if (kind != TrayMetric.Power) return 100f;
        float observed = samples.Where(float.IsFinite).Select(Math.Abs).DefaultIfEmpty(0f).Max();
        return Math.Max(25f, MathF.Ceiling(observed / 5f) * 5f);
    }

    internal static string Scale(TrayMetric kind, float maximum) => kind switch
    {
        TrayMetric.Power => $"{maximum:0} W",
        TrayMetric.Temp => "100 °C",
        _ => string.Empty,
    };
}

internal static class MonitorPlacement
{
    internal static Point Center(System.Drawing.Rectangle work, Size window) => new(
        work.Left + Math.Max(0, (work.Width - window.Width) / 2),
        work.Top + Math.Max(0, (work.Height - window.Height) / 2));
}
